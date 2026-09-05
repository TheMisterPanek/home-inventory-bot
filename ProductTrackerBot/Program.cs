// <copyright file="Program.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net.Http.Headers;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ProductTrackerBot;
using ProductTrackerBot.Database;
using ProductTrackerBot.Handlers;
using ProductTrackerBot.Localization;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;

// Load environment variables from .env file before creating the host
var envFile = Path.Combine(Environment.CurrentDirectory, ".env");
if (!File.Exists(envFile))
{
    envFile = Path.Combine(AppContext.BaseDirectory, "../../../../.env");
}
if (File.Exists(envFile))
{
    Env.Load(envFile);
}

var builder = Host.CreateApplicationBuilder(args);

// Ensure environment variables are added to configuration (may already be done by default)
builder.Configuration.AddEnvironmentVariables();

// Configure BotConfiguration from environment variables
builder.Services.AddOptions<BotConfiguration>()
    .BindConfiguration(string.Empty)
    .ValidateOnStart();

// Add custom validation for Token
// Note: BotConfiguration.Token should never be logged; ensure no logging middleware logs configuration
builder.Services.AddOptions<BotConfiguration>()
    .PostConfigure(config =>
    {
        if (string.IsNullOrWhiteSpace(config.Token))
        {
            throw new InvalidOperationException("BotConfiguration.Token is required and cannot be empty. Set BOT_TOKEN environment variable.");
        }
    });

// Configure structured JSON logging with UTC timestamps
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

// Register Telegram bot services
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var options = sp.GetRequiredService<IOptionsMonitor<BotConfiguration>>();
    return new TelegramBotClient(options.CurrentValue.Token);
});

// Register SQLite database path and initializer
var dbPath = builder.Configuration["DB_PATH"]
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../data/product-tracker.db"));
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";
builder.Services.AddSingleton(connectionString);
builder.Services.AddHostedService<DatabaseInitializer>();

// Register AI query options
var aiQueryOptions = new AiQueryOptions
{
    ApiKey = builder.Configuration["OPENROUTER_API_KEY"] ?? string.Empty,
    Model = builder.Configuration["AI_QUERY_MODEL"] ?? "openai/gpt-4o-mini",
    BaseUrl = "https://openrouter.ai/api/v1/",
    IdentityMdPath = Path.Combine(AppContext.BaseDirectory, "IDENTITY.md"),
};
builder.Services.AddSingleton(aiQueryOptions);

// Register OpenRouter HTTP client (30s timeout, bearer auth)
builder.Services.AddHttpClient("openrouter", (sp, client) =>
{
    var opts = sp.GetRequiredService<AiQueryOptions>();
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
    if (!string.IsNullOrEmpty(opts.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", opts.ApiKey);
    }

    client.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/product-tracker-bot");
    client.DefaultRequestHeaders.Add("X-Title", "ProductTrackerBot");
});
builder.Services.AddSingleton<IOpenRouterClient>(sp =>
    new OpenRouterClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("openrouter"),
        sp.GetRequiredService<AiQueryOptions>()));

builder.Services.AddSingleton<IUpdateHandler, UpdateDispatcher>();
builder.Services.AddHostedService<BotCommandRegistrationService>(sp =>
{
    var botClient = sp.GetRequiredService<ITelegramBotClient>();
    var logger = sp.GetRequiredService<ILogger<BotCommandRegistrationService>>();
    using var scope = sp.CreateScope();
    var handlers = scope.ServiceProvider.GetRequiredService<IEnumerable<ICommandHandler>>();
    return new BotCommandRegistrationService(botClient, handlers, logger);
});
builder.Services.AddHostedService<BotHostedService>();

// Register pending session services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILoginCodeStore>(sp => new LoginCodeStore(connectionString, sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton<PendingAddService>();
builder.Services.AddSingleton<ConversationHistoryService>();
builder.Services.AddSingleton<AiSuggestionService>();

// Register dialog state services
builder.Services.AddSingleton<PendingDialogService<BuyDialogState>>();
builder.Services.AddSingleton<PendingDialogService<PriceCaptureDialogState>>();
builder.Services.AddSingleton<PendingDialogService<MealCreateDialogState>>();
builder.Services.AddSingleton<PendingDialogService<MealAddIngredientDialogState>>();
builder.Services.AddSingleton<PendingDialogService<MealAddStepDialogState>>();
builder.Services.AddSingleton<PendingDialogService<BoughtDialogState>>();
builder.Services.AddSingleton<PendingDialogService<TagCaptureDialogState>>();

