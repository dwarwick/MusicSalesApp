using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Configuration for encrypted-HLS delivery. Bound from the <c>Hls</c> section.
///
/// <para>
/// The threat this exists to address is a listener taking the audio, not an attacker taking the
/// storage account. Segments are AES-128 ciphertext in
/// <see cref="AzureStorageOptions.StreamingContainerName"/>, reachable with a container read SAS, so
/// the only thing that really has to be guarded is the key - and the only thing guarding it is what
/// is configured here.
/// </para>
/// </summary>
public class HlsOptions
{
    /// <summary>
    /// Base64 of the 32-byte AES-256 key that wraps every song's content key before it is stored.
    ///
    /// <para>
    /// <b>Losing this value makes the entire catalogue undecryptable</b> and forces a full re-encode,
    /// so it belongs with <c>Jwt:SecretKey</c> and the storage connection strings — in
    /// <c>appsettings.{Environment}.json</c>, which is gitignored and lives only on the server.
    /// </para>
    ///
    /// <para>
    /// Deliberately not ASP.NET Data Protection. That key ring is excluded from backup on purpose
    /// (see <c>StorageBackupService.GetConfiguredContainerNames</c>) because everything it protects
    /// is transient and regenerating it merely signs everyone out. A content key is neither
    /// transient nor reproducible, so it must not depend on a ring that is designed to be
    /// disposable.
    /// </para>
    ///
    /// <para>
    /// Rotating it is a database re-wrap, not a re-encode: the wrapped value carries a version
    /// prefix, so old and new can coexist while rows are migrated. See <c>IHlsContentKeyProtector</c>.
    /// </para>
    /// </summary>
    public string ContentKeyWrappingKey { get; set; }

    /// <summary>
    /// How long a <b>key</b> token stays valid — the token the manifest embeds in its
    /// <c>#EXT-X-KEY</c> URI, which is the only thing standing between a listener and the content
    /// key.
    ///
    /// <para>
    /// The source design suggested five seconds, on the theory that a token is dead before anyone
    /// can copy it out of dev tools. That is too tight to survive a slow mobile network or a paused
    /// debugger, and the failure mode is a listener who cannot play anything. Sixty seconds still
    /// makes a copied URL useless by the time it is pasted anywhere, because what stops sharing is
    /// that the token is bound to one song and expires at all — not that it expires
    /// <em>instantly</em>.
    /// </para>
    /// </summary>
    public TimeSpan KeyTokenLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a <b>manifest</b> token stays valid.
    ///
    /// <para>
    /// Much longer than <see cref="KeyTokenLifetime"/>, and deliberately so. A manifest URL is
    /// handed out in bulk by the catalogue endpoint long before anything is played — the mobile app
    /// fetches the whole library at launch and may play a track hours later — so a token that dies
    /// in a minute would make the listing useless. Twenty-four hours matches the SAS lifetime that
    /// listing already used, so nothing regresses.
    /// </para>
    ///
    /// <para>
    /// It is safe to be generous here because a manifest token buys very little: it names encrypted
    /// segments, and it is rejected at the key endpoint. Holding one gets you ciphertext. Everything
    /// that matters is gated by the key token instead.
    /// </para>
    /// </summary>
    public TimeSpan ManifestTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How much of a song a listener without an active subscription may hear.
    ///
    /// <para>
    /// Matches the 60 seconds the players have always enforced, but for the first time this is the
    /// real boundary rather than a courtesy: the manifest handed to a non-subscriber lists only the
    /// segments covering this window, so the rest of the song is not merely unplayed, it is not
    /// described. Until now a non-subscriber was sent the whole file and asked in JavaScript to stop.
    /// </para>
    /// </summary>
    public TimeSpan PreviewDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the read SAS stamped onto segment URLs stays valid.
    ///
    /// <para>
    /// The streaming container is <b>private</b>, like every other container in this product, so
    /// segment URLs carry a container-scoped read SAS that the manifest builder stamps on at request
    /// time. The original design called for a public container instead; both storage accounts set
    /// <c>allowBlobPublicAccess: false</c>, and that guardrail is worth more than the stable URLs
    /// public access would have bought — the premium account holds every song master and the Data
    /// Protection key rings, for Production as well as Test.
    /// </para>
    ///
    /// <para>
    /// Long enough that a SAS cannot expire part-way through a song. Unlike
    /// <see cref="KeyTokenLifetime"/> this is a <em>continuously used</em> credential: the manifest
    /// is VOD with an <c>ENDLIST</c>, so the player fetches it once and then pulls segments against
    /// these URLs for the whole track. A minute would strand every song in the catalogue about a
    /// minute in.
    /// </para>
    ///
    /// <para>
    /// <b>Must stay below <see cref="ManifestTokenLifetime"/>.</b> When a SAS does expire, the
    /// player recovers by refetching the manifest — which mints fresh segment credentials — and
    /// that only works while the manifest's own token is still valid. Set this at or above the
    /// manifest lifetime and both expire together, leaving nothing to recover with. Startup logs a
    /// warning if the two are ever configured that way round.
    /// </para>
    ///
    /// <para>
    /// A SAS in a listener's dev tools is not the leak it would be for an MP3 — it addresses AES-128
    /// ciphertext, and the key is still gated by <see cref="KeyTokenLifetime"/>.
    /// </para>
    /// </summary>
    public TimeSpan SegmentSasLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Origins allowed to fetch a content key, e.g. <c>https://streamtunes.net</c>.
    ///
    /// <para>
    /// Empty disables the check, which is correct for local development and for native clients that
    /// send no <c>Origin</c> at all. It is a defence-in-depth layer over the token, never the
    /// primary gate — a header is trivially forged by anything that is not a browser, and treating
    /// it as authentication would be a mistake.
    /// </para>
    /// </summary>
    public IList<string> AllowedKeyOrigins { get; set; } = new List<string>();

    /// <summary>True when key wrapping is configured, so packaging and playback can work at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ContentKeyWrappingKey);

    /// <summary>
    /// The streaming container name for an environment, matching how the media and staging
    /// containers are suffixed. Used by provisioning and by the settings sync script.
    /// </summary>
    public static string StreamingContainerFor(string environmentName) => environmentName switch
    {
        "Production" => MediaProcessingContainers.StreamingProduction,
        "Test" => MediaProcessingContainers.StreamingTest,
        _ => MediaProcessingContainers.StreamingLocal
    };
}
