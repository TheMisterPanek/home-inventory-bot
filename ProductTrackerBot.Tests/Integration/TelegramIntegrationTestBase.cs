using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ProductTrackerBot.Database;
using ProductTrackerBot.Handlers;
using ProductTrackerBot.Localization;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace ProductTrackerBot.Tests.Integration;

public abstract class TelegramIntegrationTestBase : IDisposable
{
    protected const string ConnectionString = "Data Source=file:integration_test?mode=memory&cache=shared";

    protected static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SqliteConnection connection;
    private readonly UpdateDispatcher dispatcher;

    protected Mock<ITelegramBotClient> BotMock { get; }

    private readonly List<SendMessageRequest> sentMessages = new();

    private readonly List<EditMessageTextRequest> editedMessages = new();

    protected GroupRepository GroupRepository { get; }

    protected ShoppingItemRepository ItemRepository { get; }

    protected TagRepository TagRepository { get; }

    protected IHistoryRepository HistoryRepository { get; }

    protected PurchaseHistoryRepository PurchaseRepository { get; }

    protected PriceLogRepository PriceLogRepository { get; }

    protected MealRepository MealRepository { get; }

    protected MealIngredientRepository MealIngredientRepository { get; }

    protected MealStepRepository MealStepRepository { get; }

    protected DayMealsRepository DayMealsRepository { get; }

    protected Mock<IAiQueryService> AiQueryServiceMock { get; }

    protected PendingDialogService<BoughtDialogState> BoughtDialogService { get; private set; } = null!;

    protected PendingDialogService<PriceCaptureDialogState> PriceDialogService { get; private set; } = null!;

    protected AiSuggestionService AiSuggestionService { get; private set; } = null!;

    protected LoginCodeStore LoginCodeStore { get; private set; } = null!;

