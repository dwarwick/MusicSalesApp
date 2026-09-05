namespace MusicSalesApp.Common.Helpers;

public static class AppPageRoutes
{
    public const string Home = "/";
    public const string CreatorSettings = "/CreatorSettings";
    public const string CreatorAgreement = "/creator-agreement";
    public const string CreatorDashboard = "/creator/dashboard";
    public const string CreatorSongs = "/creator/songs";
    public const string CreatorPersonas = "/creator/personas";

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
    /// dialog and already owns what happens when a run finishes. Preview Lyrics is only where the
    /// creator FINDS OUT the words are wrong - hearing them against the song is the one way to
    /// notice - so it needs to point at the fix, not own it.
    /// </remarks>
    public static string CreatorSongsReplaceLyrics(int songMetadataId) =>
        $"{CreatorSongs}?{CreatorSongsQueryKeys.ReplaceLyrics}={songMetadataId}";
    public const string NewCreatorSignup = "/new-creator-signup";
    public const string NewCreatorSignupQuestions = "/new-creator-signup-questions";
    public const string Login = "/login";
    public const string ManageAccount = "/manage-account";

    /// <summary>
    /// The subscription section of Manage Account. The id half is the anchor rendered on
    /// that page, so both ends of the link come from here rather than from two literals
    /// that can drift apart silently - a wrong fragment does not error, it just does
    /// nothing, which is the worst way for this to break.
    /// </summary>
    public const string ManageAccountSubscriptionSection = "subscription";
    public const string ManageAccountSubscription = ManageAccount + "#" + ManageAccountSubscriptionSection;
    public const string CreatorFollowers = "/creator/followers";
    public const string AdminArtistMessages = "/admin/artist-messages";

    /// <summary>
    /// The followed-artists and artist-messages sections of Manage Account. Same reasoning as
    /// <see cref="ManageAccountSubscriptionSection"/>: the id half is the anchor the page renders,
    /// so a link and its target cannot drift apart. Note the nav rail must build these through the
    /// page's SectionLink helper - a bare "#following" href resolves against &lt;base href="/"&gt;
    /// and navigates to the HOME page carrying the fragment.
    /// </summary>
    public const string ManageAccountFollowingSection = "following";
    public const string ManageAccountFollowing = ManageAccount + "#" + ManageAccountFollowingSection;
    public const string ManageAccountArtistMessagesSection = "artist-messages";
    public const string ManageAccountArtistMessages = ManageAccount + "#" + ManageAccountArtistMessagesSection;
    public const string MusicLibrary = "/music-library";
    public const string Register = "/register";
    public const string RefreshSignIn = "/account/refresh-signin";
    public const string SubmitTaxForm = "/submittaxform";
    public const string UploadFiles = "/upload-files";
    public const string ValidateEmail = "/validate-email";
}
