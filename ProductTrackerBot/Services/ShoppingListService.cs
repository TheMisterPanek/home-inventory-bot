// <copyright file="ShoppingListService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ProductTrackerBot.Services;

using System.Text;
using ProductTrackerBot.Localization;
using ProductTrackerBot.Models;
using ProductTrackerBot.Repositories;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Builds shopping list message text and inline keyboards.
/// </summary>
public class ShoppingListService
{
    /// <summary>
    /// The number of items shown as interactive action-button rows per carousel page. The plain-text
    /// item listing in the message body always shows every item regardless of this page size.
    /// </summary>
    public const int ActionPageSize = 10;

    /// <summary>
    /// The number of category-filter tags shown per tag page. Groups with more tags than this page through
    /// them via a dedicated Previous/Next row rather than losing access to tags beyond the first page.
    /// </summary>
    public const int TagPageSize = 6;

    private const int BulkItemLimit = 20;

    private readonly GroupRepository groupRepository;
    private readonly ShoppingItemRepository itemRepository;
    private readonly TagRepository tagRepository;
    private readonly ILocalizer localizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingListService"/> class.
    /// </summary>
    /// <param name="groupRepository">The group repository.</param>
    /// <param name="itemRepository">The shopping item repository.</param>
    /// <param name="tagRepository">The tag repository.</param>
    /// <param name="localizer">The localizer for retrieving localized messages.</param>
    public ShoppingListService(GroupRepository groupRepository, ShoppingItemRepository itemRepository, TagRepository tagRepository, ILocalizer localizer)
    {
        this.groupRepository = groupRepository;
        this.itemRepository = itemRepository;
        this.tagRepository = tagRepository;
        this.localizer = localizer;
    }

    /// <summary>
    /// Parses a comma-separated bulk input string and adds each item to the shopping list.
    /// Trims whitespace, filters empty segments, and enforces a 20-item cap.
    /// </summary>
    /// <param name="rawCsv">The raw user input (e.g. "Молоко 2л, Яйца 6, Хлеб").</param>
    /// <param name="groupId">The owning group ID.</param>
    /// <param name="addedByName">The display name of the user adding the items.</param>
    /// <returns>The list of added shopping items.</returns>
    public virtual async Task<IReadOnlyList<ShoppingItem>> AddItemsAsync(string rawCsv, int groupId, string addedByName)
    {
        var segments = rawCsv
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Take(BulkItemLimit)
            .ToList();

        var added = new List<ShoppingItem>(segments.Count);
        foreach (var segment in segments)
        {
            var (name, quantity) = BuyInputParser.Parse(segment);
            var item = await this.itemRepository.AddAsync(groupId, name, quantity, addedByName, expDate: null);
            added.Add(item);
        }

        return added.AsReadOnly();
    }

    /// <summary>
    /// Gets paginated items with pagination metadata for a group.
    /// </summary>
    /// <param name="groupId">The group ID.</param>
    /// <param name="pageNumber">The page number (1-based; invalid numbers default to 1).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="tagNames">Optional tag filter set (OR semantics, case-insensitive); null/empty = unfiltered.</param>
    /// <returns>A tuple of (items, totalItems, totalPages, actualPageNumber).</returns>
    public virtual async Task<(IReadOnlyList<ShoppingItem> Items, int TotalItems, int TotalPages, int ActualPageNumber)> GetPagedItemsAsync(
        int groupId,
        int pageNumber,
        int pageSize,
        IReadOnlyCollection<string>? tagNames = null)
    {
        var items = await this.itemRepository.GetAllAsync(groupId);
        if (tagNames is { Count: > 0 })
        {
            items = items.Where(i => i.Tags.Any(t => tagNames.Contains(t, StringComparer.OrdinalIgnoreCase))).ToList();
        }

        var totalItems = items.Count;
        var totalPages = (totalItems + pageSize - 1) / pageSize; // Ceiling division
        if (totalPages == 0)
        {
            totalPages = 1;
        }

        var actualPageNumber = pageNumber;
        if (actualPageNumber < 1 || actualPageNumber > totalPages)
        {
            actualPageNumber = 1;
        }

        var skip = (actualPageNumber - 1) * pageSize;
        var pagedItems = items.Skip(skip).Take(pageSize).ToList();

        return (pagedItems.AsReadOnly(), totalItems, totalPages, actualPageNumber);
    }

