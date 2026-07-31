#nullable enable
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Shared;

/// <summary>
/// Renders a piece of cover art, letting the browser pick the smallest pre-resized rendition that
/// still covers the space the image occupies at the current viewport and pixel density.
///
/// <para>
/// The fallback behaviour is the point: when <see cref="CoverArtSource.HasVariants"/> is false -
/// before the backfill has run, or when generation failed for one image - no <c>srcset</c> or
/// <c>sizes</c> attribute is emitted at all and the markup is exactly what the site rendered
/// before renditions existed. That is what makes this safe to deploy ahead of the backfill.
/// </para>
/// </summary>
public partial class CoverArt : ComponentBase
{
    /// <summary>The URL set, built by <see cref="ICoverArtUrlBuilder"/>.</summary>
    [Parameter, EditorRequired]
    public CoverArtSource Source { get; set; } = CoverArtSource.None;

    /// <summary>
    /// How wide the image will render. Use a preset from <see cref="CoverArtSizes"/> rather than
    /// inventing one, so the value stays in step with the stylesheets.
    /// </summary>
    [Parameter]
    public string Sizes { get; set; } = CoverArtSizes.Card;

    [Parameter]
    public string? CssClass { get; set; }

    [Parameter]
    public string Alt { get; set; } = "Album art";

    /// <summary>
    /// Set for artwork above the fold - the player hero - so it is not deferred. Everything in a
    /// scrolling list should stay lazy.
    /// </summary>
    [Parameter]
    public bool Eager { get; set; }

    /// <summary>
    /// Intrinsic dimensions, emitted so the browser can reserve the box before the image arrives
    /// and avoid a layout shift. Omit for the fluid card art, whose height comes from
    /// <c>aspect-ratio</c>.
    /// </summary>
    [Parameter]
    public int? IntrinsicWidth { get; set; }

    [Parameter]
    public int? IntrinsicHeight { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// The candidate list, or null to omit the attribute entirely. Widths are declared with the
    /// <c>w</c> descriptor, which is what lets the browser combine them with <c>sizes</c> and the
    /// device pixel ratio to choose.
    /// </summary>
    protected string? SrcSet => Source.HasVariants
        ? string.Join(", ", Source.Variants.Select(v => $"{v.Url} {v.Width}w"))
        : null;

    /// <summary>Meaningless without candidates to choose between, so it travels with the srcset.</summary>
    protected string? SizesAttribute => Source.HasVariants ? Sizes : null;

    protected string LoadingAttribute => Eager ? "eager" : "lazy";
}
