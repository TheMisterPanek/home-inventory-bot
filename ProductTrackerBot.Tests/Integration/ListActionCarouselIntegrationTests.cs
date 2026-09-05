using Moq;
using ProductTrackerBot.Services;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;

namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class ListActionCarouselIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task List_WithMoreItemsThanPageSize_FirstCarouselPage_ShowsFullPageActionRows_TextListsAll()
    {
        await ClearDataAsync();

        var itemCount = ShoppingListService.ActionPageSize + 1;
        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (var i = 1; i <= itemCount; i++)
        {
            await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        for (var i = 1; i <= itemCount; i++)
        {
            Assert.Contains($"Item{i}", sent!.Text);
        }

        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var actionRows = keyboard.InlineKeyboard
            .Where(row => row.Any(btn => btn.CallbackData!.StartsWith("shop:sel:")))
            .ToList();
        Assert.Equal(ShoppingListService.ActionPageSize, actionRows.Count);

        Assert.Contains(
            keyboard.InlineKeyboard.SelectMany(row => row),
            btn => btn.CallbackData!.StartsWith("list_next:"));
    }

    [Fact]
    public async Task ListNext_Then_ListPrev_PageThroughCarousel_AtNewPageSize()
    {
        await ClearDataAsync();

        var itemCount = ShoppingListService.ActionPageSize + 1;
        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (var i = 1; i <= itemCount; i++)
        {
            await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        await DispatchAsync(CallbackUpdate(-100, 42, 1, "list_next:-100:2"));
        var page2 = GetLastEditedMessage();
        Assert.NotNull(page2);
        var page2Keyboard = Assert.IsType<InlineKeyboardMarkup>(page2!.ReplyMarkup);
        var page2ActionRows = page2Keyboard.InlineKeyboard
            .Where(row => row.Any(btn => btn.CallbackData!.StartsWith("shop:sel:")))
            .ToList();
        Assert.Equal(itemCount - ShoppingListService.ActionPageSize, page2ActionRows.Count);
        Assert.Contains(page2Keyboard.InlineKeyboard.SelectMany(row => row), btn => btn.CallbackData!.StartsWith("list_prev:"));

        await DispatchAsync(CallbackUpdate(-100, 42, 1, "list_prev:-100:1"));
        var page1 = GetLastEditedMessage();
        Assert.NotNull(page1);
        var page1Keyboard = Assert.IsType<InlineKeyboardMarkup>(page1!.ReplyMarkup);
        var page1ActionRows = page1Keyboard.InlineKeyboard
            .Where(row => row.Any(btn => btn.CallbackData!.StartsWith("shop:sel:")))
            .ToList();
        Assert.Equal(ShoppingListService.ActionPageSize, page1ActionRows.Count);
    }

    [Fact]
    public async Task List_WithCategorizedItems_MultiplePages_PageIndicatorAppears_AndFiltersWrap()
    {
        await ClearDataAsync();

        var itemCount = ShoppingListService.ActionPageSize + 5;
        var totalPages = (int)Math.Ceiling(itemCount / (double)ShoppingListService.ActionPageSize);
        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (var i = 1; i <= 5; i++)
        {
            var item = await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
            await TagRepository.SetItemTagsAsync(new[] { item.Id }, group.Id, new[] { $"Tag{i:D2}" });
        }

        for (var i = 6; i <= itemCount; i++)
        {
            await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var rows = keyboard.InlineKeyboard.ToList();

        // More items than ActionPageSize → item pagination row present with a page indicator.
        // 5 tags (≤ TagPageSize) → filter block present with no tag-page indicator.
        var noOpButtons = rows.SelectMany(row => row).Where(btn => btn.CallbackData == "noop").ToList();
        Assert.Single(noOpButtons);
        Assert.Equal($"1/{totalPages}", noOpButtons[0].Text);

        // Filter tag rows wrap at 2 buttons per row.
        var tagButtonRows = rows
            .Where(row => row.Any(btn => btn.CallbackData!.StartsWith("list_filter:") && btn.CallbackData != "list_filter:-100:-1:1:1"))
            .ToList();
        Assert.All(tagButtonRows, row => Assert.True(row.Count() <= 2));
    }

    [Fact]
    public async Task TappingPageIndicatorButton_NoErrorNoStateChange_MessageUnchanged()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (var i = 1; i <= 5; i++)
        {
            await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        var beforeItems = await ItemRepository.GetAllAsync(group.Id);

        BotMock.Invocations.Clear();
        await DispatchAsync(CallbackUpdate(-100, 42, 1, "noop"));

        var afterItems = await ItemRepository.GetAllAsync(group.Id);
        Assert.Equal(beforeItems.Count, afterItems.Count);

        BotMock.Verify(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        BotMock.Verify(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_With10Tags_TagPagePaginates_AllItemsAndCancelStayVisible()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (var i = 1; i <= 10; i++)
        {
            var item = await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
            await TagRepository.SetItemTagsAsync(new[] { item.Id }, group.Id, new[] { $"Tag{i:D2}" });
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var allButtons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();

        for (var i = 1; i <= 6; i++)
        {
            Assert.Contains(allButtons, b => b.Text == $"Tag{i:D2}");
        }

        for (var i = 7; i <= 10; i++)
        {
            Assert.DoesNotContain(allButtons, b => b.Text == $"Tag{i:D2}");
        }

        var tagNextButton = allButtons.First(b => b.CallbackData!.StartsWith("list_tagpage:"));

        await DispatchAsync(CallbackUpdate(-100, 42, 1, tagNextButton.CallbackData!));

        var page2Edit = GetLastEditedMessage();
        Assert.NotNull(page2Edit);
        var page2Keyboard = Assert.IsType<InlineKeyboardMarkup>(page2Edit!.ReplyMarkup);
        var page2Buttons = page2Keyboard.InlineKeyboard.SelectMany(row => row).ToList();

        for (var i = 7; i <= 10; i++)
        {
            Assert.Contains(page2Buttons, b => b.Text == $"Tag{i:D2}");
        }

        // Cancel row remains visible regardless of tag page.
        Assert.Contains(page2Buttons, b => b.CallbackData == "action:cancel");
    }

    [Fact]
    public async Task ItemPagination_WhileTagPage2Showing_ResetsTagPageTo1()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        for (var i = 1; i <= 10; i++)
        {
            var item = await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
            await TagRepository.SetItemTagsAsync(new[] { item.Id }, group.Id, new[] { $"Tag{i:D2}" });
        }

        for (var i = 11; i <= ShoppingListService.ActionPageSize + 2; i++)
        {
            await ItemRepository.AddAsync(group.Id, $"Item{i}", null, "TestUser");
        }

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));
        var sent = GetLastSentMessage();
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var tagNextButton = keyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(b => b.CallbackData!.StartsWith("list_tagpage:"));

        await DispatchAsync(CallbackUpdate(-100, 42, 1, tagNextButton.CallbackData!));
        var tagPage2Edit = GetLastEditedMessage();
        var tagPage2Keyboard = Assert.IsType<InlineKeyboardMarkup>(tagPage2Edit!.ReplyMarkup);
        var itemNextButton = tagPage2Keyboard.InlineKeyboard
            .SelectMany(row => row)
            .First(b => b.CallbackData!.StartsWith("list_next:"));

        await DispatchAsync(CallbackUpdate(-100, 42, 1, itemNextButton.CallbackData!));

        var afterItemPageEdit = GetLastEditedMessage();
        Assert.NotNull(afterItemPageEdit);
        var afterKeyboard = Assert.IsType<InlineKeyboardMarkup>(afterItemPageEdit!.ReplyMarkup);
        var afterButtons = afterKeyboard.InlineKeyboard.SelectMany(row => row).ToList();

        // Tag page reset to 1: tags 1-6 visible again, not 7-10.
        for (var i = 1; i <= 6; i++)
        {
            Assert.Contains(afterButtons, b => b.Text == $"Tag{i:D2}");
        }

        Assert.DoesNotContain(afterButtons, b => b.Text == "Tag07");
    }
}