// Register repositories
builder.Services.AddScoped<GroupRepository>();
builder.Services.AddScoped<ShoppingItemRepository>();
builder.Services.AddSingleton<IHistoryRepository, HistoryRepository>();
builder.Services.AddScoped<PurchaseHistoryRepository>();
builder.Services.AddScoped<PriceLogRepository>();
builder.Services.AddScoped<MealRepository>();
builder.Services.AddScoped<MealIngredientRepository>();
builder.Services.AddScoped<MealStepRepository>();
builder.Services.AddScoped<DayMealsRepository>();
builder.Services.AddScoped<TagRepository>();

// Register AI query service
builder.Services.AddScoped<IAiQueryService, AiQueryService>();

// Register services
builder.Services.AddScoped<ShoppingListService>();
builder.Services.AddScoped<TagCaptureService>();
builder.Services.AddScoped<BuyAddService>();
builder.Services.AddScoped<MealMergeService>();
builder.Services.AddScoped<ExpiryNotificationService>();
builder.Services.AddScoped<IUndoService, UndoService>();
builder.Services.AddSingleton<ILocalizer, Localizer>();

// Register hosted services for background jobs
var notifyTimeUtc = Environment.GetEnvironmentVariable("NOTIFY_TIME_UTC") ?? "09:00";
var notifyIntervalMinutes = int.TryParse(Environment.GetEnvironmentVariable("NOTIFY_INTERVAL_MINUTES"), out var nim) ? nim : (int?)null;
builder.Services.AddSingleton(sp => new ExpiryNotificationJob(
    sp.GetRequiredService<ITelegramBotClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<ExpiryNotificationJob>>(),
    notifyTimeUtc,
    notifyIntervalMinutes));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExpiryNotificationJob>());

builder.Services.AddScoped<ICommandHandler, AiCommandHandler>();
builder.Services.AddScoped<ICommandHandler, LoginCommandHandler>();

// Register command handlers
builder.Services.AddScoped<ICommandHandler, BuyCommandHandler>();
builder.Services.AddScoped<ICommandHandler, ListCommandHandler>();
builder.Services.AddScoped<ICommandHandler, HistoryCommandHandler>();
builder.Services.AddScoped<ICommandHandler, SearchCommandHandler>();
builder.Services.AddScoped<ICommandHandler, LanguageCommandHandler>();
builder.Services.AddScoped<ICommandHandler, MealsCommandHandler>();
builder.Services.AddScoped<ICommandHandler, PricesCommandHandler>();
builder.Services.AddScoped<ICommandHandler, SettingsCommandHandler>();
builder.Services.AddScoped<ICommandHandler, StartCommandHandler>();
builder.Services.AddScoped<ICommandHandler, UndoCommandHandler>();
builder.Services.AddScoped<ICommandHandler, WeekCommandHandler>();
builder.Services.AddScoped<ICommandHandler, BoughtCommandHandler>();
builder.Services.AddScoped<ICommandHandler, UseCommandHandler>();

// Register dialog message handlers
builder.Services.AddScoped<IDialogMessageHandler, BuyStepHandler>();
builder.Services.AddScoped<PriceCaptureStepHandler>();
builder.Services.AddScoped<IDialogMessageHandler>(sp => sp.GetRequiredService<PriceCaptureStepHandler>());
builder.Services.AddScoped<IDialogMessageHandler, MealDialogStepHandler>();
builder.Services.AddScoped<BoughtStepHandler>();
builder.Services.AddScoped<IDialogMessageHandler>(sp => sp.GetRequiredService<BoughtStepHandler>());
builder.Services.AddScoped<IDialogMessageHandler, TagCaptureStepHandler>();

// Register callback handlers
builder.Services.AddScoped<ICallbackHandler, BuySkipCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, BuyConfirmCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, BuyEditCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, BuyCancelCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ShopSelectCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ShopDoneCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ShopRemoveCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, PriceSkipCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, PriceShopSuggestionCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, LanguageCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, LanguageSelectionHandler>();
builder.Services.AddScoped<ICallbackHandler, ListNextCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ListPrevCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, MealCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ActionCancelCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, UndoInlineCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, AiAddItemCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, AiAddAllCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, WeekCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, BoughtSkipExpiryCallbackHandler>();
builder.Services.AddScoped<ExpiryDaySuggestionService>();
builder.Services.AddScoped<ICallbackHandler, ExpirySuggestCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, UseRemoveCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, TagToggleCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, TagDoneCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, TagSkipCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ListFilterCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, NoOpCallbackHandler>();
builder.Services.AddScoped<ICallbackHandler, ListTagPageCallbackHandler>();

var host = builder.Build();
await host.RunAsync();


