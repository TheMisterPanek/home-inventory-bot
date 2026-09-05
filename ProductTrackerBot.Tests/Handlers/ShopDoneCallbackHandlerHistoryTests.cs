using ProductTrackerBot.Localization;
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

public class ShopDoneCallbackHandlerHistoryTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static CallbackQuery DeserializeCallbackQuery(string json) =>
        JsonSerializer.Deserialize<CallbackQuery>(json, JsonOpts)!;

    private static Mock<ITelegramBotClient> CreateBotMock()
    {
        var botMock = new Mock<ITelegramBotClient>();
        botMock.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock.Setup(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        botMock.Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        return botMock;
    }

    private static (ShopDoneCallbackHandler Handler, Mock<IHistoryRepository> HistoryMock, Mock<ITelegramBotClient> Bot) CreateFullHandler(
        ShoppingItem item,
        bool historyThrows = false)
    {
        var bot = CreateBotMock();

        var groupRepo = new Mock<GroupRepository>("Data Source=file::memory:");
        groupRepo.Setup(r => r.GetOrCreateAsync(-100L))
            .ReturnsAsync(new Group { Id = 10, ChatId = -100L });
        groupRepo.Setup(r => r.UpdateListMessageIdAsync(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:");
        itemRepo.Setup(r => r.GetByIdAsync(item.Id)).ReturnsAsync(item);
        itemRepo.Setup(r => r.DeleteAsync(item.Id)).Returns(Task.CompletedTask);
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(new List<ShoppingItem>().AsReadOnly());

        var localizer = CreateLocalizerMock();
        var listService = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var historyMock = new Mock<IHistoryRepository>();
        var setup = historyMock.Setup(h => h.RecordAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<BotActionType>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()));

        if (historyThrows)
            setup.ThrowsAsync(new InvalidOperationException("DB error"));
        else
            setup.Returns(Task.CompletedTask);

        var handler = new ShopDoneCallbackHandler(
            bot.Object,
            itemRepo.Object,
            listService,
            groupRepo.Object,
            historyMock.Object,
            Mock.Of<ILogger<ShopDoneCallbackHandler>>());

        return (handler, historyMock, bot);
    }

    [Fact]
    public async Task Calls_RecordAsync_With_ItemBought()
    {
        var item = new ShoppingItem { Id = 5, GroupId = 10, Name = "Молоко", Quantity = "2л", AddedByName = "Alice" };
        var (handler, historyMock, _) = CreateFullHandler(item);

        var callbackQuery = DeserializeCallbackQuery(
            "{\"id\":\"cb1\",\"from\":{\"id\":42,\"first_name\":\"Alice\"}," +
            "\"message\":{\"message_id\":10,\"chat\":{\"id\":-100}}," +
            "\"data\":\"shop:done:5\"}");

        await handler.HandleAsync(callbackQuery, CancellationToken.None);

        historyMock.Verify(
            h => h.RecordAsync(-100L, 42L, "Alice", BotActionType.ItemBought, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Swallows_RecordAsync_Failure()
    {
        var item = new ShoppingItem { Id = 6, GroupId = 10, Name = "Хлеб", AddedByName = "Alice" };
        var (handler, _, _) = CreateFullHandler(item, historyThrows: true);

        var callbackQuery = DeserializeCallbackQuery(
            "{\"id\":\"cb2\",\"from\":{\"id\":42,\"first_name\":\"Alice\"}," +
            "\"message\":{\"message_id\":10,\"chat\":{\"id\":-100}}," +
            "\"data\":\"shop:done:6\"}");

        await handler.HandleAsync(callbackQuery, CancellationToken.None);
    }

    [Fact]
    public async Task Passes_ItemBoughtRevert_Payload_With_Item_Details()
    {
        var item = new ShoppingItem { Id = 5, GroupId = 10, Name = "Молоко", Quantity = "2л", AddedByName = "Alice" };
        var (handler, historyMock, _) = CreateFullHandler(item);
        string? capturedRevert = null;
        historyMock.Setup(h => h.RecordAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<BotActionType>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<long, long, string, BotActionType, string, string?, CancellationToken>(
                (_, _, _, _, _, revert, _) => capturedRevert = revert)
            .Returns(Task.CompletedTask);

        var callbackQuery = DeserializeCallbackQuery(
            "{\"id\":\"cb1\",\"from\":{\"id\":42,\"first_name\":\"Alice\"}," +
            "\"message\":{\"message_id\":10,\"chat\":{\"id\":-100}}," +
            "\"data\":\"shop:done:5\"}");

        await handler.HandleAsync(callbackQuery, CancellationToken.None);

        Assert.NotNull(capturedRevert);
        var revert = System.Text.Json.JsonSerializer.Deserialize<ItemBoughtRevert>(capturedRevert!, BotActionPayloadContext.Default.ItemBoughtRevert);
        Assert.NotNull(revert);
        Assert.Equal(5, revert!.ItemId);
        Assert.Equal("Молоко", revert.ItemName);
        Assert.Equal("2л", revert.Quantity);
        Assert.Equal(10, revert.ListId);
    }

    private static Mock<ILocalizer> CreateLocalizerMock()
    {
        var mock = new Mock<ILocalizer>();
        mock.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>()))
            .Returns((long _, string key) => key);
        return mock;
    }
}
