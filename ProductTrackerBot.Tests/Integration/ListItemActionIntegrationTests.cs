using Moq;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;

namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class ListItemActionIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task List_ItemRow_IsSingleFullWidthSelectButton()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Молоко", "2л", "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var row = keyboard.InlineKeyboard.First().ToList();

        var button = Assert.Single(row);
        Assert.Equal("Молоко 2л", button.Text);
        Assert.Equal($"shop:sel:{item.Id}:1::1", button.CallbackData);
        Assert.DoesNotContain(keyboard.InlineKeyboard.SelectMany(r => r), b => b.CallbackData?.StartsWith("shop:remove:") == true);
    }

    [Fact]
    public async Task TappingItem_ExpandsRow_IntoNameBoughtAndRemoveButtons()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        await DispatchAsync(CallbackUpdate(-100, 42, 1, $"shop:sel:{item.Id}:1::1"));

        var edited = GetLastEditedMessage();
        Assert.NotNull(edited);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(edited!.ReplyMarkup);
        var row = keyboard.InlineKeyboard.First().ToList();

        Assert.Equal(3, row.Count);
        Assert.Equal("Молоко", row[0].Text);
        Assert.Equal("shop:sel:0:1::1", row[0].CallbackData);
        Assert.Equal($"shop:done:{item.Id}:1::1", row[1].CallbackData);
        Assert.Equal($"shop:remove:{item.Id}:1::1", row[2].CallbackData);

        // Item still on the list — expanding is a display-only action
        var remaining = await ItemRepository.GetAllAsync(group.Id);
        Assert.Contains(remaining, i => i.Id == item.Id);
    }

    [Fact]
    public async Task TappingExpandedItemName_CollapsesRowBack()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Хлеб", null, "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        await DispatchAsync(CallbackUpdate(-100, 42, 1, $"shop:sel:{item.Id}:1::1"));
        await DispatchAsync(CallbackUpdate(-100, 42, 1, "shop:sel:0:1::1"));

        var edited = GetLastEditedMessage();
        Assert.NotNull(edited);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(edited!.ReplyMarkup);
        var row = keyboard.InlineKeyboard.First().ToList();

        var button = Assert.Single(row);
        Assert.Equal($"shop:sel:{item.Id}:1::1", button.CallbackData);
    }

    [Fact]
    public async Task BoughtButton_DeletesItemSilently_AndOnlyRefreshesListMessage()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");
        await ItemRepository.AddAsync(group.Id, "Хлеб", null, "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        BotMock.Invocations.Clear();

        await DispatchAsync(CallbackUpdate(-100, 42, 1, $"shop:done:{item.Id}:1::1"));

        var remaining = await ItemRepository.GetAllAsync(group.Id);
        Assert.DoesNotContain(remaining, i => i.Id == item.Id);

        // No confirmation, no "where did you buy" prompt — the list message is just re-rendered
        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var edited = GetLastEditedMessage();
        Assert.NotNull(edited);
        Assert.DoesNotContain("Молоко", edited!.Text);
        Assert.Contains("Хлеб", edited.Text);
    }

    [Fact]
    public async Task BoughtButton_InFilteredView_KeepsTagFilterOnRefresh()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var bleach = await ItemRepository.AddAsync(group.Id, "Белизна", null, "TestUser");
        var soap = await ItemRepository.AddAsync(group.Id, "Мыло", null, "TestUser");
        var milk = await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");
        await TagRepository.SetItemTagsAsync(new[] { bleach.Id, soap.Id }, group.Id, new[] { "Химия" });
        await TagRepository.SetItemTagsAsync(new[] { milk.Id }, group.Id, new[] { "Еда" });

        var allTags = await TagRepository.GetDistinctTagsAsync(group.Id);
        var chemistryIndex = allTags.ToList().FindIndex(t => t == "Химия");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        await DispatchAsync(CallbackUpdate(-100, 42, 1, $"list_filter:-100:{chemistryIndex}:1:1"));

        var filtered = GetLastEditedMessage();
        Assert.NotNull(filtered);
        var filteredKeyboard = Assert.IsType<InlineKeyboardMarkup>(filtered!.ReplyMarkup);
        var bleachButton = filteredKeyboard.InlineKeyboard
            .SelectMany(r => r)
            .First(b => b.Text == "Белизна");

        // Expand the row, then mark it bought — both callbacks carry the filtered view context
        await DispatchAsync(CallbackUpdate(-100, 42, 1, bleachButton.CallbackData!));
        var expanded = GetLastEditedMessage();
        var boughtButton = Assert.IsType<InlineKeyboardMarkup>(expanded!.ReplyMarkup).InlineKeyboard
            .SelectMany(r => r)
            .First(b => b.CallbackData!.StartsWith($"shop:done:{bleach.Id}:"));

        await DispatchAsync(CallbackUpdate(-100, 42, 1, boughtButton.CallbackData!));

        var refreshed = GetLastEditedMessage();
        Assert.NotNull(refreshed);
        Assert.Contains("Мыло", refreshed!.Text);
        Assert.DoesNotContain("Белизна", refreshed.Text);
        // Still filtered to Химия — the Еда item must not reappear
        Assert.DoesNotContain("Молоко", refreshed.Text);
    }

    [Fact]
    public async Task RemoveButton_DeletesItem_AndRefreshesListSilently()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Хлеб", null, "TestUser");
        await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        BotMock.Invocations.Clear();

        await DispatchAsync(CallbackUpdate(-100, 42, 1, $"shop:remove:{item.Id}:1::1"));

        var remaining = await ItemRepository.GetAllAsync(group.Id);
        Assert.DoesNotContain(remaining, i => i.Id == item.Id);

        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var edited = GetLastEditedMessage();
        Assert.NotNull(edited);
        Assert.DoesNotContain("Хлеб", edited!.Text);
    }

    [Fact]
    public async Task LegacyCallbackWithoutViewContext_StillMarksItemBought()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Молоко", null, "TestUser");

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        await DispatchAsync(CallbackUpdate(-100, 42, 1, $"shop:done:{item.Id}"));

        var remaining = await ItemRepository.GetAllAsync(group.Id);
        Assert.DoesNotContain(remaining, i => i.Id == item.Id);
    }
}
