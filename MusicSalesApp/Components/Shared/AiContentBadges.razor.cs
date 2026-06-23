using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Shared;

public partial class AiContentBadgesModel : BlazorBase
{
    [Parameter] public bool IsAiMusic { get; set; }
    [Parameter] public bool IsAiVocals { get; set; }
    [Parameter] public bool IsAiLyrics { get; set; }
    [Parameter] public string CssClass { get; set; } = string.Empty;

    protected bool HasAnyAiDisclosure => IsAiMusic || IsAiVocals || IsAiLyrics;
}