    protected TelegramIntegrationTestBase()
    {
        this.connection = new SqliteConnection(ConnectionString);
        this.connection.Open();

        var dbInitializer = new DatabaseInitializer(ConnectionString, Mock.Of<ILogger<DatabaseInitializer>>());
        dbInitializer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        this.GroupRepository = new GroupRepository(ConnectionString);
        this.ItemRepository = new ShoppingItemRepository(ConnectionString);
        this.TagRepository = new TagRepository(ConnectionString);
        this.HistoryRepository = new HistoryRepository(ConnectionString);
        this.PurchaseRepository = new PurchaseHistoryRepository(ConnectionString);
        this.PriceLogRepository = new PriceLogRepository(ConnectionString);
        this.MealRepository = new MealRepository(ConnectionString);
        this.MealIngredientRepository = new MealIngredientRepository(ConnectionString);
        this.MealStepRepository = new MealStepRepository(ConnectionString);
        this.DayMealsRepository = new DayMealsRepository(ConnectionString);

        var localizer = new Mock<ILocalizer>();
        localizer.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>()))
            .Returns<long, string>((_, key) => key);
        localizer.Setup(l => l.Get(It.IsAny<long>(), "buy.item-added"))
            .Returns("{name} added {item}");
        localizer.Setup(l => l.Get(It.IsAny<long>(), "buy.item-added-quantity"))
            .Returns("{name} added {item} ({quantity})");

        this.BotMock = new Mock<ITelegramBotClient>();
        this.BotMock.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Message>, CancellationToken>((req, _) =>
            {
                if (req is SendMessageRequest smr) this.sentMessages.Add(smr);
            })
            .ReturnsAsync(new Message());
        this.BotMock.Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Message>, CancellationToken>((req, _) =>
            {
                if (req is EditMessageTextRequest emr) this.editedMessages.Add(emr);
            })
            .ReturnsAsync(new Message());
        this.BotMock.Setup(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.BotMock.Setup(b => b.SendRequest(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        this.BotMock.Setup(b => b.SendRequest(It.IsAny<EditMessageReplyMarkupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());
        this.BotMock.Setup(b => b.SendRequest(It.IsAny<SendChatActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        this.AiQueryServiceMock = new Mock<IAiQueryService>();
        this.AiQueryServiceMock
            .Setup(s => s.AnswerAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiQueryResult("AI response", []));

        var buyDialogService = new PendingDialogService<BuyDialogState>();
        var priceDialogService = new PendingDialogService<PriceCaptureDialogState>();
        this.PriceDialogService = priceDialogService;
        var tagCaptureDialogService = new PendingDialogService<TagCaptureDialogState>();
        var mealCreateDialogService = new PendingDialogService<MealCreateDialogState>();
        var mealIngredientDialogService = new PendingDialogService<MealAddIngredientDialogState>();
        var mealStepDialogService = new PendingDialogService<MealAddStepDialogState>();
        var boughtDialogService = new PendingDialogService<BoughtDialogState>();
        this.BoughtDialogService = boughtDialogService;

        var pendingAddService = new PendingAddService();
        var aiSuggestionService = new AiSuggestionService();
        this.AiSuggestionService = aiSuggestionService;
        var loginCodeStore = new LoginCodeStore(ConnectionString, TimeProvider.System);
        this.LoginCodeStore = loginCodeStore;

        var listService = new ShoppingListService(this.GroupRepository, this.ItemRepository,
                this.TagRepository, localizer.Object);
        var undoService = new UndoService(this.HistoryRepository, this.ItemRepository, this.GroupRepository, Mock.Of<ILogger<UndoService>>());
        var mealMergeService = new MealMergeService();
        var tagCaptureService = new TagCaptureService(
            this.BotMock.Object, tagCaptureDialogService, priceDialogService, this.TagRepository, localizer.Object);

        var buyAddService = new BuyAddService(
            this.BotMock.Object, this.ItemRepository, this.HistoryRepository, tagCaptureService,
            localizer.Object, Mock.Of<ILogger<BuyAddService>>());

        // Command handlers
        var buyHandler = new BuyCommandHandler(
            this.BotMock.Object, this.GroupRepository, buyDialogService, pendingAddService, localizer.Object,
            listService, this.HistoryRepository, tagCaptureService, buyAddService, Mock.Of<ILogger<BuyCommandHandler>>());

        var listHandler = new ListCommandHandler(
            this.BotMock.Object, listService, this.GroupRepository, this.HistoryRepository,
            Mock.Of<ILogger<ListCommandHandler>>());

        var historyHandler = new HistoryCommandHandler(this.BotMock.Object, this.HistoryRepository);

        var searchHandler = new SearchCommandHandler(
            this.BotMock.Object, this.PurchaseRepository, this.GroupRepository,
            Mock.Of<ILogger<SearchCommandHandler>>());

        var pricesHandler = new PricesCommandHandler(
            this.BotMock.Object, this.PriceLogRepository, this.GroupRepository, localizer.Object);

        var settingsHandler = new SettingsCommandHandler(
            this.BotMock.Object, localizer.Object, Mock.Of<ILogger<SettingsCommandHandler>>());

        var languageHandler = new LanguageCommandHandler(this.BotMock.Object, localizer.Object);

        var undoCommandHandler = new UndoCommandHandler(this.BotMock.Object, undoService, localizer.Object);

        var mealsHandler = new MealsCommandHandler(
            this.BotMock.Object, this.GroupRepository, this.MealRepository,
            Mock.Of<ILogger<MealsCommandHandler>>());

        var aiHandler = new AiCommandHandler(
            this.BotMock.Object, this.GroupRepository, this.AiQueryServiceMock.Object,
            aiSuggestionService, new ConversationHistoryService(), localizer.Object, Mock.Of<ILogger<AiCommandHandler>>());

        var loginHandler = new LoginCommandHandler(this.BotMock.Object, localizer.Object, loginCodeStore);

        var weekHandler = new WeekCommandHandler(
            this.BotMock.Object, this.GroupRepository, this.DayMealsRepository,
            localizer.Object, Mock.Of<ILogger<WeekCommandHandler>>());

        var boughtHandler = new BoughtCommandHandler(
            this.BotMock.Object, this.GroupRepository, boughtDialogService, localizer.Object);

        var useHandler = new UseCommandHandler(
            this.BotMock.Object, this.GroupRepository, this.PurchaseRepository,
            this.ItemRepository, localizer.Object);

        var nonStartHandlers = new List<ICommandHandler>
        {
            buyHandler, listHandler, historyHandler, searchHandler, pricesHandler,
            settingsHandler, languageHandler, undoCommandHandler, mealsHandler, aiHandler, loginHandler, weekHandler,
            boughtHandler, useHandler,
        };

        var scopeFactory = BuildScopeFactory(nonStartHandlers);
        var startHandler = new StartCommandHandler(scopeFactory, this.BotMock.Object, localizer.Object);

        var commandHandlers = new List<ICommandHandler> { startHandler };
        commandHandlers.AddRange(nonStartHandlers);

        // Callback handlers
        var shopDoneHandler = new ShopDoneCallbackHandler(
            this.BotMock.Object, this.ItemRepository, listService, this.GroupRepository,
            this.HistoryRepository, Mock.Of<ILogger<ShopDoneCallbackHandler>>());

        var shopSelectHandler = new ShopSelectCallbackHandler(
            this.BotMock.Object, listService, this.GroupRepository,
            Mock.Of<ILogger<ShopSelectCallbackHandler>>());

        var shopRemoveHandler = new ShopRemoveCallbackHandler(
            this.BotMock.Object, this.ItemRepository, listService, this.GroupRepository,
            this.HistoryRepository, Mock.Of<ILogger<ShopRemoveCallbackHandler>>());

        var actionCancelHandler = new ActionCancelCallbackHandler(
            this.BotMock.Object, Mock.Of<ILogger<ActionCancelCallbackHandler>>());

        var langCallbackHandler = new LanguageCallbackHandler(
            this.BotMock.Object, this.GroupRepository, localizer.Object);

        var langSelectionHandler = new LanguageSelectionHandler(
            this.BotMock.Object, this.GroupRepository, this.HistoryRepository,
            localizer.Object, Mock.Of<ILogger<LanguageSelectionHandler>>());

        var buySkipHandler = new BuySkipCallbackHandler(
            this.BotMock.Object, buyDialogService, this.ItemRepository, this.HistoryRepository,
            tagCaptureService, localizer.Object, Mock.Of<ILogger<BuySkipCallbackHandler>>());

        var buySkipExpiryHandler = new BuySkipExpiryCallbackHandler(
            this.BotMock.Object, buyDialogService, this.ItemRepository, this.HistoryRepository,
            localizer.Object, Mock.Of<ILogger<BuySkipExpiryCallbackHandler>>());

        var listNextHandler = new ListNextCallbackHandler(
            this.BotMock.Object, listService, this.GroupRepository, this.HistoryRepository,
            Mock.Of<ILogger<ListNextCallbackHandler>>());

        var listPrevHandler = new ListPrevCallbackHandler(
            this.BotMock.Object, listService, this.GroupRepository, this.HistoryRepository,
            Mock.Of<ILogger<ListPrevCallbackHandler>>());

        var listFilterHandler = new ListFilterCallbackHandler(
            this.BotMock.Object, listService, this.GroupRepository, this.TagRepository, this.HistoryRepository,
            Mock.Of<ILogger<ListFilterCallbackHandler>>());

        var listTagPageHandler = new ListTagPageCallbackHandler(
            this.BotMock.Object, listService, this.GroupRepository, this.TagRepository, this.HistoryRepository,
            Mock.Of<ILogger<ListTagPageCallbackHandler>>());

        var noOpHandler = new NoOpCallbackHandler(this.BotMock.Object);

        var tagToggleHandler = new TagToggleCallbackHandler(
            this.BotMock.Object, tagCaptureDialogService, localizer.Object,
            Mock.Of<ILogger<TagToggleCallbackHandler>>());

        var tagDoneHandler = new TagDoneCallbackHandler(
            this.BotMock.Object, tagCaptureDialogService, this.TagRepository, listService, localizer.Object,
            Mock.Of<ILogger<TagDoneCallbackHandler>>());

        var tagSkipHandler = new TagSkipCallbackHandler(
            this.BotMock.Object, tagCaptureDialogService, listService, localizer.Object,
            Mock.Of<ILogger<TagSkipCallbackHandler>>());

        var undoInlineHandler = new UndoInlineCallbackHandler(
            this.BotMock.Object, undoService, priceDialogService, localizer.Object,
            Mock.Of<ILogger<UndoInlineCallbackHandler>>());

        var priceSkipHandler = new PriceSkipCallbackHandler(
            this.BotMock.Object, priceDialogService, this.PurchaseRepository, this.GroupRepository,
            this.TagRepository, localizer.Object, Mock.Of<ILogger<PriceSkipCallbackHandler>>());

        var priceShopHandler = new PriceShopSuggestionCallbackHandler(
            this.BotMock.Object, priceDialogService, localizer.Object, Mock.Of<ILogger<PriceShopSuggestionCallbackHandler>>());

        var mealCallbackHandler = new MealCallbackHandler(
            this.BotMock.Object, this.GroupRepository, this.MealRepository,
            this.MealIngredientRepository, this.MealStepRepository, this.ItemRepository,
            mealCreateDialogService, mealIngredientDialogService, mealStepDialogService,
            mealMergeService, localizer.Object, Mock.Of<ILogger<MealCallbackHandler>>());

        var weekCallbackHandler = new WeekCallbackHandler(
            this.BotMock.Object, this.GroupRepository, this.DayMealsRepository,
            this.MealRepository, this.MealIngredientRepository, this.ItemRepository,
            localizer.Object, Mock.Of<ILogger<WeekCallbackHandler>>());

        var buyConfirmHandler = new BuyConfirmCallbackHandler(
            this.BotMock.Object, pendingAddService, buyAddService);

        var buyEditHandler = new BuyEditCallbackHandler(
            this.BotMock.Object, pendingAddService, buyDialogService, localizer.Object);

        var buyCancelHandler = new BuyCancelCallbackHandler(
            this.BotMock.Object, pendingAddService, localizer.Object);

        var aiAddItemHandler = new AiAddItemCallbackHandler(
            this.BotMock.Object, aiSuggestionService, this.GroupRepository, this.ItemRepository,
            this.HistoryRepository, localizer.Object, Mock.Of<ILogger<AiAddItemCallbackHandler>>());

        var aiAddAllHandler = new AiAddAllCallbackHandler(
            this.BotMock.Object, aiSuggestionService, this.GroupRepository, this.ItemRepository,
            this.HistoryRepository, localizer.Object, Mock.Of<ILogger<AiAddAllCallbackHandler>>());

        var boughtSkipExpiryCallbackHandler = new BoughtSkipExpiryCallbackHandler(
            this.BotMock.Object, boughtDialogService, this.PurchaseRepository, this.HistoryRepository,
            localizer.Object, Mock.Of<ILogger<BoughtSkipExpiryCallbackHandler>>());

        var suggestionService = new ExpiryDaySuggestionService(this.PurchaseRepository);

        // Dialog handlers (created before ExpirySuggestCallbackHandler to allow cross-reference)
        var buyStepHandler = new BuyStepHandler(
            this.BotMock.Object, buyDialogService, pendingAddService, localizer.Object);

        var priceCaptureHandler = new PriceCaptureStepHandler(
            this.BotMock.Object, priceDialogService, this.PurchaseRepository, this.PriceLogRepository,
            this.GroupRepository, this.TagRepository, suggestionService, localizer.Object, Mock.Of<ILogger<PriceCaptureStepHandler>>());

        var mealDialogHandler = new MealDialogStepHandler(
            this.BotMock.Object, this.GroupRepository, mealCreateDialogService,
            mealIngredientDialogService, mealStepDialogService,
            this.MealRepository, this.MealIngredientRepository, this.MealStepRepository,
            Mock.Of<ILogger<MealDialogStepHandler>>());

        var tagCaptureStepHandler = new TagCaptureStepHandler(
            this.BotMock.Object, tagCaptureDialogService, localizer.Object);

        var boughtStepHandler = new BoughtStepHandler(
            this.BotMock.Object, boughtDialogService, this.PurchaseRepository, this.HistoryRepository,
            suggestionService, localizer.Object, Mock.Of<ILogger<BoughtStepHandler>>());

        var expirySuggestCallbackHandler = new ExpirySuggestCallbackHandler(
            this.BotMock.Object, boughtDialogService, priceDialogService,
            boughtStepHandler, priceCaptureHandler);

        var useRemoveCallbackHandler = new UseRemoveCallbackHandler(
            this.BotMock.Object, this.GroupRepository, this.PurchaseRepository,
            this.ItemRepository, localizer.Object, Mock.Of<ILogger<UseRemoveCallbackHandler>>());

        var callbackHandlers = new List<ICallbackHandler>
        {
            shopSelectHandler, shopDoneHandler, shopRemoveHandler, actionCancelHandler, langCallbackHandler,
            langSelectionHandler, buySkipHandler, buySkipExpiryHandler, buyConfirmHandler,
            buyEditHandler, buyCancelHandler, listNextHandler, listPrevHandler, listFilterHandler, undoInlineHandler,
            priceSkipHandler, priceShopHandler, mealCallbackHandler, aiAddItemHandler,
            aiAddAllHandler, weekCallbackHandler, boughtSkipExpiryCallbackHandler,
            expirySuggestCallbackHandler, useRemoveCallbackHandler,
            tagToggleHandler, tagDoneHandler, tagSkipHandler,
            listTagPageHandler, noOpHandler,
        };

        var dialogHandlers = new List<IDialogMessageHandler>
        {
            buyStepHandler, priceCaptureHandler, mealDialogHandler, boughtStepHandler,
            tagCaptureStepHandler,
        };


        var dispatcherSp = new Mock<IServiceProvider>();
        dispatcherSp.Setup(x => x.GetService(typeof(IEnumerable<ICommandHandler>))).Returns(commandHandlers);
        dispatcherSp.Setup(x => x.GetService(typeof(IEnumerable<ICallbackHandler>))).Returns(callbackHandlers);
        dispatcherSp.Setup(x => x.GetService(typeof(IEnumerable<IDialogMessageHandler>))).Returns(dialogHandlers);
        var dispatcherScope = new Mock<IServiceScope>();
        dispatcherScope.Setup(x => x.ServiceProvider).Returns(dispatcherSp.Object);
        var dispatcherScopeFactory = new Mock<IServiceScopeFactory>();
        dispatcherScopeFactory.Setup(x => x.CreateScope()).Returns(dispatcherScope.Object);
        this.dispatcher = new UpdateDispatcher(dispatcherScopeFactory.Object, Mock.Of<ILogger<UpdateDispatcher>>());
    }

    protected Task DispatchAsync(Update update) =>
        this.dispatcher.HandleUpdateAsync(this.BotMock.Object, update, CancellationToken.None);

    /// <summary>
    /// Returns the last message the bot sent via SendMessage (not EditMessageText).
    /// </summary>
    protected SendMessageRequest? GetLastSentMessage() =>
        this.sentMessages.Count > 0 ? this.sentMessages[^1] : null;

    /// <summary>
    /// Returns the last message the bot edited via EditMessageText (e.g. list refresh via list_filter).
    /// </summary>
    protected EditMessageTextRequest? GetLastEditedMessage() =>
        this.editedMessages.Count > 0 ? this.editedMessages[^1] : null;

    /// <summary>
    /// Returns the callback data for the confirm button from the last buy review message sent by the bot.
    /// Use this to simulate tapping "✓ Add" after a /buy command sends a review.
    /// </summary>
    protected string? GetLastBuyConfirmCallbackData()
    {
        for (int i = this.sentMessages.Count - 1; i >= 0; i--)
        {
            var req = this.sentMessages[i];
            if (req.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup ikm)
            {
                foreach (var row in ikm.InlineKeyboard)
                {
                    foreach (var btn in row)
                    {
                        if (btn.CallbackData?.StartsWith("buy:confirm:") == true)
                        {
                            return btn.CallbackData;
                        }
                    }
                }
            }
        }

        return null;
    }

    protected string? GetLastTagToggleCallbackData()
    {
        for (int i = this.sentMessages.Count - 1; i >= 0; i--)
        {
            var req = this.sentMessages[i];
            if (req.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup ikm)
            {
                foreach (var row in ikm.InlineKeyboard)
                {
                    foreach (var btn in row)
                    {
                        if (btn.CallbackData?.StartsWith("tag:toggle:") == true)
                        {
                            return btn.CallbackData;
                        }
                    }
                }
            }
        }

        return null;
    }

    protected string? GetLastExpirySuggestCallbackData()
    {
        for (int i = this.sentMessages.Count - 1; i >= 0; i--)
        {
            var req = this.sentMessages[i];
            if (req.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup ikm)
            {
                foreach (var row in ikm.InlineKeyboard)
                {
                    foreach (var btn in row)
                    {
                        if (btn.CallbackData?.StartsWith("expiry:suggest:") == true)
                        {
                            return btn.CallbackData;
                        }
                    }
                }
            }
        }

        return null;
    }

    protected async Task ClearDataAsync()
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM PriceLog;
            DELETE FROM PurchaseHistoryTags;
            DELETE FROM PurchaseHistory;
            DELETE FROM BotActionHistory;
            DELETE FROM DayMeals;
            DELETE FROM MealSteps;
            DELETE FROM MealIngredients;
            DELETE FROM Meals;
            DELETE FROM ItemTags;
            DELETE FROM Tags;
            DELETE FROM ShoppingItems;
            DELETE FROM Groups;";
        await cmd.ExecuteNonQueryAsync();
    }

    // Chat type is included so handlers that check ChatType (e.g. /meals, /prices) work in supergroup mode
    protected static Update CommandUpdate(long chatId, long userId, string text) =>
        DeserializeUpdate(
            $"{{\"update_id\":1,\"message\":{{\"message_id\":1,\"from\":{{\"id\":{userId},\"first_name\":\"TestUser\"}},\"chat\":{{\"id\":{chatId},\"type\":\"supergroup\"}},\"text\":\"{text}\"}}}}");

    protected static Update CallbackUpdate(long chatId, long userId, int messageId, string data) =>
        DeserializeUpdate(
            $"{{\"update_id\":1,\"callback_query\":{{\"id\":\"cb1\",\"from\":{{\"id\":{userId},\"first_name\":\"TestUser\"}},\"message\":{{\"message_id\":{messageId},\"chat\":{{\"id\":{chatId},\"type\":\"supergroup\"}}}},\"data\":\"{data}\"}}}}");

    protected static Update MessageUpdate(long chatId, long userId, string text) =>
        DeserializeUpdate(
            $"{{\"update_id\":1,\"message\":{{\"message_id\":1,\"from\":{{\"id\":{userId},\"first_name\":\"TestUser\"}},\"chat\":{{\"id\":{chatId},\"type\":\"supergroup\"}},\"text\":\"{text}\"}}}}");

    // Private chat variant for handlers that restrict to group-only
    protected static Update PrivateCommandUpdate(long chatId, long userId, string text) =>
        DeserializeUpdate(
            $"{{\"update_id\":1,\"message\":{{\"message_id\":1,\"from\":{{\"id\":{userId},\"first_name\":\"TestUser\"}},\"chat\":{{\"id\":{chatId},\"type\":\"private\"}},\"text\":\"{text}\"}}}}");

    public void Dispose() => this.connection.Dispose();

    private static Update DeserializeUpdate(string json) =>
        JsonSerializer.Deserialize<Update>(json, JsonOpts)!;

    private static IServiceScopeFactory BuildScopeFactory(IEnumerable<ICommandHandler> handlers)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEnumerable<ICommandHandler>)))
            .Returns(handlers);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return scopeFactory.Object;
    }
}
