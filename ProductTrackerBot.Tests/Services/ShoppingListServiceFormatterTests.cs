using ProductTrackerBot.Localization;
using Moq;
using ProductTrackerBot.Localization;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;

namespace ProductTrackerBot.Tests.Services;

public class ShoppingListServiceFormatterTests
{
    private ShoppingListService CreateService(IReadOnlyList<ShoppingItem>? items = null)
    {
        var groupRepo = new Mock<GroupRepository>("Data Source=file::memory:?cache=shared");
        groupRepo.Setup(r => r.GetOrCreateAsync(It.IsAny<long>()))
            .ReturnsAsync(new Group { Id = 10, ChatId = 1, ListMessageId = null });

        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        itemRepo.Setup(r => r.GetAllAsync(10))
            .ReturnsAsync(items ?? new List<ShoppingItem>());

        var localizer = CreateLocalizerMock();
        return new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);
    }

    private static Mock<ILocalizer> CreateLocalizerMock()
    {
        var mock = new Mock<ILocalizer>();
        mock.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>()))
            .Returns((long _, string key) => key);
        mock.Setup(l => l.Get(It.IsAny<long>(), "pagination_next_button")).Returns("Next");
        mock.Setup(l => l.Get(It.IsAny<long>(), "pagination_previous_button")).Returns("Previous");
        mock.Setup(l => l.Get(It.IsAny<long>(), "pagination_page_label")).Returns("Page");
        mock.Setup(l => l.Get(It.IsAny<long>(), "pagination_of_label")).Returns("of");
        mock.Setup(l => l.Get(It.IsAny<long>(), "pagination_items_label")).Returns("total items");
        return mock;
    }

    [Fact]
    public async Task Formatter_SinglePageNoPagination_NoNavigationButtons()
    {
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Item1", Quantity = null, AddedByName = "User1" },
        };
        var service = CreateService(items);

        var (_, keyboard, _) = await service.BuildListAsync(1, pageNumber: 1);

        Assert.NotNull(keyboard);
        var buttonRows = keyboard!.InlineKeyboard.ToList();
        // Should have 1 item row + 1 Cancel row, no pagination row
        Assert.Equal(2, buttonRows.Count);
        Assert.DoesNotContain(buttonRows, row => row.Any(b => b.Text == "Next" || b.Text == "Previous"));
    }

    [Fact]
    public async Task Formatter_FirstPageMultiplePages_ShowsNextButton()
    {
        var items = Enumerable.Range(1, 15)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", Quantity = null, AddedByName = "User1" })
            .ToList();
        var service = CreateService(items);

        var (_, keyboard, _) = await service.BuildListAsync(1, pageNumber: 1);

        Assert.NotNull(keyboard);
        var allButtons = keyboard!.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(allButtons, b => b.Text == "Next");
        Assert.DoesNotContain(allButtons, b => b.Text == "Previous");
    }

    [Fact]
    public async Task Formatter_MiddlePageMultiplePages_ShowsBothButtons()
    {
        var items = Enumerable.Range(1, 25)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", Quantity = null, AddedByName = "User1" })
            .ToList();
        var service = CreateService(items);

        var (_, keyboard, _) = await service.BuildListAsync(1, pageNumber: 2);

        Assert.NotNull(keyboard);
        var allButtons = keyboard!.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(allButtons, b => b.Text == "Previous");
        Assert.Contains(allButtons, b => b.Text == "Next");
    }

    [Fact]
    public async Task Formatter_LastPageMultiplePages_ShowsPreviousButton()
    {
        var items = Enumerable.Range(1, ShoppingListService.ActionPageSize + 5)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", Quantity = null, AddedByName = "User1" })
            .ToList();
        var lastPage = (int)Math.Ceiling(items.Count / (double)ShoppingListService.ActionPageSize);
        var service = CreateService(items);

        var (_, keyboard, _) = await service.BuildListAsync(1, pageNumber: lastPage);

        Assert.NotNull(keyboard);
        var allButtons = keyboard!.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(allButtons, b => b.Text == "Previous");
        Assert.DoesNotContain(allButtons, b => b.Text == "Next");
    }

    [Fact]
    public async Task Formatter_PageFooter_DisplaysCorrectPageInfo()
    {
        var items = Enumerable.Range(1, 25)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", Quantity = null, AddedByName = "User1" })
            .ToList();
        var totalPages = (int)Math.Ceiling(items.Count / (double)ShoppingListService.ActionPageSize);
        var service = CreateService(items);

        var (text, _, _) = await service.BuildListAsync(1, pageNumber: 2);

        Assert.Contains($"Page 2 of {totalPages}", text);
        Assert.Contains("(25 total items)", text);
    }

    [Fact]
    public async Task Formatter_CallbackData_ContainsValidGroupIdAndPageNumber()
    {
        var items = Enumerable.Range(1, 15)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", Quantity = null, AddedByName = "User1" })
            .ToList();
        var service = CreateService(items);

        var (_, keyboard, _) = await service.BuildListAsync(chatId: 1, pageNumber: 1);

        Assert.NotNull(keyboard);
        var nextButton = keyboard!.InlineKeyboard.SelectMany(row => row).First(b => b.Text.Contains("Next"));
        Assert.NotNull(nextButton.CallbackData);
        // Format should be list_next:chatId:pageNumber
        Assert.StartsWith("list_next:1:", nextButton.CallbackData);
    }

    [Fact]
    public async Task BulletList_SinglePage_AllItemsAppearInMessageText()
    {
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Milk", Quantity = null, AddedByName = "User1" },
            new() { Id = 2, GroupId = 10, Name = "Bread", Quantity = null, AddedByName = "User1" },
            new() { Id = 3, GroupId = 10, Name = "Eggs", Quantity = null, AddedByName = "User1" },
        };
        var service = CreateService(items);

        var (text, _, _) = await service.BuildListAsync(1, pageNumber: 1);

        Assert.Contains("• Milk", text);
        Assert.Contains("• Bread", text);
        Assert.Contains("• Eggs", text);
    }

    [Fact]
    public async Task BulletList_ItemWithQuantity_ShowsQuantityInParentheses()
    {
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Milk", Quantity = "2L", AddedByName = "User1" },
        };
        var service = CreateService(items);

        var (text, _, _) = await service.BuildListAsync(1, pageNumber: 1);

        Assert.Contains("• Milk (2L)", text);
    }

    [Fact]
    public async Task BulletList_Paginated_ShowsAllItemsInText()
    {
        var items = Enumerable.Range(1, 15)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i:D2}", Quantity = null, AddedByName = "User1" })
            .ToList();
        var service = CreateService(items);

        var (text, _, _) = await service.BuildListAsync(1, pageNumber: 2);

        // Text should show ALL items regardless of pagination
        Assert.Contains("• Item01", text);
        Assert.Contains("• Item10", text);
        Assert.Contains("• Item11", text);
        Assert.Contains("• Item15", text);
    }

    [Fact]
    public async Task BulletList_LargeList_MessageStaysUnder4096Chars()
    {
        var items = Enumerable.Range(1, 10)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"LongItemName{i}", Quantity = "500g", AddedByName = "User1" })
            .ToList();
        var service = CreateService(items);

        var (text, keyboard, _) = await service.BuildListAsync(1, pageNumber: 1);

        var keyboardText = keyboard?.InlineKeyboard
            .SelectMany(row => row.Select(b => b.Text + b.CallbackData))
            .Aggregate("", (acc, s) => acc + s) ?? string.Empty;
        Assert.True(text.Length + keyboardText.Length < 4096);
    }

    [Fact]
    public async Task BulletList_KeyboardStillPresent_AfterBulletListInText()
    {
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Apple", Quantity = null, AddedByName = "User1" },
        };
        var service = CreateService(items);

        var (text, keyboard, _) = await service.BuildListAsync(1, pageNumber: 1);

        Assert.Contains("• Apple", text);
        Assert.NotNull(keyboard);
        // item row + Cancel row
        Assert.Equal(2, keyboard!.InlineKeyboard.Count());
        Assert.Equal("shop:sel:1:1::1", keyboard.InlineKeyboard.First().First().CallbackData);
    }
}
