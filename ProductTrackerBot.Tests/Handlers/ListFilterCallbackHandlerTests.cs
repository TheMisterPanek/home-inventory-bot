using ProductTrackerBot.Localization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using ProductTrackerBot.Handlers;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace ProductTrackerBot.Tests.Handlers;

public class ListFilterCallbackHandlerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static CallbackQuery DeserializeCallbackQuery(string json) =>
        JsonSerializer.Deserialize<CallbackQuery>(json, JsonOpts)!;

    private static CallbackQuery CreateCallbackQuery(string callbackData) =>
        DeserializeCallbackQuery($"{{\"id\":\"cb1\",\"from\":{{\"id\":42,\"first_name\":\"Alice\"}},\"chat_instance\":\"123\",\"message\":{{\"message_id\":1,\"chat\":{{\"id\":-100}},\"text\":\"Shopping list\"}},\"data\":\"{callbackData}\"}}");

    private static Mock<ITelegramBotClient> CreateBotMock()
    {
        var botMock = new Mock<ITelegramBotClient>();
        botMock
            .Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock
            .Setup(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return botMock;
    }

    private static (ListFilterCallbackHandler Handler, Mock<ShoppingItemRepository> ItemRepo) CreateHandler(
        ITelegramBotClient bot,
        IReadOnlyList<ShoppingItem> items,
        IReadOnlyList<string> tags)
    {
        var groupRepo = new Mock<GroupRepository>("Data Source=file::memory:");
        groupRepo.Setup(r => r.GetOrCreateAsync(-100L))
            .ReturnsAsync(new Group { Id = 10, ChatId = -100L });
        groupRepo.Setup(r => r.UpdateListMessageIdAsync(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:");
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items);

        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(tags);

        var localizer = CreateLocalizerMock();
        var listService = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, localizer.Object);
        var historyRepo = new Mock<IHistoryRepository>();
        var handler = new ListFilterCallbackHandler(
            bot,
            listService,
            groupRepo.Object,
            tagRepo.Object,
            historyRepo.Object,
            Mock.Of<ILogger<ListFilterCallbackHandler>>());
        return (handler, itemRepo);
    }

    [Fact]
    public async Task ValidTagTap_FiltersToMatchingItemsOnly()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Порошок", AddedByName = "Ivan", Tags = new[] { "Химия" } },
            new() { Id = 2, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
        };
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Еда", "Химия" });
        var callback = CreateCallbackQuery("list_filter:-100:1:1:1");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r => r.Text.Contains("Порошок") && !r.Text.Contains("Молоко")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        bot.Verify(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllButtonTap_ClearsFilter_ShowsAllItems()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Порошок", AddedByName = "Ivan", Tags = new[] { "Химия" } },
            new() { Id = 2, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
        };
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Еда", "Химия" });
        var callback = CreateCallbackQuery("list_filter:-100:-1:1:1");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r => r.Text.Contains("Порошок") && r.Text.Contains("Молоко")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StaleTagIndex_FallsBackToAllView()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
        };

        // Tag list shrank since the message was rendered — index 3 no longer resolves.
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Еда" });
        var callback = CreateCallbackQuery("list_filter:-100:3:1:1");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r => r.Text.Contains("Молоко")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MultipleActiveTags_UnionsMatches()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Порошок", AddedByName = "Ivan", Tags = new[] { "Химия" } },
            new() { Id = 2, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
            new() { Id = 3, GroupId = 10, Name = "Ключи", AddedByName = "Ivan", Tags = new[] { "Авто" } },
        };
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Авто", "Еда", "Химия" });
        var callback = CreateCallbackQuery("list_filter:-100:1,2:1:1");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r =>
                    r.Text.Contains("Молоко") && r.Text.Contains("Порошок") && !r.Text.Contains("Ключи")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FilteredView_PaginatesWithinTag()
    {
        var bot = CreateBotMock();
        var itemCount = ShoppingListService.ActionPageSize * 2;
        var items = Enumerable.Range(1, itemCount)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Товар{i}", AddedByName = "Ivan", Tags = new[] { "Химия" } })
            .ToList();
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Химия" });
        // itemCount items at ActionPageSize per page → exactly 2 pages; page 2 holds the last ActionPageSize items.
        var callback = CreateCallbackQuery("list_filter:-100:0:2:1");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r =>
                    r.ReplyMarkup!.InlineKeyboard.Count(row => row.Any(btn => btn.CallbackData!.StartsWith("shop:sel:"))) == ShoppingListService.ActionPageSize
                    && r.ReplyMarkup!.InlineKeyboard.Any(row => row.Any(btn => btn.Text == $"Товар{itemCount - 1}"))
                    && r.ReplyMarkup!.InlineKeyboard.Any(row => row.Any(btn => btn.Text == $"Товар{itemCount}"))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // TDD — bug #2: re-tapping the currently active filter produces identical content, so Telegram
    // returns 400 "message is not modified". The handler must NOT treat that as "message gone" and
    // resend the whole list, otherwise every no-op tap spams a duplicate list message.
    [Fact]
    public async Task NotModifiedEdit_DoesNotResendDuplicateMessage()
    {
        var bot = CreateBotMock();
        bot
            .Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiRequestException("Bad Request: message is not modified", 400));

        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Порошок", AddedByName = "Ivan", Tags = new[] { "Химия" } },
        };
        var (handler, _) = CreateHandler(bot.Object, items, new[] { "Химия" });
        var callback = CreateCallbackQuery("list_filter:-100:0:1:1");

        await handler.HandleAsync(callback, CancellationToken.None);

        // No duplicate list posted; the callback is still answered so the spinner clears.
        bot.Verify(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        bot.Verify(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TogglingTag_OnTagPage2_PreservesTagPage2InResponse()
    {
        var bot = CreateBotMock();
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Tag7" } },
        };
        var tags = Enumerable.Range(1, 8).Select(i => $"Tag{i}").ToList();
        var (handler, _) = CreateHandler(bot.Object, items, tags);

        // Tap toggles tag index 6 ("Tag7") on, while currently viewing tag page 2.
        var callback = CreateCallbackQuery("list_filter:-100:6:1:2");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(
            b => b.SendRequest(
                It.Is<EditMessageTextRequest>(r =>
                    r.ReplyMarkup!.InlineKeyboard.SelectMany(row => row).Any(btn => btn.Text == "✓ Tag7")
                    && r.ReplyMarkup!.InlineKeyboard.SelectMany(row => row).Any(btn => btn.Text == "Tag8")
                    && !r.ReplyMarkup!.InlineKeyboard.SelectMany(row => row).Any(btn => btn.Text == "Tag1")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvalidData_IgnoresCallback()
    {
        var bot = CreateBotMock();
        var (handler, _) = CreateHandler(bot.Object, new List<ShoppingItem>(), Array.Empty<string>());
        var callback = CreateCallbackQuery("list_filter:invalid");

        await handler.HandleAsync(callback, CancellationToken.None);

        bot.Verify(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<ILocalizer> CreateLocalizerMock()
    {
        var mock = new Mock<ILocalizer>();
        mock.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>()))
            .Returns((long _, string key) => key);
        return mock;
    }
}
