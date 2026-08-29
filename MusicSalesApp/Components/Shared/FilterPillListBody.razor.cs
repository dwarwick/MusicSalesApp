using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Shared;

public partial class FilterPillListBodyModel : BlazorBase
{
    [Parameter]
    public string SearchPlaceholder { get; set; } = string.Empty;

    [Parameter, EditorRequired]
    public Dictionary<string, int> Items { get; set; } = new();

    [Parameter, EditorRequired]
    public HashSet<string> SelectedItems { get; set; } = new();

    [Parameter]
    public EventCallback<(string item, bool isChecked)> OnItemToggled { get; set; }

    protected string _searchText = string.Empty;

    /// <summary>
    /// Narrows the option list only - it never filters the songs. Selected items sort first so a
    /// choice already made does not disappear below the fold as the list shrinks.
    /// </summary>
    protected List<string> GetFilteredItems()
    {
        var items = Items.Keys.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            items = items.Where(i => i.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        return items
            .OrderByDescending(i => SelectedItems.Contains(i))
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    protected int GetItemCount(string item)
    {
        return Items.TryGetValue(item, out var count) ? count : 0;
    }

    protected async Task HandleToggle(string item, bool isChecked)
    {
        await OnItemToggled.InvokeAsync((item, isChecked));
    }

    /// <summary>Resets the option-list search, for a caller clearing the whole filter.</summary>
    public void ResetSearch() => _searchText = string.Empty;
}
