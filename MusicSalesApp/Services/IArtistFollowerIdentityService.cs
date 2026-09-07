namespace MusicSalesApp.Services;

/// <summary>
/// Allocates and renders the pseudonym a listener wears for one artist.
/// </summary>
/// <remarks>
/// This is the whole of the anonymity guarantee, so it is deliberately its own service with no
/// database of its own: the allocation is pure logic over the numbers already taken, which makes
/// the properties that matter directly testable rather than inferred from a query.
/// </remarks>
public interface IArtistFollowerIdentityService
{
    /// <summary>
    /// Picks a number for a new follower of one persona that no other follower of that persona
    /// already holds.
    /// </summary>
    /// <param name="numbersAlreadyUsedForPersona">
    /// Every <c>AnonymousListenerNumber</c> currently assigned within this persona, including
    /// those on inactive follows - a dormant row keeps its number so a re-follow reuses it.
    /// </param>
    int AllocateNumber(IReadOnlySet<int> numbersAlreadyUsedForPersona);

    /// <summary>
    /// Renders the number as the creator sees it, e.g. "Listener #4817".
    /// </summary>
    string FormatDisplayName(int anonymousListenerNumber);
}
