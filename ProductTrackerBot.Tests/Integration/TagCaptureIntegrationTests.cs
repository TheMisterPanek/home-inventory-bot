namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class TagCaptureIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task InlineSingleAdd_ToggleTwoSuggestions_Done_PersistsBothTags()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var tagId1 = await TagRepository.GetOrCreateAsync(group.Id, "Химия");
        await TagRepository.LinkPurchaseHistoryTagsAsync(
            (await PurchaseRepository.AddAsync(new Models.PurchaseRecord
            {
                GroupId = group.Id,
                UserId = 42,
                ItemName = "Порошок",
                BoughtByName = "TestUser",
                PurchasedAt = DateTime.UtcNow,
            })).Id,
            group.Id,
            new[] { "Химия", "Скидка" });

        // Inline single add sends a review step; confirm it to persist the item.
        await DispatchAsync(CommandUpdate(-100, 42, "/buy Молоко 2л"));
        var confirmData = GetLastBuyConfirmCallbackData();
        Assert.NotNull(confirmData);
        await DispatchAsync(CallbackUpdate(-100, 42, 1, confirmData));

        var toggle0 = GetLastTagToggleCallbackData();
        Assert.NotNull(toggle0);
        await DispatchAsync(CallbackUpdate(-100, 42, 2, toggle0));

        var toggle1 = "tag:toggle:1";
        await DispatchAsync(CallbackUpdate(-100, 42, 2, toggle1));

        await DispatchAsync(CallbackUpdate(-100, 42, 2, "tag:done"));

        var items = await ItemRepository.GetAllAsync(group.Id);
        var milk = Assert.Single(items, i => i.Name == "Молоко");
        Assert.Equal(2, milk.Tags.Count);
        Assert.Contains("Химия", milk.Tags);
        Assert.Contains("Скидка", milk.Tags);
    }

    [Fact]
    public async Task InlineSingleAdd_FreeTextReply_CreatesNewTag()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);

        await DispatchAsync(CommandUpdate(-100, 42, "/buy Молоко 2л"));
        var confirmData = GetLastBuyConfirmCallbackData();
        Assert.NotNull(confirmData);
        await DispatchAsync(CallbackUpdate(-100, 42, 1, confirmData));

        // No suggestions exist yet; reply with free text to create a brand-new tag.
        await DispatchAsync(MessageUpdate(-100, 42, "Для стирки"));
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "tag:done"));

        var items = await ItemRepository.GetAllAsync(group.Id);
        var milk = Assert.Single(items, i => i.Name == "Молоко");
        Assert.Contains("Для стирки", milk.Tags);
    }

    [Fact]
    public async Task BulkAdd_SingleTagPrompt_AppliesToAllItems()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);

        await DispatchAsync(CommandUpdate(-100, 42, "/buy Молоко, Яйца, Хлеб"));

        // No purchase history yet, so only the skip/done buttons are shown.
        var toggleData = GetLastTagToggleCallbackData();
        Assert.Null(toggleData);

        await DispatchAsync(MessageUpdate(-100, 42, "Бакалея"));
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "tag:done"));

        var items = await ItemRepository.GetAllAsync(group.Id);
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Contains("Бакалея", i.Tags));
    }

    [Fact]
    public async Task DialogSkipQuantity_ThenSkipTags_LeavesItemUntagged()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);

        await DispatchAsync(CommandUpdate(-100, 42, "/buy"));
        await DispatchAsync(MessageUpdate(-100, 42, "Rice"));
        await DispatchAsync(CallbackUpdate(-100, 42, 99, "buy:skip_quantity"));

        await DispatchAsync(CallbackUpdate(-100, 42, 100, "tag:skip"));

        var items = await ItemRepository.GetAllAsync(group.Id);
        var rice = Assert.Single(items, i => i.Name == "Rice");
        Assert.Empty(rice.Tags);
    }

    [Fact]
    public async Task BoughtItem_WithTags_CarriesTagsIntoPurchaseHistory()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Bleach", null, "TestUser", null);
        await TagRepository.SetItemTagsAsync(new[] { item.Id }, group.Id, new[] { "Химия", "Скидка" });
        var reloaded = await ItemRepository.GetByIdAsync(item.Id);
        Assert.Equal(2, reloaded!.Tags.Count);

        PriceDialogService.SetState(-100, 42, new Models.PriceCaptureDialogState
        {
            Step = 1,
            GroupId = group.Id,
            ItemName = "Bleach",
            BoughtByName = "TestUser",
            Tags = reloaded.Tags,
        });
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "price:skip_store"));
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "price:skip_price"));
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "price:skip_expiry"));

        var records = await PurchaseRepository.SearchAsync(group.Id, "Bleach");
        var record = Assert.Single(records);
        Assert.Contains("Химия", record.Tags);
        Assert.Contains("Скидка", record.Tags);
    }

    [Fact]
    public async Task BoughtItem_WithNoTags_LeavesPurchaseHistoryUntagged()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        await ItemRepository.AddAsync(group.Id, "Soap", null, "TestUser", null);

        PriceDialogService.SetState(-100, 42, new Models.PriceCaptureDialogState
        {
            Step = 1,
            GroupId = group.Id,
            ItemName = "Soap",
            BoughtByName = "TestUser",
        });
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "price:skip_store"));
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "price:skip_price"));
        await DispatchAsync(CallbackUpdate(-100, 42, 2, "price:skip_expiry"));

        var records = await PurchaseRepository.SearchAsync(group.Id, "Soap");
        var record = Assert.Single(records);
        Assert.Empty(record.Tags);
    }

    [Fact]
    public async Task TagSuggestions_SharedAcrossUsersInSameGroup()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var historyId = (await PurchaseRepository.AddAsync(new Models.PurchaseRecord
        {
            GroupId = group.Id,
            UserId = 1,
            ItemName = "Стиральный порошок",
            BoughtByName = "Alice",
            PurchasedAt = DateTime.UtcNow,
        })).Id;
        await TagRepository.LinkPurchaseHistoryTagsAsync(historyId, group.Id, new[] { "Химия" });

        // A different user (99) in the same group triggers a new tag prompt.
        // No quantity is detected, so the item is saved immediately (no review step).
        await DispatchAsync(CommandUpdate(-100, 99, "/buy Отбеливатель"));

        var toggleData = GetLastTagToggleCallbackData();
        Assert.NotNull(toggleData);
        await DispatchAsync(CallbackUpdate(-100, 99, 2, toggleData));
        await DispatchAsync(CallbackUpdate(-100, 99, 2, "tag:done"));

        var items = await ItemRepository.GetAllAsync(group.Id);
        var item = Assert.Single(items, i => i.Name == "Отбеливатель");
        Assert.Contains("Химия", item.Tags);
    }
}
