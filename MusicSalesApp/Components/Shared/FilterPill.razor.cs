using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Shared;

public partial class FilterPillModel : BlazorBase
{
    [Parameter]
    public string Label { get; set; } = string.Empty;

    [Parameter]
    public string SearchPlaceholder { get; set; } = string.Empty;

    [Parameter]
    public Dictionary<string, int> Items { get; set; } = new();

    [Parameter]
    public HashSet<string> SelectedItems { get; set; } = new();

    [Parameter]
    public EventCallback<(string item, bool isChecked)> OnItemToggled { get; set; }

    [Parameter]
    public EventCallback OnCleared { get; set; }

    protected bool _dropdownOpen;
    protected string _searchText = string.Empty;

    protected void ToggleDropdown()
    {
        _dropdownOpen = !_dropdownOpen;
    }

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

    protected async Task HandleClear()
    {
        _searchText = string.Empty;
        _dropdownOpen = false;
        await OnCleared.InvokeAsync();
    }
}
