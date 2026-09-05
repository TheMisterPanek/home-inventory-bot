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
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace ProductTrackerBot.Tests.Handlers;

public class TagCaptureDialogTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static Message DeserializeMessage(string json) =>
        JsonSerializer.Deserialize<Message>(json, JsonOpts)!;

    private static Mock<ITelegramBotClient> CreateBotMock()
    {
        var botMock = new Mock<ITelegramBotClient>();
        botMock.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock.Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock.Setup(b => b.SendRequest(It.IsAny<EditMessageReplyMarkupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        botMock.Setup(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return botMock;
    }

    private static Mock<ShoppingListService> CreateListServiceMock()
    {
        var groupRepo = new Mock<GroupRepository>("Data Source=file::memory:");
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:");
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        var localizer = Mock.Of<ILocalizer>();
        var listServiceMock = new Mock<ShoppingListService>(groupRepo.Object, itemRepo.Object, tagRepo.Object, localizer);
        listServiceMock
            .Setup(s => s.BuildListAsync(It.IsAny<long>(), It.IsAny<int>(), null, It.IsAny<int>(), It.IsAny<int?>()))
            .ReturnsAsync(("list", null, new Group { Id = 10, ChatId = -100L }));
        return listServiceMock;
    }

    private static Mock<ILocalizer> CreateLocalizerMock()
    {
        var localizerMock = new Mock<ILocalizer>();
        localizerMock.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>()))
            .Returns((long _, string key) => key);
        localizerMock.Setup(l => l.Get(It.IsAny<long>(), "tag.prompt"))
            .Returns("Какими тегами пометить {item}?");
        localizerMock.Setup(l => l.Get(It.IsAny<long>(), "tag.set-confirmation"))
            .Returns("✓ Теги «{tags}» установлены");
        localizerMock.Setup(l => l.Get(It.IsAny<long>(), "tag.free-text-added"))
            .Returns("✓ Тег «{tag}» добавлен");
        return localizerMock;
    }

    private static CallbackQuery CreateCallback(string data, long chatId = -100, int userId = 42, string firstName = "Alice")
    {
        return new CallbackQuery
        {
            Id = "cb1",
            Data = data,
            From = new User { Id = userId, FirstName = firstName },
            Message = DeserializeMessage(
                "{\"message_id\":1,\"chat\":{\"id\":" + chatId + ",\"type\":\"supergroup\"}}"),
        };
    }

    [Fact]
    public async Task TagCaptureService_With_Suggestions_Sends_Prompt_With_Toggle_Skip_Done_Buttons()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.GetTopTagsAsync(10, 5))
            .ReturnsAsync(new List<string> { "Химия", "Авто" });

        var service = new TagCaptureService(bot.Object, dialogService, new PendingDialogService<PriceCaptureDialogState>(), tagRepo.Object, CreateLocalizerMock().Object);

        await service.StartTagCaptureAsync(-100L, 42L, 10, new[] { 1 }, "Молоко", preselectedTags: null, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Equal(new List<int> { 1 }, state!.ItemIds);
        Assert.Equal("Молоко", state.ItemLabel);
        Assert.Equal(10, state.GroupId);
        Assert.NotNull(state.TopTags);
        Assert.Equal(2, state.TopTags!.Count);
        Assert.Empty(state.SelectedTagNames);

        bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r =>
                r.Text!.Contains("Молоко") &&
                r.ReplyMarkup != null &&
                ((InlineKeyboardMarkup?)r.ReplyMarkup)!.InlineKeyboard.Count() == 4), // 2 suggestions + skip + done
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TagCaptureService_StartTagCaptureAsync_Clears_Stale_PriceCaptureDialog()
    {
        // Regression test: a leftover price-capture dialog (e.g. from marking a previous item
        // bought and not finishing the store/price/expiry steps) must not survive into a newly
        // started tag-capture prompt — otherwise the dispatcher routes the user's next reply to
        // PriceCaptureStepHandler (registered before TagCaptureStepHandler) instead of the tag
        // prompt, producing a spurious "That doesn't look like a price" reply.
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        var priceDialogService = new PendingDialogService<PriceCaptureDialogState>();
        priceDialogService.SetState(-100L, 42L, new PriceCaptureDialogState { Step = 1, ItemName = "Milk" });

        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.GetTopTagsAsync(10, 5))
            .ReturnsAsync(new List<string>());

        var service = new TagCaptureService(bot.Object, dialogService, priceDialogService, tagRepo.Object, CreateLocalizerMock().Object);

        await service.StartTagCaptureAsync(-100L, 42L, 10, new[] { 1 }, "Кола", preselectedTags: null, CancellationToken.None);

        Assert.Null(priceDialogService.GetState(-100L, 42L));
        Assert.NotNull(dialogService.GetState(-100L, 42L));
    }

    [Fact]
    public async Task TagCaptureService_With_No_Suggestions_Sends_Prompt_With_Skip_And_Done_Only()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.GetTopTagsAsync(10, 5))
            .ReturnsAsync(new List<string>());

        var service = new TagCaptureService(bot.Object, dialogService, new PendingDialogService<PriceCaptureDialogState>(), tagRepo.Object, CreateLocalizerMock().Object);

        await service.StartTagCaptureAsync(-100L, 42L, 10, new[] { 1 }, "Молоко", preselectedTags: null, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Null(state!.TopTags);

        bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r =>
                r.ReplyMarkup != null &&
                ((InlineKeyboardMarkup?)r.ReplyMarkup)!.InlineKeyboard.Count() == 2), // skip + done
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TagCaptureService_Bulk_Stores_All_ItemIds()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.GetTopTagsAsync(10, 5))
            .ReturnsAsync(new List<string>());

        var service = new TagCaptureService(bot.Object, dialogService, new PendingDialogService<PriceCaptureDialogState>(), tagRepo.Object, CreateLocalizerMock().Object);

        await service.StartTagCaptureAsync(-100L, 42L, 10, new[] { 1, 2, 3 }, "3 товара", preselectedTags: null, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Equal(new List<int> { 1, 2, 3 }, state!.ItemIds);
        Assert.Equal("3 товара", state.ItemLabel);
    }

    [Fact]
    public async Task TagCaptureService_PreselectedTags_Are_Marked_Selected()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.GetTopTagsAsync(10, 5))
            .ReturnsAsync(new List<string> { "Химия", "Авто" });

        var service = new TagCaptureService(bot.Object, dialogService, new PendingDialogService<PriceCaptureDialogState>(), tagRepo.Object, CreateLocalizerMock().Object);

        await service.StartTagCaptureAsync(-100L, 42L, 10, new[] { 1 }, "Bread", preselectedTags: new[] { "Химия" }, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Contains("Химия", state!.SelectedTagNames);

        bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r =>
                ((InlineKeyboardMarkup?)r.ReplyMarkup)!.InlineKeyboard.Any(row => row.Any(btn => btn.Text.Contains("✓ Химия")))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TagToggleCallbackHandler_TogglesTagOn_KeepsDialogOpen()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Молоко",
            GroupId = 10,
            TopTags = new List<string> { "Молочка", "Химия" },
        });

        var handler = new TagToggleCallbackHandler(
            bot.Object, dialogService, CreateLocalizerMock().Object, Mock.Of<ILogger<TagToggleCallbackHandler>>());

        var callback = CreateCallback("tag:toggle:0");

        await handler.HandleAsync(callback, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Contains("Молочка", state!.SelectedTagNames);

        bot.Verify(b => b.SendRequest(It.IsAny<EditMessageReplyMarkupRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TagToggleCallbackHandler_TogglingTwice_RemovesTag()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Молоко",
            GroupId = 10,
            TopTags = new List<string> { "Молочка" },
        });

        var handler = new TagToggleCallbackHandler(
            bot.Object, dialogService, CreateLocalizerMock().Object, Mock.Of<ILogger<TagToggleCallbackHandler>>());

        await handler.HandleAsync(CreateCallback("tag:toggle:0"), CancellationToken.None);
        await handler.HandleAsync(CreateCallback("tag:toggle:0"), CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.DoesNotContain("Молочка", state!.SelectedTagNames);
    }

    [Fact]
    public async Task TagToggleCallbackHandler_Invalid_Index_Does_Not_Toggle()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Молоко",
            GroupId = 10,
            TopTags = new List<string> { "Молочка" },
        });

        var handler = new TagToggleCallbackHandler(
            bot.Object, dialogService, CreateLocalizerMock().Object, Mock.Of<ILogger<TagToggleCallbackHandler>>());

        await handler.HandleAsync(CreateCallback("tag:toggle:5"), CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Empty(state!.SelectedTagNames);
    }

    [Fact]
    public async Task TagDoneCallbackHandler_Applies_SelectedTags_And_Clears_State()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Молоко",
            GroupId = 10,
            SelectedTagNames = new HashSet<string> { "Молочка", "Скидка" },
        });

        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.SetItemTagsAsync(It.IsAny<IReadOnlyList<int>>(), 10, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(Task.CompletedTask);
        var listService = CreateListServiceMock();

        var handler = new TagDoneCallbackHandler(
            bot.Object, dialogService, tagRepo.Object, listService.Object, CreateLocalizerMock().Object,
            Mock.Of<ILogger<TagDoneCallbackHandler>>());

        await handler.HandleAsync(CreateCallback("tag:done"), CancellationToken.None);

        tagRepo.Verify(r => r.SetItemTagsAsync(
            It.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 1),
            10,
            It.Is<IReadOnlyCollection<string>>(t => t.Count == 2 && t.Contains("Молочка") && t.Contains("Скидка"))),
            Times.Once);
        Assert.Null(dialogService.GetState(-100L, 42L));

        listService.Verify(s => s.BuildListAsync(-100L, 1, null, It.IsAny<int>(), It.IsAny<int?>()), Times.Once);
        bot.Verify(
            b => b.SendRequest(
                It.Is<SendMessageRequest>(r => r.ChatId! == -100L && r.Text == "list"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TagDoneCallbackHandler_With_No_Selection_Applies_Empty_Set()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Молоко",
            GroupId = 10,
        });

        var tagRepo = new Mock<TagRepository>("Data Source=file:test");
        tagRepo.Setup(r => r.SetItemTagsAsync(It.IsAny<IReadOnlyList<int>>(), 10, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(Task.CompletedTask);

        var handler = new TagDoneCallbackHandler(
            bot.Object, dialogService, tagRepo.Object, CreateListServiceMock().Object, CreateLocalizerMock().Object,
            Mock.Of<ILogger<TagDoneCallbackHandler>>());

        await handler.HandleAsync(CreateCallback("tag:done"), CancellationToken.None);

        tagRepo.Verify(r => r.SetItemTagsAsync(
            It.IsAny<IReadOnlyList<int>>(), 10, It.Is<IReadOnlyCollection<string>>(t => t.Count == 0)), Times.Once);
        Assert.Null(dialogService.GetState(-100L, 42L));
    }

    [Fact]
    public async Task TagSkipCallbackHandler_Clears_State_Without_Applying_Tags()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Молоко",
            GroupId = 10,
            SelectedTagNames = new HashSet<string> { "Химия" },
        });

        var listService = CreateListServiceMock();
        var handler = new TagSkipCallbackHandler(
            bot.Object, dialogService, listService.Object, CreateLocalizerMock().Object,
            Mock.Of<ILogger<TagSkipCallbackHandler>>());

        await handler.HandleAsync(CreateCallback("tag:skip"), CancellationToken.None);

        Assert.Null(dialogService.GetState(-100L, 42L));
        bot.Verify(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        listService.Verify(s => s.BuildListAsync(-100L, 1, null, It.IsAny<int>(), It.IsAny<int?>()), Times.Once);
        bot.Verify(
            b => b.SendRequest(
                It.Is<SendMessageRequest>(r => r.ChatId! == -100L && r.Text == "list"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void TagCaptureStepHandler_CanHandle_Returns_True_When_Pending_State()
    {
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState { ItemIds = new List<int> { 1 }, GroupId = 10 });

        var handler = new TagCaptureStepHandler(CreateBotMock().Object, dialogService, CreateLocalizerMock().Object);

        Assert.True(handler.CanHandle(-100L, 42L));
        Assert.False(handler.CanHandle(-100L, 99L));
    }

    [Fact]
    public async Task TagCaptureStepHandler_FreeText_Adds_Tag_Alongside_Existing_Selection()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Порошок",
            GroupId = 10,
            TopTags = new List<string> { "Скидка" },
            SelectedTagNames = new HashSet<string> { "Бытовая химия" },
        });

        var handler = new TagCaptureStepHandler(bot.Object, dialogService, CreateLocalizerMock().Object);

        var message = DeserializeMessage(
            "{\"message_id\":10,\"from\":{\"id\":42,\"first_name\":\"Alice\"},\"chat\":{\"id\":-100,\"type\":\"supergroup\"},\"text\":\"Скидка\"}");

        await handler.HandleAsync(message, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Contains("Бытовая химия", state!.SelectedTagNames);
        Assert.Contains("Скидка", state.SelectedTagNames);

        bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text!.Contains("Порошок")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TagCaptureStepHandler_FreeText_Not_Among_Suggestions_Sends_Acknowledgement()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1 },
            ItemLabel = "Порошок",
            GroupId = 10,
            TopTags = new List<string> { "Бытовая химия" },
        });

        var handler = new TagCaptureStepHandler(bot.Object, dialogService, CreateLocalizerMock().Object);

        var message = DeserializeMessage(
            "{\"message_id\":10,\"from\":{\"id\":42,\"first_name\":\"Alice\"},\"chat\":{\"id\":-100,\"type\":\"supergroup\"},\"text\":\"Скидка\"}");

        await handler.HandleAsync(message, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Contains("Скидка", state!.SelectedTagNames);

        bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text!.Contains("Скидка") && !r.Text.Contains("Порошок")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TagCaptureStepHandler_Bulk_Applies_FreeText_To_Dialog_State()
    {
        var bot = CreateBotMock();
        var dialogService = new PendingDialogService<TagCaptureDialogState>();
        dialogService.SetState(-100L, 42L, new TagCaptureDialogState
        {
            ItemIds = new List<int> { 1, 2, 3 },
            ItemLabel = "3 товара",
            GroupId = 10,
        });

        var handler = new TagCaptureStepHandler(bot.Object, dialogService, CreateLocalizerMock().Object);

        var message = DeserializeMessage(
            "{\"message_id\":10,\"from\":{\"id\":42,\"first_name\":\"Alice\"},\"chat\":{\"id\":-100,\"type\":\"supergroup\"},\"text\":\"Молочка\"}");

        await handler.HandleAsync(message, CancellationToken.None);

        var state = dialogService.GetState(-100L, 42L);
        Assert.NotNull(state);
        Assert.Contains("Молочка", state!.SelectedTagNames);
        Assert.Equal(new List<int> { 1, 2, 3 }, state.ItemIds);
    }
}
