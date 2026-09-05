// <copyright file="ShopSelectCallbackHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ProductTrackerBot.Handlers;

using Microsoft.Extensions.Logging;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

/// <summary>
/// Handles taps on a /list item name — expands that row into its "bought" and "remove" actions, or
/// collapses it again (item ID 0). Nothing is written; only the list message is re-rendered.
/// </summary>
public class ShopSelectCallbackHandler : ICallbackHandler
{
    private readonly ITelegramBotClient botClient;
    private readonly ShoppingListService listService;
    private readonly GroupRepository groupRepository;
    private readonly ILogger<ShopSelectCallbackHandler> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShopSelectCallbackHandler"/> class.
    /// </summary>
    /// <param name="botClient">The Telegram bot client.</param>
    /// <param name="listService">The shopping list service.</param>
    /// <param name="groupRepository">The group repository.</param>
    /// <param name="logger">The logger.</param>
    public ShopSelectCallbackHandler(
        ITelegramBotClient botClient,
        ShoppingListService listService,
        GroupRepository groupRepository,
        ILogger<ShopSelectCallbackHandler> logger)
    {
        this.botClient = botClient;
        this.listService = listService;
        this.groupRepository = groupRepository;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public string CallbackPrefix => "shop:sel:";

    /// <inheritdoc/>
    public async Task HandleAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (callbackQuery.Data is null || callbackQuery.Message is null)
        {
            return;
        }

        if (!ListViewContext.TryParse(callbackQuery.Data, this.CallbackPrefix, out var itemId, out var context))
        {
            this.logger.LogWarning("Invalid data in shop:sel callback: {Data}", callbackQuery.Data);
            return;
        }

        var chatId = callbackQuery.Message.Chat.Id;
        var group = await this.groupRepository.GetOrCreateAsync(chatId);
        var tagNames = await this.listService.ResolveTagNamesAsync(group.Id, context.TagIndices);

        var (messageText, keyboard, _) = await this.listService.BuildListAsync(
            chatId,
            context.PageNumber,
            tagNames,
            context.TagPageNumber,
            itemId == 0 ? null : itemId);

        try
        {
            await this.botClient.EditMessageText(
                chatId: chatId,
                messageId: callbackQuery.Message.MessageId,
                text: messageText,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // 400 covers both "message gone" and "message is not modified" (e.g. double-tapping the same
            // row). Only the former warrants a fresh message; resending on a no-op would duplicate the list.
            if (!ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
            {
                var sent = await this.botClient.SendMessage(
                    chatId: chatId,
                    text: messageText,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);

                await this.groupRepository.UpdateListMessageIdAsync(group.Id, sent.MessageId);
            }
        }

        await this.botClient.AnswerCallbackQuery(
            callbackQueryId: callbackQuery.Id,
            cancellationToken: cancellationToken);
    }
}
