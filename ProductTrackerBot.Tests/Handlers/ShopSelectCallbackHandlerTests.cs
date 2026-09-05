using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using ProductTrackerBot.Handlers;
using ProductTrackerBot.Localization;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace ProductTrackerBot.Tests.Handlers;

public class ShopSelectCallbackHandlerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static CallbackQuery CreateCallbackQuery(string callbackData) =>
        JsonSerializer.Deserialize<CallbackQuery>(
            $"{{\"id\":\"cb1\",\"from\":{{\"id\":42,\"first_name\":\"Alice\"}},\"chat_instance\":\"123\",\"message\":{{\"message_id\":1,\"chat\":{{\"id\":-100}},\"text\":\"Shopping list\"}},\"data\":\"{callbackData}\"}}",
            JsonOpts)!;

    private static Mock<ITelegramBotClient> CreateBotMock()
    {
        var botMock = new Mock<ITelegramBotClient>();
        botMock.Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock.Setup(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return botMock;
    }

    private static (ShopSelectCallbackHandler Handler, Mock<ShoppingItemRepository> ItemRepo) CreateHandler(
        ITelegramBotClient bot,
        IReadOnlyList<ShoppingItem> items,
        IReadOnlyList<string>? tags = null)
    {
        var groupRepo = new Mock<GroupRepository>("Data Source=file::memory:");
        groupRepo.Setup(r => r.GetOrCreateAsync(-100L)).ReturnsAsync(new Group { Id = 10, ChatId = -100L });
        groupRepo.Setup(r => r.UpdateListMessageIdAsync(It.IsAny<int>(), It.IsAny<int>())).Returns(Task.CompletedTask);

        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:");
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items);

        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(tags ?? Array.Empty<string>());

        var localizer = new Mock<ILocalizer>();
        localizer.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>())).Returns((long _, string key) => key);

        var listService = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, localizer.Object);
        var handler = new ShopSelectCallbackHandler(
            bot, listService, groupRepo.Object, Mock.Of<ILogger<ShopSelectCallbackHandler>>());
        return (handler, itemRepo);
    }

    [Fact]
    public async Task TappingItem_EditsListWithExpandedActionRow()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Alice" },
        };
        var (handler, itemRepo) = CreateHandler(bot.Object, items);

        await handler.HandleAsync(CreateCallbackQuery("shop:sel:1:1::1"), CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r =>
                    r.ReplyMarkup!.InlineKeyboard.First().Count() == 3
                    && r.ReplyMarkup!.InlineKeyboard.First().Any(btn => btn.CallbackData == "shop:done:1:1::1")
                    && r.ReplyMarkup!.InlineKeyboard.First().Any(btn => btn.CallbackData == "shop:remove:1:1::1")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Selecting is display-only — nothing is deleted
        itemRepo.Verify(r => r.DeleteAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ItemIdZero_CollapsesEveryRow()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Alice" },
        };
        var (handler, _) = CreateHandler(bot.Object, items);

        await handler.HandleAsync(CreateCallbackQuery("shop:sel:0:1::1"), CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r =>
                    r.ReplyMarkup!.InlineKeyboard.First().Count() == 1
                    && r.ReplyMarkup!.InlineKeyboard.First().First().CallbackData == "shop:sel:1:1::1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TappingItem_InFilteredView_KeepsFilterApplied()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Белизна", AddedByName = "Alice", Tags = new[] { "Химия" } },
            new() { Id = 2, GroupId = 10, Name = "Молоко", AddedByName = "Alice", Tags = new[] { "Еда" } },
        };
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Еда", "Химия" });

        // Tag index 1 = "Химия"
        await handler.HandleAsync(CreateCallbackQuery("shop:sel:1:1:1:1"), CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r =>
                    r.Text.Contains("Белизна")
                    && !r.Text.Contains("Молоко")
                    && r.ReplyMarkup!.InlineKeyboard.First().Any(btn => btn.CallbackData == "shop:done:1:1:1:1")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MalformedCallback_IsIgnored()
    {
        var bot = CreateBotMock();
        var (handler, _) = CreateHandler(bot.Object, new List<ShoppingItem>());

        await handler.HandleAsync(CreateCallbackQuery("shop:sel:abc"), CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
