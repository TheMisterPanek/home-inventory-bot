// <copyright file="ShopDoneCallbackHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ProductTrackerBot.Handlers;

using Microsoft.Extensions.Logging;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

/// <summary>
/// Handles the "✓" buy button — marks an item as bought and deletes it silently: no confirmation message
/// and no follow-up prompts, just the list message re-rendered in the same view (page and tag filters kept).
/// </summary>
public class ShopDoneCallbackHandler : ICallbackHandler
{
    private readonly ITelegramBotClient botClient;
    private readonly ShoppingItemRepository itemRepository;
    private readonly ShoppingListService listService;
    private readonly GroupRepository groupRepository;
    private readonly IHistoryRepository historyRepository;
    private readonly ILogger<ShopDoneCallbackHandler> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShopDoneCallbackHandler"/> class.
    /// </summary>
    /// <param name="botClient">The Telegram bot client.</param>
    /// <param name="itemRepository">The shopping item repository.</param>
    /// <param name="listService">The shopping list service.</param>
    /// <param name="groupRepository">The group repository.</param>
    /// <param name="historyRepository">The history repository.</param>
    /// <param name="logger">The logger.</param>
    public ShopDoneCallbackHandler(
        ITelegramBotClient botClient,
        ShoppingItemRepository itemRepository,
        ShoppingListService listService,
        GroupRepository groupRepository,
        IHistoryRepository historyRepository,
        ILogger<ShopDoneCallbackHandler> logger)
    {
        this.botClient = botClient;
        this.itemRepository = itemRepository;
        this.listService = listService;
        this.groupRepository = groupRepository;
        this.historyRepository = historyRepository;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public string CallbackPrefix => "shop:done:";

    /// <inheritdoc/>
    public async Task HandleAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (callbackQuery.Data is null || callbackQuery.Message is null)
        {
            return;
        }

        if (!ListViewContext.TryParse(callbackQuery.Data, this.CallbackPrefix, out var itemId, out var context))
        {
            this.logger.LogWarning("Invalid data in shop:done callback: {Data}", callbackQuery.Data);
            return;
        }

        // Read item details before deleting
        var item = await this.itemRepository.GetByIdAsync(itemId);
        if (item is null)
        {
            return;
        }

        // Delete the item
        await this.itemRepository.DeleteAsync(itemId);

        // Rebuild and update the list message in the same view the button was tapped from
        var chatId = callbackQuery.Message.Chat.Id;
        await this.UpdateListMessageAsync(chatId, callbackQuery.Message.MessageId, context, cancellationToken);

        await this.botClient.AnswerCallbackQuery(
            callbackQueryId: callbackQuery.Id,
            cancellationToken: cancellationToken);

        var displayName = callbackQuery.From.FirstName;

        try
        {
            var buttonText = item.Quantity is not null
                ? $"{item.Name} {item.Quantity}"
                : item.Name;
            var payload = new ItemPayload(buttonText, null);
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, BotActionPayloadContext.Default.ItemPayload);
            var revertPayload = new ItemBoughtRevert(item.Id, item.Name, item.Quantity, item.GroupId);
            var revertPayloadJson = System.Text.Json.JsonSerializer.Serialize(revertPayload, BotActionPayloadContext.Default.ItemBoughtRevert);
            await this.historyRepository.RecordAsync(
                chatId: chatId,
                userId: callbackQuery.From.Id,
                userName: displayName ?? "Unknown",
                actionType: BotActionType.ItemBought,
                payloadJson: payloadJson,
                revertPayloadJson: revertPayloadJson,
                ct: cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to record history for ItemBought");
        }
    }

    private async Task UpdateListMessageAsync(long chatId, int messageId, ListViewContext context, CancellationToken cancellationToken)
    {
        var group = await this.groupRepository.GetOrCreateAsync(chatId);
        var tagNames = await this.listService.ResolveTagNamesAsync(group.Id, context.TagIndices);
        var (messageText, keyboard, _) = await this.listService.BuildListAsync(chatId, context.PageNumber, tagNames, context.TagPageNumber);

        try
        {
            await this.botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: messageText,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // Post a new message if edit fails
            var sent = await this.botClient.SendMessage(
                chatId: chatId,
                text: messageText,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);

            await this.groupRepository.UpdateListMessageIdAsync(group.Id, sent.MessageId);
        }
    }
}
