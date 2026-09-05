using ProductTrackerBot.Services;
using Telegram.Bot.Types.ReplyMarkups;

namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class ListFilterIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task List_NoTaggedItems_ShowsNoFilterRow()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        Assert.DoesNotContain(
            keyboard.InlineKeyboard.SelectMany(row => row),
            btn => btn.CallbackData?.StartsWith("list_filter:") == true);
    }

    [Fact]
    public async Task List_WithTaggedItems_FilterButtonScopesView_AndAllClearsIt()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item1 = await ItemRepository.AddAsync(group.Id, "Порошок", null, "TestUser");
        await TagRepository.SetItemTagsAsync(new[] { item1.Id }, group.Id, new[] { "Химия" });
        var item2 = await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");
        await TagRepository.SetItemTagsAsync(new[] { item2.Id }, group.Id, new[] { "Еда" });

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var filterButton = keyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(btn => btn.CallbackData?.StartsWith("list_filter:") == true && btn.Text == "Химия");

        await DispatchAsync(CallbackUpdate(-100, 42, 1, filterButton.CallbackData!));

        var filteredEdit = GetLastEditedMessage();
        Assert.NotNull(filteredEdit);
        Assert.Contains("Порошок", filteredEdit!.Text);
        Assert.DoesNotContain("Молоко", filteredEdit.Text);

        var filteredKeyboard = Assert.IsType<InlineKeyboardMarkup>(filteredEdit.ReplyMarkup);
        var allButton = filteredKeyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(btn => btn.CallbackData == "list_filter:-100:-1:1:1");

        await DispatchAsync(CallbackUpdate(-100, 42, 1, allButton.CallbackData!));

        var clearedEdit = GetLastEditedMessage();
        Assert.NotNull(clearedEdit);
        Assert.Contains("Порошок", clearedEdit!.Text);
        Assert.Contains("Молоко", clearedEdit.Text);
    }

    [Fact]
    public async Task List_TwoActiveTags_ExpandsToUnion_TappingOneAgainNarrows()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item1 = await ItemRepository.AddAsync(group.Id, "Порошок", null, "TestUser");
        await TagRepository.SetItemTagsAsync(new[] { item1.Id }, group.Id, new[] { "Химия" });
        var item2 = await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");
        await TagRepository.SetItemTagsAsync(new[] { item2.Id }, group.Id, new[] { "Еда" });
        var item3 = await ItemRepository.AddAsync(group.Id, "Ключи", null, "TestUser");
        await TagRepository.SetItemTagsAsync(new[] { item3.Id }, group.Id, new[] { "Авто" });

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var chemistryButton = keyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(btn => btn.CallbackData?.StartsWith("list_filter:") == true && btn.Text == "Химия");

        await DispatchAsync(CallbackUpdate(-100, 42, 1, chemistryButton.CallbackData!));

        var firstFilterEdit = GetLastEditedMessage();
        Assert.Contains("Порошок", firstFilterEdit!.Text);
        Assert.DoesNotContain("Молоко", firstFilterEdit.Text);

        var firstFilterKeyboard = Assert.IsType<InlineKeyboardMarkup>(firstFilterEdit.ReplyMarkup);
        var foodButton = firstFilterKeyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(btn => btn.Text == "Еда");

        await DispatchAsync(CallbackUpdate(-100, 42, 1, foodButton.CallbackData!));

        var unionEdit = GetLastEditedMessage();
        Assert.Contains("Порошок", unionEdit!.Text);
        Assert.Contains("Молоко", unionEdit.Text);
        Assert.DoesNotContain("Ключи", unionEdit.Text);

        var unionKeyboard = Assert.IsType<InlineKeyboardMarkup>(unionEdit.ReplyMarkup);
        var activeChemistryButton = unionKeyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(btn => btn.Text == "✓ Химия");

        await DispatchAsync(CallbackUpdate(-100, 42, 1, activeChemistryButton.CallbackData!));

        var narrowedEdit = GetLastEditedMessage();
        Assert.DoesNotContain("Порошок", narrowedEdit!.Text);
        Assert.Contains("Молоко", narrowedEdit.Text);
    }

    [Fact]
    public async Task FilteredView_PaginatesWithinTag()
    {
        await ClearDataAsync();

        var itemCount = ShoppingListService.ActionPageSize * 2;
        var totalPages = 2;
        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (int i = 1; i <= itemCount; i++)
        {
            var item = await ItemRepository.AddAsync(group.Id, $"Товар{i}", null, "TestUser");
            await TagRepository.SetItemTagsAsync(new[] { item.Id }, group.Id, new[] { "Химия" });
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var filterButton = keyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(btn => btn.CallbackData?.StartsWith("list_filter:") == true && btn.Text == "Химия");

        await DispatchAsync(CallbackUpdate(-100, 42, 1, filterButton.CallbackData!));

        // itemCount items at ActionPageSize per page → exactly 2 pages; page through to the last page.
        for (int page = 2; page <= totalPages; page++)
        {
            var currentEdit = GetLastEditedMessage();
            Assert.NotNull(currentEdit);
            var currentKeyboard = Assert.IsType<InlineKeyboardMarkup>(currentEdit!.ReplyMarkup);
            var nextButton = currentKeyboard.InlineKeyboard
                .SelectMany(row => row)
                .First(btn => btn.CallbackData?.StartsWith($"list_filter:-100:0:{page}:") == true);

            await DispatchAsync(CallbackUpdate(-100, 42, 1, nextButton.CallbackData!));
        }

        var lastPageEdit = GetLastEditedMessage();
        Assert.NotNull(lastPageEdit);
        var lastPageKeyboard = Assert.IsType<InlineKeyboardMarkup>(lastPageEdit!.ReplyMarkup);
        var itemButtons = lastPageKeyboard.InlineKeyboard
            .SelectMany(row => row)
            .Where(btn => btn.CallbackData?.StartsWith("shop:sel:") == true)
            .Select(btn => btn.Text)
            .ToList();
        Assert.Equal(ShoppingListService.ActionPageSize, itemButtons.Count);
        Assert.Contains($"Товар{itemCount - 1}", itemButtons);
        Assert.Contains($"Товар{itemCount}", itemButtons);
    }
}
