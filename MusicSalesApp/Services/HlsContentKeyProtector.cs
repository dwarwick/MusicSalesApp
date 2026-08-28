#nullable enable
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Wraps and unwraps a song's 16-byte AES-128 content key for storage.
///
/// <para>
/// The stored form is authenticated (AES-256-GCM) and bound to the song it belongs to, so a wrapped
/// key copied from one row into another fails to unwrap rather than decrypting the wrong song. The
/// binding costs nothing and removes a whole class of "the database was edited" failure.
/// </para>
/// </summary>
public interface IHlsContentKeyProtector
{
    /// <summary>True when a usable wrapping key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Wraps <paramref name="contentKey"/> for storage against <paramref name="songMetadataId"/>.
    /// </summary>
    string Protect(int songMetadataId, byte[] contentKey);

    /// <summary>
    /// Recovers a content key, or throws <see cref="CryptographicException"/> if the stored value
    /// was tampered with, belongs to a different song, or was wrapped under a key we no longer hold.
    /// </summary>
    byte[] Unprotect(int songMetadataId, string protectedKey);
}

/// <inheritdoc />
public sealed class HlsContentKeyProtector : IHlsContentKeyProtector
{
    /// <summary>
    /// Prefix identifying the wrapping scheme and key generation.
    ///
    /// <para>
    /// Present from the first version precisely so a rotation never has to guess. To rotate: add the
    /// new key, start writing <c>v2</c>, keep unwrapping <c>v1</c> until a background pass has
    /// re-wrapped every row, then drop the old key. No audio is re-encoded at any point — the
    /// content keys themselves are unchanged, only their wrapping.
    /// </para>
    /// </summary>
    private const string CurrentVersionPrefix = "v1.";

    /// <summary>AES-GCM's standard nonce size. Never reuse one under the same key.</summary>
    private const int NonceSizeBytes = 12;

    /// <summary>Full-strength GCM tag. Truncating it weakens the authentication for 4 saved bytes.</summary>
    private const int TagSizeBytes = 16;

    /// <summary>AES-128 content keys, as HLS requires.</summary>
    public const int ContentKeySizeBytes = 16;

    private readonly byte[]? _wrappingKey;
    private readonly ILogger<HlsContentKeyProtector> _logger;

    public HlsContentKeyProtector(IOptions<HlsOptions> options, ILogger<HlsContentKeyProtector> logger)
    {
        _logger = logger;

        var configured = options?.Value?.ContentKeyWrappingKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Not fatal at construction: a site with no wrapping key configured should still start
            // and serve everything that is not encrypted playback. The endpoints that need it fail
            // individually and say so.
            _logger.LogWarning(
                "Hls:ContentKeyWrappingKey is not configured. Encrypted playback is unavailable.");
            _wrappingKey = null;
            return;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Hls:ContentKeyWrappingKey is not valid base64. Expected base64 of 32 random bytes.");
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Hls:ContentKeyWrappingKey decoded to {key.Length} bytes; 32 are required for AES-256.");
        }

        _wrappingKey = key;
    }

    /// <inheritdoc />
    public bool IsConfigured => _wrappingKey != null;

    /// <inheritdoc />
    public string Protect(int songMetadataId, byte[] contentKey)
    {
        ArgumentNullException.ThrowIfNull(contentKey);

        if (contentKey.Length != ContentKeySizeBytes)
        {
            throw new ArgumentException(
                $"An HLS content key must be {ContentKeySizeBytes} bytes, not {contentKey.Length}.",
                nameof(contentKey));
        }

        var wrappingKey = RequireWrappingKey();

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[contentKey.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(wrappingKey, TagSizeBytes))
        {
            aes.Encrypt(nonce, contentKey, ciphertext, tag, AssociatedData(songMetadataId));
        }

        // nonce ‖ ciphertext ‖ tag, one base64 blob. 12 + 16 + 16 = 44 bytes -> 60 base64 chars,
        // which with the prefix sits well inside the column's 256.
        var payload = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(payload, 0);
        ciphertext.CopyTo(payload, NonceSizeBytes);
        tag.CopyTo(payload, NonceSizeBytes + ciphertext.Length);

        return CurrentVersionPrefix + Convert.ToBase64String(payload);
    }

    /// <inheritdoc />
    public byte[] Unprotect(int songMetadataId, string protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            throw new CryptographicException("No wrapped content key was stored for this song.");
        }

        var wrappingKey = RequireWrappingKey();

        if (!protectedKey.StartsWith(CurrentVersionPrefix, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "The wrapped content key uses an unrecognised scheme version. "
                + "If the wrapping key was rotated, the rows still need re-wrapping.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedKey[CurrentVersionPrefix.Length..]);
        }
        catch (FormatException)
        {
            throw new CryptographicException("The wrapped content key is not valid base64.");
        }

        if (payload.Length != NonceSizeBytes + ContentKeySizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("The wrapped content key is the wrong length.");
        }

        var nonce = payload.AsSpan(0, NonceSizeBytes);
        var ciphertext = payload.AsSpan(NonceSizeBytes, ContentKeySizeBytes);
        var tag = payload.AsSpan(NonceSizeBytes + ContentKeySizeBytes, TagSizeBytes);

        var contentKey = new byte[ContentKeySizeBytes];
        using var aes = new AesGcm(wrappingKey, TagSizeBytes);

        // Throws CryptographicException on a bad tag, which is also what a key from another song
        // produces - the song id is the associated data.
        aes.Decrypt(nonce, ciphertext, tag, contentKey, AssociatedData(songMetadataId));

        return contentKey;
    }

    /// <summary>
    /// Generates a fresh content key. Lives here so every caller gets the right size from the right
    /// generator, rather than each one deciding.
    /// </summary>
    public static byte[] CreateContentKey() => RandomNumberGenerator.GetBytes(ContentKeySizeBytes);

    /// <summary>
    /// Binds a wrapped key to its song. Not secret — associated data is authenticated, not
    /// encrypted — it just makes a key moved between rows fail closed.
    /// </summary>
    private static byte[] AssociatedData(int songMetadataId)
        => Encoding.UTF8.GetBytes($"songmetadata:{songMetadataId}");

    private byte[] RequireWrappingKey()
        => _wrappingKey
            ?? throw new InvalidOperationException(
                "Hls:ContentKeyWrappingKey is not configured, so content keys cannot be wrapped or unwrapped.");
}
