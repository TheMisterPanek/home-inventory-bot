using ProductTrackerBot.Localization;
using Moq;
using ProductTrackerBot.Localization;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using ProductTrackerBot.Services;

namespace ProductTrackerBot.Tests.Services;

public class ShoppingListServiceTests
{
    [Fact]
    public async Task Empty_List_Should_Return_Empty_Message_With_No_Keyboard()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(new List<ShoppingItem>().AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (text, keyboard, group) = await service.BuildListAsync(1);

        Assert.Equal("list.empty", text);
        Assert.Null(keyboard);
        Assert.Equal(10, group.Id);
    }

    // TDD — bug #4: when a tag filter matches zero active items (e.g. the last item carrying that
    // tag was bought between render and tap), BuildListAsync must still return a keyboard with an
    // "All" button so the user can clear the filter. Returning a null keyboard strands them.
    [Fact]
    public async Task Filtered_View_With_No_Matches_Should_Offer_All_Button()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Иван", Tags = new[] { "Еда" } },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(new[] { "Еда" });
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, localizer.Object);

        // Filter by a tag that no active item carries.
        var (_, keyboard, _) = await service.BuildListAsync(1, 1, new[] { "Химия" });

        Assert.NotNull(keyboard);
        Assert.Contains(
            keyboard!.InlineKeyboard.SelectMany(row => row),
            btn => btn.CallbackData == "list_filter:1:-1:1:1");
    }

    [Fact]
    public async Task Single_Item_Should_Produce_List_With_One_Row()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", Quantity = "2л", AddedByName = "Иван" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (text, keyboard, group) = await service.BuildListAsync(1);

        Assert.Contains("list.header", text);
        Assert.Contains("Молоко (2л)", text);
        Assert.NotNull(keyboard);
        // keyboard has item row + Cancel row
        Assert.Equal(2, keyboard!.InlineKeyboard.Count());

        var row = keyboard.InlineKeyboard.First();
        var button = Assert.Single(row);
        Assert.Equal("Молоко 2л", button.Text);
        Assert.Equal("shop:sel:1:1::1", button.CallbackData);
    }

    [Fact]
    public async Task Multiple_Items_Should_Have_Multiple_Rows()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", Quantity = "2л", AddedByName = "Иван" },
            new() { Id = 2, GroupId = 10, Name = "Хлеб", Quantity = null, AddedByName = "Мария" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (text, keyboard, _) = await service.BuildListAsync(1);

        Assert.Contains("Молоко (2л)", text);
        Assert.Contains("Хлеб", text);
        Assert.NotNull(keyboard);
        // keyboard has 2 item rows + Cancel row
        Assert.Equal(3, keyboard!.InlineKeyboard.Count());

        // Verify callback data for both items
        var firstRow = keyboard.InlineKeyboard.First();
        Assert.Equal("shop:sel:1:1::1", Assert.Single(firstRow).CallbackData);

        var secondRow = keyboard.InlineKeyboard.ElementAt(1);
        Assert.Equal("shop:sel:2:1::1", Assert.Single(secondRow).CallbackData);
    }

    [Fact]
    public async Task Item_Without_Quantity_Should_Not_Show_Quantity_In_Text()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Хлеб", Quantity = null, AddedByName = "Мария" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (text, keyboard, _) = await service.BuildListAsync(1);

        Assert.Contains("• Хлеб", text); // Should not have quantity after the name
        Assert.Equal("Хлеб", keyboard!.InlineKeyboard.First().First().Text);
    }

    [Fact]
    public async Task GetPagedItemsAsync_ValidPageNumber_ReturnsCorrectSlice()
    {
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Item1", Quantity = null, AddedByName = "User1" },
            new() { Id = 2, GroupId = 10, Name = "Item2", Quantity = null, AddedByName = "User1" },
            new() { Id = 3, GroupId = 10, Name = "Item3", Quantity = null, AddedByName = "User1" },
            new() { Id = 4, GroupId = 10, Name = "Item4", Quantity = null, AddedByName = "User1" },
            new() { Id = 5, GroupId = 10, Name = "Item5", Quantity = null, AddedByName = "User1" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(new Mock<GroupRepository>("Data Source=file::memory:?cache=shared").Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (pagedItems, totalItems, totalPages, actualPage) = await service.GetPagedItemsAsync(10, pageNumber: 1, pageSize: 2);

        Assert.Equal(2, pagedItems.Count);
        Assert.Equal(1, pagedItems[0].Id);
        Assert.Equal(2, pagedItems[1].Id);
        Assert.Equal(5, totalItems);
        Assert.Equal(3, totalPages);
        Assert.Equal(1, actualPage);
    }

    [Fact]
    public async Task GetPagedItemsAsync_PageExceedsMax_DefaultsToPage1()
    {
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Item1", Quantity = null, AddedByName = "User1" },
            new() { Id = 2, GroupId = 10, Name = "Item2", Quantity = null, AddedByName = "User1" },
            new() { Id = 3, GroupId = 10, Name = "Item3", Quantity = null, AddedByName = "User1" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(new Mock<GroupRepository>("Data Source=file::memory:?cache=shared").Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (pagedItems, totalItems, totalPages, actualPage) = await service.GetPagedItemsAsync(10, pageNumber: 99, pageSize: 2);

        Assert.Equal(2, pagedItems.Count);
        Assert.Equal(1, pagedItems[0].Id);
        Assert.Equal(2, pagedItems[1].Id);
        Assert.Equal(3, totalItems);
        Assert.Equal(2, totalPages);
        Assert.Equal(1, actualPage);
    }

    [Fact]
    public async Task GetPagedItemsAsync_EmptyList_ReturnsEmptyItemsAndPageCountOf1()
    {
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(new List<ShoppingItem>().AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(new Mock<GroupRepository>("Data Source=file::memory:?cache=shared").Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (pagedItems, totalItems, totalPages, actualPage) = await service.GetPagedItemsAsync(10, pageNumber: 1, pageSize: 10);

        Assert.Empty(pagedItems);
        Assert.Equal(0, totalItems);
        Assert.Equal(1, totalPages);
        Assert.Equal(1, actualPage);
    }

    [Fact]
    public async Task GetPagedItemsAsync_ExactlyPageSize_ReturnsSinglePage()
    {
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = Enumerable.Range(1, 10)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", Quantity = null, AddedByName = "User1" })
            .ToList();
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var localizer = CreateLocalizerMock();

        var service = new ShoppingListService(new Mock<GroupRepository>("Data Source=file::memory:?cache=shared").Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);

        var (pagedItems, totalItems, totalPages, actualPage) = await service.GetPagedItemsAsync(10, pageNumber: 1, pageSize: 10);

        Assert.Equal(10, pagedItems.Count);
        Assert.Equal(10, totalItems);
        Assert.Equal(1, totalPages);
        Assert.Equal(1, actualPage);
    }

    [Fact]
    public async Task AddItemsAsync_SingleItem_NoComma_AddsOneItem()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var added = await service.AddItemsAsync("Молоко", groupId: 10, addedByName: "Ivan");
        Assert.Single(added);
        itemRepo.Verify(r => r.AddAsync(10, "Молоко", null, "Ivan", null), Times.Once);
    }

    [Fact]
    public async Task AddItemsAsync_SingleItemWithQuantity_AddsItemWithQty()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var added = await service.AddItemsAsync("Молоко 2л", groupId: 10, addedByName: "Ivan");
        Assert.Single(added);
        itemRepo.Verify(r => r.AddAsync(10, "Молоко", "2 л", "Ivan", null), Times.Once);
    }

    [Fact]
    public async Task AddItemsAsync_CsvNoQuantities_AddsThreeItems()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var added = await service.AddItemsAsync("Молоко, Яйца, Хлеб", groupId: 10, addedByName: "Ivan");
        Assert.Equal(3, added.Count);
        itemRepo.Verify(r => r.AddAsync(10, It.IsAny<string>(), null, "Ivan", null), Times.Exactly(3));
    }

    [Fact]
    public async Task AddItemsAsync_CsvWithQuantities_ParsesEachCorrectly()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var added = await service.AddItemsAsync("Молоко 2л, Яйца 6, Хлеб", groupId: 10, addedByName: "Ivan");
        Assert.Equal(3, added.Count);
        itemRepo.Verify(r => r.AddAsync(10, "Молоко", "2 л", "Ivan", null), Times.Once);
        itemRepo.Verify(r => r.AddAsync(10, "Яйца", "6", "Ivan", null), Times.Once);
        itemRepo.Verify(r => r.AddAsync(10, "Хлеб", null, "Ivan", null), Times.Once);
    }

    [Fact]
    public async Task AddItemsAsync_WhitespacePadding_TrimsCorrectly()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var added = await service.AddItemsAsync("  Молоко  ,  Яйца  ,  Хлеб  ", groupId: 10, addedByName: "Ivan");
        Assert.Equal(3, added.Count);
        itemRepo.Verify(r => r.AddAsync(10, "Молоко", null, "Ivan", null), Times.Once);
        itemRepo.Verify(r => r.AddAsync(10, "Яйца", null, "Ivan", null), Times.Once);
        itemRepo.Verify(r => r.AddAsync(10, "Хлеб", null, "Ivan", null), Times.Once);
    }

    [Fact]
    public async Task AddItemsAsync_MoreThan20Items_OnlyFirst20Added()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var csv = string.Join(", ", Enumerable.Range(1, 21).Select(i => $"Item{i}"));
        var added = await service.AddItemsAsync(csv, groupId: 10, addedByName: "Ivan");
        Assert.Equal(20, added.Count);
        itemRepo.Verify(r => r.AddAsync(10, It.IsAny<string>(), It.IsAny<string?>(), "Ivan", null), Times.Exactly(20));
    }

    [Fact]
    public async Task AddItemsAsync_EmptySegments_SkipsEmpty()
    {
        var (service, itemRepo) = CreateServiceWithItemRepo(groupId: 10);
        var added = await service.AddItemsAsync("Молоко,,Яйца", groupId: 10, addedByName: "Ivan");
        Assert.Equal(2, added.Count);
        itemRepo.Verify(r => r.AddAsync(10, It.IsAny<string>(), It.IsAny<string?>(), "Ivan", null), Times.Exactly(2));
    }

    private static (ShoppingListService Service, Mock<ShoppingItemRepository> ItemRepo) CreateServiceWithItemRepo(int groupId)
    {
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        itemRepo
            .Setup(r => r.AddAsync(groupId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<DateOnly?>()))
            .ReturnsAsync((int gid, string name, string? qty, string by, DateOnly? exp) =>
                new ShoppingItem { Id = 1, GroupId = gid, Name = name, Quantity = qty, AddedByName = by });

        var groupRepo = new Mock<GroupRepository>("Data Source=file::memory:?cache=shared");
        var localizer = CreateLocalizerMock();
        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, localizer.Object);
        return (service, itemRepo);
    }

    [Fact]
    public async Task GetPagedItemsAsync_WithTag_FiltersCaseInsensitively()
    {
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Порошок", AddedByName = "Ivan", Tags = new[] { "Химия" } },
            new() { Id = 2, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
            new() { Id = 3, GroupId = 10, Name = "Отбеливатель", AddedByName = "Ivan", Tags = new[] { "химия" } },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());

        var service = new ShoppingListService(
            new Mock<GroupRepository>("Data Source=file::memory:?cache=shared").Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, CreateLocalizerMock().Object);

        var (pagedItems, totalItems, totalPages, actualPage) = await service.GetPagedItemsAsync(10, pageNumber: 1, pageSize: 10, tagNames: new[] { "Химия" });

        Assert.Equal(2, totalItems);
        Assert.Equal(1, totalPages);
        Assert.Equal(1, actualPage);
        Assert.Equal(new[] { 1, 3 }, pagedItems.Select(i => i.Id));
    }

    [Fact]
    public async Task SelectedItem_Row_Expands_Into_Name_Bought_And_Remove_Buttons()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", Quantity = "2л", AddedByName = "Иван" },
            new() { Id = 2, GroupId = 10, Name = "Хлеб", AddedByName = "Мария" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object,
                new Mock<TagRepository>("Data Source=file::memory:").Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1, selectedItemId: 1);

        var expandedRow = keyboard!.InlineKeyboard.First().ToList();
        Assert.Equal(3, expandedRow.Count);
        Assert.Equal("Молоко 2л", expandedRow[0].Text);
        Assert.Equal("shop:sel:0:1::1", expandedRow[0].CallbackData); // tapping the name collapses the row
        Assert.Equal("list.bought", expandedRow[1].Text);
        Assert.Equal("shop:done:1:1::1", expandedRow[1].CallbackData);
        Assert.Equal("list.remove", expandedRow[2].Text);
        Assert.Equal("shop:remove:1:1::1", expandedRow[2].CallbackData);

        // The other item stays collapsed
        var collapsedRow = keyboard.InlineKeyboard.ElementAt(1);
        Assert.Equal("shop:sel:2:1::1", Assert.Single(collapsedRow).CallbackData);
    }

    [Fact]
    public async Task ItemButtons_CarryActiveFilterAndPage_InCallbackData()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 7, GroupId = 10, Name = "Белизна", AddedByName = "Ivan", Tags = new[] { "Химия" } },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(new List<string> { "Еда", "Химия" });

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1, activeTagNames: new[] { "Химия" }, selectedItemId: 7);

        var expandedRow = keyboard!.InlineKeyboard.First().ToList();
        Assert.Equal("shop:done:7:1:1:1", expandedRow[1].CallbackData);
        Assert.Equal("shop:remove:7:1:1:1", expandedRow[2].CallbackData);
    }

    [Fact]
    public async Task ResolveTagNamesAsync_MapsIndices_AndDropsStaleOnes()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(new List<string> { "Еда", "Химия" });

        var service = new ShoppingListService(
            groupRepo.Object,
            new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared").Object,
            tagRepo.Object,
            CreateLocalizerMock().Object);

        Assert.Equal(new[] { "Химия" }, await service.ResolveTagNamesAsync(10, new[] { 1, 9 }));
        Assert.Null(await service.ResolveTagNamesAsync(10, Array.Empty<int>()));
        Assert.Null(await service.ResolveTagNamesAsync(10, new[] { 42 }));
    }

    [Fact]
    public async Task BuildListAsync_NoTaggedItems_OmitsFilterRow()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Ivan" },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(Array.Empty<string>());

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1);

        // Item row + Cancel row only — no filter row
        Assert.Equal(2, keyboard!.InlineKeyboard.Count());
    }

    [Fact]
    public async Task BuildListAsync_WithTaggedItems_AddsFilterRow()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(new List<string> { "Еда" });

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1);

        // Item row + filter (tag) row + Cancel row
        Assert.Equal(3, keyboard!.InlineKeyboard.Count());
        var filterRow = keyboard.InlineKeyboard.ElementAt(1);
        Assert.Single(filterRow);
        Assert.Equal("Еда", filterRow.First().Text);
        Assert.Equal("list_filter:1:0:1:1", filterRow.First().CallbackData);
    }

    [Fact]
    public async Task BuildListAsync_WithActiveFilter_AddsAllButton()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem>
        {
            new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Ivan", Tags = new[] { "Еда" } },
        };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(new List<string> { "Еда" });

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1, activeTagNames: new[] { "Еда" });

        // Item row + tag-toggle row + All-Items row + Cancel row
        Assert.Equal(4, keyboard!.InlineKeyboard.Count());
        var tagToggleRow = keyboard.InlineKeyboard.ElementAt(1);
        Assert.Single(tagToggleRow);
        Assert.Equal("✓ Еда", tagToggleRow.First().Text);

        var allItemsRow = keyboard.InlineKeyboard.ElementAt(2);
        Assert.Single(allItemsRow);
        Assert.Equal("list_filter:1:-1:1:1", allItemsRow.First().CallbackData);
    }

    [Fact]
    public async Task Spacer_NotInserted_When_NoPaginationAndNoFilters()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem> { new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Иван" } };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(Array.Empty<string>());

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1);

        // Item row + Cancel row only — no pagination, no filters, so no spacer.
        Assert.Equal(2, keyboard!.InlineKeyboard.Count());
        Assert.DoesNotContain(keyboard.InlineKeyboard.SelectMany(row => row), btn => btn.CallbackData == "noop");
    }

    [Fact]
    public async Task PaginationRow_ShowsPageIndicator_NoFilters()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = Enumerable.Range(1, ShoppingListService.ActionPageSize + 1)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", AddedByName = "Иван" })
            .ToList();
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(Array.Empty<string>());

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1);

        // ActionPageSize item rows + pagination row (indicator + Next) + Cancel row
        var rows = keyboard!.InlineKeyboard.ToList();
        Assert.Equal(ShoppingListService.ActionPageSize + 2, rows.Count);
        var paginationRow = rows[ShoppingListService.ActionPageSize].ToList();
        Assert.Equal("1/2", paginationRow[0].Text);
        Assert.Equal("noop", paginationRow[0].CallbackData);
    }

    [Fact]
    public async Task PaginationRow_And_FilterRow_BothPresent_NoExtraRows()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = Enumerable.Range(1, ShoppingListService.ActionPageSize + 1)
            .Select(i => new ShoppingItem { Id = i, GroupId = 10, Name = $"Item{i}", AddedByName = "Иван" })
            .ToList();
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(new List<string> { "Еда" });

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1);

        // ActionPageSize item rows + pagination row + filter row + Cancel row
        var rows = keyboard!.InlineKeyboard.ToList();
        Assert.Equal(ShoppingListService.ActionPageSize + 3, rows.Count);
        Assert.Single(rows.SelectMany(row => row), btn => btn.CallbackData == "noop");
    }

    [Fact]
    public async Task FilterRow_WrapsAtTwoButtonsPerRow_With5Tags()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem> { new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Иван" } };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        var tags = new List<string> { "Tag1", "Tag2", "Tag3", "Tag4", "Tag5" };
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(tags);

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        var (_, keyboard, _) = await service.BuildListAsync(1);

        // item row + 3 wrapped tag rows (2,2,1) + Cancel row
        var rows = keyboard!.InlineKeyboard.ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(2, rows[1].Count());
        Assert.Equal(2, rows[2].Count());
        Assert.Single(rows[3]);
    }

    [Fact]
    public async Task TagPagination_MoreThanTagPageSizeTags_PagesAndClamps()
    {
        var groupRepo = CreateGroupRepoMock(chatId: 1, groupId: 10);
        var itemRepo = new Mock<ShoppingItemRepository>("Data Source=file::memory:?cache=shared");
        var items = new List<ShoppingItem> { new() { Id = 1, GroupId = 10, Name = "Молоко", AddedByName = "Иван" } };
        itemRepo.Setup(r => r.GetAllAsync(10)).ReturnsAsync(items.AsReadOnly());
        var tagRepo = new Mock<TagRepository>("Data Source=file::memory:");
        var tags = Enumerable.Range(1, 8).Select(i => $"Tag{i}").ToList();
        tagRepo.Setup(r => r.GetDistinctTagsAsync(10)).ReturnsAsync(tags);

        var service = new ShoppingListService(groupRepo.Object, itemRepo.Object, tagRepo.Object, CreateLocalizerMock().Object);

        // Page 1: tags 1-6 (3 wrapped rows), a tag-pagination row with only "Next".
        var (_, page1Keyboard, _) = await service.BuildListAsync(1);
        var page1Buttons = page1Keyboard!.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(page1Buttons, b => b.Text == "Tag1");
        Assert.Contains(page1Buttons, b => b.Text == "Tag6");
        Assert.DoesNotContain(page1Buttons, b => b.Text == "Tag7");
        Assert.Contains(page1Buttons, b => b.CallbackData!.StartsWith("list_tagpage:1:") && b.Text == "pagination_next_button");
        Assert.DoesNotContain(page1Buttons, b => b.CallbackData!.StartsWith("list_tagpage:1:") && b.Text == "pagination_previous_button");

        // Page 2: tags 7-8, pagination row shows only "Previous" (last page).
        var (_, page2Keyboard, _) = await service.BuildListAsync(1, tagPageNumber: 2);
        var page2Buttons = page2Keyboard!.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(page2Buttons, b => b.Text == "Tag7");
        Assert.Contains(page2Buttons, b => b.Text == "Tag8");
        Assert.DoesNotContain(page2Buttons, b => b.Text == "Tag1");
        Assert.Contains(page2Buttons, b => b.CallbackData!.StartsWith("list_tagpage:1:") && b.Text == "pagination_previous_button");
        Assert.DoesNotContain(page2Buttons, b => b.CallbackData!.StartsWith("list_tagpage:1:") && b.Text == "pagination_next_button");

        // Out-of-range tag page clamps back to page 1.
        var (_, clampedKeyboard, _) = await service.BuildListAsync(1, tagPageNumber: 99);
        var clampedButtons = clampedKeyboard!.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(clampedButtons, b => b.Text == "Tag1");
    }

    private static Mock<GroupRepository> CreateGroupRepoMock(long chatId, int groupId)
    {
        var mock = new Mock<GroupRepository>("Data Source=file::memory:?cache=shared");
        mock.Setup(r => r.GetOrCreateAsync(chatId))
            .ReturnsAsync(new Group { Id = groupId, ChatId = chatId, ListMessageId = null });
        return mock;
    }

    private static Mock<ILocalizer> CreateLocalizerMock()
    {
        var mock = new Mock<ILocalizer>();
        mock.Setup(l => l.Get(It.IsAny<long>(), It.IsAny<string>()))
            .Returns((long _, string key) => key);
        return mock;
    }
}
