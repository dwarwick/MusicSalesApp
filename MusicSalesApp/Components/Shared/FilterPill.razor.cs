using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Shared;

public partial class FilterPillModel : BlazorBase
{
    [Parameter, EditorRequired]
    public string Label { get; set; } = string.Empty;

    [Parameter]
    public string SearchPlaceholder { get; set; } = string.Empty;

    [Parameter, EditorRequired]
    public Dictionary<string, int> Items { get; set; } = new();

    [Parameter, EditorRequired]
    public HashSet<string> SelectedItems { get; set; } = new();

    [Parameter]
    public EventCallback<(string item, bool isChecked)> OnItemToggled { get; set; }

    [Parameter]
    public EventCallback OnCleared { get; set; }

    protected bool _dropdownOpen;
    protected FilterPillListBody _body;

    protected void ToggleDropdown()
    {
        _dropdownOpen = !_dropdownOpen;
    }

    protected async Task HandleToggle((string item, bool isChecked) args)
    {
        await OnItemToggled.InvokeAsync(args);
    }

    protected async Task HandleClear()
    {
        _body?.ResetSearch();
        _dropdownOpen = false;
        await OnCleared.InvokeAsync();
    }
}
