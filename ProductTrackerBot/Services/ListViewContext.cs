// <copyright file="ListViewContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ProductTrackerBot.Services;

/// <summary>
/// The rendering context of a /list message — which item page, which tag filters, and which tag page it
/// was showing. Item action buttons (select / bought / remove) carry it in their callback data so the
/// handler can re-render the same view instead of resetting the user back to page 1 unfiltered.
/// </summary>
/// <param name="PageNumber">The item page number (1-based).</param>
/// <param name="TagIndices">The zero-based indices of the active tag filters (empty = unfiltered).</param>
/// <param name="TagPageNumber">The tag-filter page number (1-based).</param>
public readonly record struct ListViewContext(int PageNumber, IReadOnlyList<int> TagIndices, int TagPageNumber)
{
    /// <summary>
    /// Gets the context of a freshly opened, unfiltered list: first item page, no filters, first tag page.
    /// </summary>
    public static ListViewContext Default => new(1, Array.Empty<int>(), 1);

    /// <summary>
    /// Gets the active tag indices rendered as the comma-separated form used in callback data.
    /// </summary>
    public string TagIndexCsv => string.Join(",", this.TagIndices);

    /// <summary>
    /// Renders the context as the "{page}:{tagCsv}:{tagPage}" callback-data suffix.
    /// </summary>
    /// <returns>The suffix, without a leading separator.</returns>
    public string ToCallbackSuffix() => $"{this.PageNumber}:{this.TagIndexCsv}:{this.TagPageNumber}";

    /// <summary>
    /// Parses an item action callback ("{prefix}{itemId}[:{page}:{tagCsv}:{tagPage}]"). The context suffix is
    /// optional: callbacks in list messages rendered before it existed still resolve, falling back to
    /// <see cref="Default"/>.
    /// </summary>
    /// <param name="callbackData">The full callback data.</param>
    /// <param name="prefix">The handler's callback prefix (e.g. "shop:done:").</param>
    /// <param name="itemId">The parsed item ID (0 means "no item", used to collapse a selected row).</param>
    /// <param name="context">The parsed view context.</param>
    /// <returns><c>true</c> when the item ID parsed; <c>false</c> when the callback is malformed.</returns>
    public static bool TryParse(string callbackData, string prefix, out int itemId, out ListViewContext context)
    {
        itemId = 0;
        context = Default;

        if (!callbackData.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = callbackData[prefix.Length..].Split(':');
        if (!int.TryParse(parts[0], out itemId))
        {
            return false;
        }

        if (parts.Length >= 4
            && int.TryParse(parts[1], out var pageNumber)
            && int.TryParse(parts[3], out var tagPageNumber))
        {
            var tagIndices = parts[2]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var idx) ? idx : -1)
                .Where(idx => idx >= 0)
                .ToList();

            context = new ListViewContext(pageNumber, tagIndices, tagPageNumber);
        }

        return true;
    }
}
