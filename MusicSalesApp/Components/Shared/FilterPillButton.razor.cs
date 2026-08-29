using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Shared;

public partial class FilterPillButtonModel : BlazorBase
{
    [Parameter, EditorRequired]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Drives the accent fill. Not interchangeable with <see cref="Count"/>: the title and music
    /// type pills are active without having a count to show.
    /// </summary>
    [Parameter]
    public bool IsActive { get; set; }

    /// <summary>Rendered as the badge when greater than zero. Null on the single-value pills.</summary>
    [Parameter]
    public int? Count { get; set; }

    /// <summary>Only flips the arrow glyph - the caller owns the open/closed state.</summary>
    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public string AriaLabel { get; set; }

    /// <summary>
    /// Tooltip on the ✕. Defaults to the label, which reads correctly for a pill whose label is a
    /// fixed noun, but not for one whose label changes with the selection - the music type pill
    /// reads "Any AI" when active, and "Clear Any AI filters" is not what that means.
    /// </summary>
    [Parameter]
    public string ClearTitle { get; set; }

    protected string ResolvedClearTitle => ClearTitle ?? $"Clear {Label} filters";

    [Parameter]
    public EventCallback OnClick { get; set; }

    /// <summary>When unset, no ✕ is rendered even while the pill is active.</summary>
    [Parameter]
    public EventCallback OnClear { get; set; }
}