    /// <summary>
    /// Builds the shopping list message text and inline keyboard for a group chat.
    /// </summary>
    /// <param name="chatId">The Telegram chat ID.</param>
    /// <param name="pageNumber">The page number for pagination (1-based; defaults to 1).</param>
    /// <param name="activeTagNames">Optional set of currently-active tag filters (OR semantics, case-insensitive); null/empty = unfiltered.</param>
    /// <param name="tagPageNumber">The tag-filter page number (1-based; defaults to 1). Out-of-range values clamp to 1.</param>
    /// <param name="selectedItemId">The ID of the item whose action buttons are expanded; null/0 = every row collapsed.</param>
    /// <returns>A tuple of (messageText, inlineKeyboard, group).</returns>
    public virtual async Task<(string MessageText, InlineKeyboardMarkup? Keyboard, Group Group)> BuildListAsync(
        long chatId,
        int pageNumber = 1,
        IReadOnlyCollection<string>? activeTagNames = null,
        int tagPageNumber = 1,
        int? selectedItemId = null)
    {
        var group = await this.groupRepository.GetOrCreateAsync(chatId);
        var isFiltered = activeTagNames is { Count: > 0 };

        var (pagedItems, totalItems, totalPages, actualPageNumber) = await this.GetPagedItemsAsync(group.Id, pageNumber, ActionPageSize, activeTagNames);

        var allTags = await this.tagRepository.GetDistinctTagsAsync(group.Id) ?? Array.Empty<string>();
        var activeIndices = isFiltered ? IndicesOfTags(allTags, activeTagNames!) : Array.Empty<int>();

        var totalTagPages = allTags.Count == 0 ? 1 : ((allTags.Count + TagPageSize - 1) / TagPageSize);
        var actualTagPageNumber = tagPageNumber;
        if (actualTagPageNumber < 1 || actualTagPageNumber > totalTagPages)
        {
            actualTagPageNumber = 1;
        }

        if (totalItems == 0)
        {
            if (isFiltered)
            {
                // Filtered to a set with no current matches — still offer a way back to the full list.
                var emptyFilterRows = this.BuildFilterSectionRows(chatId, group.ChatId, allTags, activeIndices, isFiltered, actualPageNumber, actualTagPageNumber, totalTagPages);
                var emptyKeyboard = new InlineKeyboardMarkup(emptyFilterRows);
                return (this.localizer.Get(chatId, "list.empty"), emptyKeyboard, group);
            }

            return (this.localizer.Get(chatId, "list.empty"), null, group);
        }

        var allItems = await this.itemRepository.GetAllAsync(group.Id);
        if (isFiltered)
        {
            allItems = allItems.Where(i => i.Tags.Any(t => activeTagNames!.Contains(t, StringComparer.OrdinalIgnoreCase))).ToList();
        }

        var sb = new StringBuilder(this.localizer.Get(chatId, "list.header") + "\n\n");
        sb.Append(FormatItemsAsText(allItems));

        var activeIndexCsv = string.Join(",", activeIndices);
        var viewSuffix = new ListViewContext(actualPageNumber, activeIndices, actualTagPageNumber).ToCallbackSuffix();

        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var itemGroup in pagedItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in itemGroup)
            {
                var btnLabel = item.Quantity is not null
                    ? $"{item.Name} {item.Quantity}"
                    : item.Name;

                if (item.ExpDate.HasValue)
                {
                    btnLabel += $" (до {item.ExpDate.Value:dd.MM})";
                }

                if (selectedItemId == item.Id)
                {
                    // Expanded row: the name collapses it again, then the bought / remove actions.
                    buttons.Add(
                    [
                        InlineKeyboardButton.WithCallbackData(btnLabel, $"shop:sel:0:{viewSuffix}"),
                        InlineKeyboardButton.WithCallbackData(this.localizer.Get(chatId, "list.bought"), $"shop:done:{item.Id}:{viewSuffix}"),
                        InlineKeyboardButton.WithCallbackData(this.localizer.Get(chatId, "list.remove"), $"shop:remove:{item.Id}:{viewSuffix}"),
                    ]);
                }
                else
                {
                    // Collapsed row: the item name alone, spanning the full keyboard width.
                    buttons.Add(
                    [
                        InlineKeyboardButton.WithCallbackData(btnLabel, $"shop:sel:{item.Id}:{viewSuffix}"),
                    ]);
                }
            }
        }

        // Add pagination buttons if there are multiple item-action pages
        if (totalPages > 1)
        {
            var paginationButtons = new List<InlineKeyboardButton>();
            if (actualPageNumber > 1)
            {
                var callbackData = isFiltered
                    ? $"list_filter:{group.ChatId}:{activeIndexCsv}:{actualPageNumber - 1}:{actualTagPageNumber}"
                    : $"list_prev:{group.ChatId}:{actualPageNumber - 1}";
                paginationButtons.Add(InlineKeyboardButton.WithCallbackData(
                    this.localizer.Get(chatId, "pagination_previous_button"),
                    callbackData));
            }

            paginationButtons.Add(InlineKeyboardButton.WithCallbackData($"{actualPageNumber}/{totalPages}", "noop"));

            if (actualPageNumber < totalPages)
            {
                var callbackData = isFiltered
                    ? $"list_filter:{group.ChatId}:{activeIndexCsv}:{actualPageNumber + 1}:{actualTagPageNumber}"
                    : $"list_next:{group.ChatId}:{actualPageNumber + 1}";
                paginationButtons.Add(InlineKeyboardButton.WithCallbackData(
                    this.localizer.Get(chatId, "pagination_next_button"),
                    callbackData));
            }

            buttons.Add(paginationButtons);
        }

        // Add tag filter section (only when the group has at least one tagged active item)
        var filterRows = allTags.Count > 0
            ? this.BuildFilterSectionRows(chatId, group.ChatId, allTags, activeIndices, isFiltered, actualPageNumber, actualTagPageNumber, totalTagPages)
            : new List<List<InlineKeyboardButton>>();

        if (filterRows.Count > 0)
        {
            buttons.AddRange(filterRows);
        }

        // Add page footer
        var pageLabel = this.localizer.Get(chatId, "pagination_page_label");
        var ofLabel = this.localizer.Get(chatId, "pagination_of_label");
        var itemsLabel = this.localizer.Get(chatId, "pagination_items_label");
        sb.AppendLine($"\n{pageLabel} {actualPageNumber} {ofLabel} {totalPages} ({totalItems} {itemsLabel})");
        if (totalTagPages > 1)
        {
            var tagsLabel = this.localizer.Get(chatId, "list.tags-label");
            sb.AppendLine($"{tagsLabel}: {pageLabel} {actualTagPageNumber} {ofLabel} {totalTagPages}");
        }

        // Add Cancel button as the last row
        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(this.localizer.Get(chatId, "action.cancel"), "action:cancel"),
        ]);

        return (sb.ToString(), new InlineKeyboardMarkup(buttons), group);
    }

    /// <summary>
    /// Resolves the tag names behind the zero-based indices carried in callback data, dropping indices that
    /// no longer point at a tag (the tag set shifts as items are bought).
    /// </summary>
    /// <param name="groupId">The group ID.</param>
    /// <param name="tagIndices">The zero-based tag indices.</param>
    /// <returns>The resolved tag names, or null when nothing resolves (i.e. an unfiltered view).</returns>
    public virtual async Task<IReadOnlyCollection<string>?> ResolveTagNamesAsync(int groupId, IReadOnlyList<int> tagIndices)
    {
        if (tagIndices.Count == 0)
        {
            return null;
        }

        var allTags = await this.tagRepository.GetDistinctTagsAsync(groupId) ?? Array.Empty<string>();
        var resolved = tagIndices
            .Where(idx => idx >= 0 && idx < allTags.Count)
            .Select(idx => allTags[idx])
            .ToList();

        return resolved.Count > 0 ? resolved : null;
    }

    private List<List<InlineKeyboardButton>> BuildFilterSectionRows(
        long chatId,
        long groupChatId,
        IReadOnlyList<string> allTags,
        IReadOnlyList<int> activeIndices,
        bool isFiltered,
        int itemPageNumber,
        int actualTagPageNumber,
        int totalTagPages)
    {
        var rows = new List<List<InlineKeyboardButton>>();
        var activeIndexCsv = string.Join(",", activeIndices);
        var skip = (actualTagPageNumber - 1) * TagPageSize;
        var take = Math.Min(TagPageSize, allTags.Count - skip);

        var tagButtons = new List<InlineKeyboardButton>();
        for (int i = skip; i < skip + take; i++)
        {
            var isActive = activeIndices.Contains(i);
            var newIndices = isActive
                ? activeIndices.Where(x => x != i).OrderBy(x => x)
                : activeIndices.Append(i).OrderBy(x => x);
            var newCsv = string.Join(",", newIndices);

            var label = TruncateCategoryLabel(allTags[i]);
            if (isActive)
            {
                label = $"✓ {label}";
            }

            tagButtons.Add(InlineKeyboardButton.WithCallbackData(
                label,
                $"list_filter:{groupChatId}:{newCsv}:1:{actualTagPageNumber}"));
        }

        foreach (var chunk in tagButtons.Chunk(2))
        {
            rows.Add(chunk.ToList());
        }

        if (totalTagPages > 1)
        {
            var tagPaginationButtons = new List<InlineKeyboardButton>();
            if (actualTagPageNumber > 1)
            {
                tagPaginationButtons.Add(InlineKeyboardButton.WithCallbackData(
                    this.localizer.Get(chatId, "pagination_previous_button"),
                    $"list_tagpage:{groupChatId}:{activeIndexCsv}:{itemPageNumber}:{actualTagPageNumber - 1}"));
            }

            tagPaginationButtons.Add(InlineKeyboardButton.WithCallbackData($"{actualTagPageNumber}/{totalTagPages}", "noop"));

            if (actualTagPageNumber < totalTagPages)
            {
                tagPaginationButtons.Add(InlineKeyboardButton.WithCallbackData(
                    this.localizer.Get(chatId, "pagination_next_button"),
                    $"list_tagpage:{groupChatId}:{activeIndexCsv}:{itemPageNumber}:{actualTagPageNumber + 1}"));
            }

            rows.Add(tagPaginationButtons);
        }

        if (isFiltered)
        {
            rows.Add(
            [
                InlineKeyboardButton.WithCallbackData(this.localizer.Get(chatId, "list.filter-all"), $"list_filter:{groupChatId}:-1:1:1"),
            ]);
        }

        return rows;
    }

    /// <summary>
    /// Resolves the zero-based indices of <paramref name="tagNames"/> within <paramref name="allTags"/>
    /// (case-insensitive), dropping any names no longer present.
    /// </summary>
    private static IReadOnlyList<int> IndicesOfTags(IReadOnlyList<string> allTags, IReadOnlyCollection<string> tagNames)
    {
        var result = new List<int>();
        for (int i = 0; i < allTags.Count; i++)
        {
            if (tagNames.Contains(allTags[i], StringComparer.OrdinalIgnoreCase))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>
    /// Truncates a category name for display on a button label. The full, untruncated name must always
    /// be the value persisted to storage — truncation is a rendering concern only.
    /// </summary>
    /// <param name="category">The full category name.</param>
    /// <returns>The truncated label.</returns>
    internal static string TruncateCategoryLabel(string category) =>
        category.Length > 20 ? category[..20] + "…" : category;

    private static string FormatItemsAsText(IReadOnlyList<ShoppingItem> items)
    {
        var sb = new StringBuilder();
        foreach (var itemGroup in items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var groupItems = itemGroup.ToList();
            var quantities = groupItems
                .Where(i => i.Quantity is not null)
                .Select(i => i.Quantity!);
            var label = groupItems[0].Name;
            if (quantities.Any())
            {
                label += $" ({string.Join(", ", quantities)})";
            }

            var firstItem = groupItems[0];
            if (firstItem.ExpDate.HasValue)
            {
                label += $" (до {firstItem.ExpDate.Value:dd.MM})";
            }

            if (firstItem.ExpDate.HasValue && firstItem.ExpDate.Value <= DateOnly.FromDateTime(DateTime.Now))
            {
                sb.Append("⚠️ ");
            }

            sb.AppendLine($"• {label}");
        }

        return sb.ToString();
    }
}
