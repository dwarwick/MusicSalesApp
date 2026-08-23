namespace MusicSalesApp.Common.Helpers;

public static class AppPageRoutes
{
    public const string Home = "/";
    public const string CreatorSettings = "/CreatorSettings";
    public const string CreatorAgreement = "/creator-agreement";
    public const string CreatorDashboard = "/creator/dashboard";
    public const string CreatorSongs = "/creator/songs";

    /// <summary>
    /// The lyric timing editor for one song. Built here rather than written out at each call site
    /// so the page's <c>@page</c> directive and the link in the completion email cannot drift apart -
    /// a broken link in that email leaves a creator with timings they were told about and no way to
    /// reach them.
    /// </summary>
    public static string CreatorSongLyrics(int songMetadataId) =>
        $"{CreatorSongs}/{songMetadataId}/lyrics";

    /// <summary>
    /// The songs grid, asking it to open the paste box for one song so its words can be replaced.
    /// </summary>
    /// <remarks>
    /// A round trip rather than a paste box on the timing page, because the grid already hosts that
    /// dialog and already owns what happens when a run finishes. Preview Results is only where the
    /// creator FINDS OUT the words are wrong - hearing them against the song is the one way to
    /// notice - so it needs to point at the fix, not own it.
    /// </remarks>
    public static string CreatorSongsReplaceLyrics(int songMetadataId) =>
        $"{CreatorSongs}?{CreatorSongsQueryKeys.ReplaceLyrics}={songMetadataId}";
    public const string NewCreatorSignup = "/new-creator-signup";
    public const string NewCreatorSignupQuestions = "/new-creator-signup-questions";
    public const string Login = "/login";
    public const string ManageAccount = "/manage-account";
    public const string MusicLibrary = "/music-library";
    public const string Register = "/register";
    public const string RefreshSignIn = "/account/refresh-signin";
    public const string SubmitTaxForm = "/submittaxform";
    public const string UploadFiles = "/upload-files";
    public const string ValidateEmail = "/validate-email";
}
