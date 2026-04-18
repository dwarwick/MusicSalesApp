namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for song report reasons used in the ReportedSongs table.
/// Always use these constants instead of inline strings to avoid mismatches.
/// </summary>
public static class ReportReasonTypes
{
    public const string CopyrightViolation = "Copyright Violation";
    public const string TermsOfUseViolation = "Terms of Use Violation";

    /// <summary>
    /// All valid report reasons. Used for input validation.
    /// </summary>
    public static readonly string[] All = [CopyrightViolation, TermsOfUseViolation];
}
