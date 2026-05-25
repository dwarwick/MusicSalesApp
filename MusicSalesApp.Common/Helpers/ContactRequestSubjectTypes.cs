namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Subject values accepted by the mobile Contact Us form.
/// </summary>
public static class ContactRequestSubjectTypes
{
    public const string BugReport = "Bug Report";
    public const string AppSuggestion = "App Suggestion";
    public const string GeneralQuestionOrComment = "General Question / Comment";

    public static readonly string[] All =
    [
        BugReport,
        AppSuggestion,
        GeneralQuestionOrComment
    ];
}