using System;
using System.Threading;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The tokens are what stand between a listener and the content key, so the properties worth pinning
/// are the ones that would silently widen access if they regressed.
/// </summary>
[TestFixture]
public class HlsStreamTokenServiceTests
{
    private const int SongId = 77;

    private static HlsStreamTokenService Create(HlsOptions options = null)
        => new(new EphemeralDataProtectionProvider(), Options.Create(options ?? new HlsOptions()));

    [Test]
    public void AManifestTokenValidatesForItsOwnSong()
    {
        var service = Create();
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Manifest, 5, true));

        Assert.That(
            service.TryValidate(token, SongId, HlsTokenKind.Manifest, out var payload),
            Is.True);
        Assert.That(payload.UserId, Is.EqualTo(5));
        Assert.That(payload.HasFullAccess, Is.True);
    }

    [Test]
    public void ATokenIssuedForOneSongIsRejectedForAnother()
    {
        var service = Create();
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Key, 5, true));

        // Otherwise one paid-for song's key request would unlock the whole catalogue.
        Assert.That(service.TryValidate(token, SongId + 1, HlsTokenKind.Key, out _), Is.False);
    }

    /// <summary>
    /// The kind check is what allows the two lifetimes to differ by three orders of magnitude.
    ///
    /// <para>
    /// A manifest token lives 24 hours because the catalogue endpoint mints them in bulk long before
    /// anything is played. A key token lives a minute. Without this check the long lifetime would
    /// become the key's lifetime too, and the whole scheme would collapse into a 24-hour key URL.
    /// </para>
    /// </summary>
    [Test]
    public void AManifestTokenIsRejectedAtTheKeyEndpoint()
    {
        var service = Create();
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Manifest, 5, true));

        Assert.That(service.TryValidate(token, SongId, HlsTokenKind.Key, out _), Is.False);
    }

    [Test]
    public void AKeyTokenIsRejectedAtTheManifestEndpoint()
    {
        var service = Create();
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Key, 5, true));

        Assert.That(service.TryValidate(token, SongId, HlsTokenKind.Manifest, out _), Is.False);
    }

    [Test]
    public void AnExpiredTokenIsRejected()
    {
        // One tick of life, so it is expired by the time it is validated. This is the property the
        // whole design leans on: a key URL copied out of dev tools is useless by the time it is
        // pasted anywhere.
        var service = Create(new HlsOptions { KeyTokenLifetime = TimeSpan.FromMilliseconds(1) });
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Key, 5, true));

        Thread.Sleep(50);

        Assert.That(service.TryValidate(token, SongId, HlsTokenKind.Key, out _), Is.False);
    }

    [Test]
    public void ATamperedTokenIsRejected()
    {
        var service = Create();
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Key, 5, true));
        var tampered = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];

        Assert.That(service.TryValidate(tampered, SongId, HlsTokenKind.Key, out _), Is.False);
    }

    [Test]
    public void AMissingTokenIsRejectedRatherThanThrowing()
    {
        var service = Create();

        // The key endpoint reads this straight off the query string, so absent and empty are both
        // ordinary inputs rather than exceptional ones.
        Assert.That(service.TryValidate(null, SongId, HlsTokenKind.Key, out _), Is.False);
        Assert.That(service.TryValidate("", SongId, HlsTokenKind.Key, out _), Is.False);
        Assert.That(service.TryValidate("not-a-token", SongId, HlsTokenKind.Key, out _), Is.False);
    }

    [Test]
    public void AnAnonymousListenersTokenIsValid()
    {
        var service = Create();
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Manifest, null, false));

        // Anonymous is a real case, not a failure: they are entitled to the free preview.
        Assert.That(service.TryValidate(token, SongId, HlsTokenKind.Manifest, out var payload), Is.True);
        Assert.That(payload.UserId, Is.Null);
        Assert.That(payload.HasFullAccess, Is.False);
    }

    [Test]
    public void ANonPositiveConfiguredLifetimeFallsBackRatherThanMintingDeadTokens()
    {
        // A misconfiguration here would otherwise disable playback silently, by issuing tokens that
        // are already expired - the failure would look like a player bug, not a settings mistake.
        var service = Create(new HlsOptions { KeyTokenLifetime = TimeSpan.Zero });
        var token = service.Issue(new HlsStreamTokenPayload(SongId, HlsTokenKind.Key, 5, true));

        Assert.That(service.TryValidate(token, SongId, HlsTokenKind.Key, out _), Is.True);
    }

    /// <summary>
    /// The segment SAS must expire before the manifest token, or expiry becomes unrecoverable.
    ///
    /// <para>
    /// When a SAS expires mid-session the player recovers by refetching the manifest, because the
    /// server stamps fresh segment credentials every time it builds one. That only works while the
    /// manifest's own token is still valid. Set the two the other way round and both die together,
    /// leaving nothing to recover with — and neither value looks wrong on its own, which is why the
    /// relationship is pinned here rather than left to whoever edits them next.
    /// </para>
    ///
    /// <para>
    /// Pinned for the same reason <c>PoisonHandlerBeatsTheReconcilerTests</c> pins its ordering: the
    /// two numbers are independently sensible and only wrong together.
    /// </para>
    /// </summary>
    [Test]
    public void TheSegmentSasExpiresWellBeforeTheManifestToken()
    {
        var options = new HlsOptions();

        Assert.That(
            options.SegmentSasLifetime,
            Is.LessThan(options.ManifestTokenLifetime),
            "a segment SAS that outlives the manifest token cannot be refreshed, so an expiry "
            + "would end playback permanently");
    }

    /// <summary>
    /// The key token is a one-shot credential; the segment SAS is used continuously.
    ///
    /// <para>
    /// hls.js fetches the key once, before the first segment, so seconds are ample. Segments are
    /// pulled against their URLs for the whole track, so the SAS has to outlast a song. Making the
    /// key lifetime the longer of the two would widen the window on the one credential that
    /// actually decrypts audio, in exchange for nothing.
    /// </para>
    /// </summary>
    [Test]
    public void TheKeyTokenIsFarShorterLivedThanTheSegmentSas()
    {
        var options = new HlsOptions();

        Assert.That(options.KeyTokenLifetime, Is.LessThan(options.SegmentSasLifetime));
    }

    /// <summary>
    /// A segment SAS has to outlive a whole track, because the manifest is VOD and fetched once.
    ///
    /// <para>
    /// Ten minutes is a deliberately generous upper bound on a song. The failure this guards against
    /// is subtle: playback starts normally, plays through the buffered head of the track, then stops
    /// part-way with a 403 — which reads as a broken player rather than an expired credential.
    /// </para>
    /// </summary>
    [Test]
    public void TheSegmentSasComfortablyOutlastsAnyPlausibleTrack()
    {
        var options = new HlsOptions();

        Assert.That(options.SegmentSasLifetime, Is.GreaterThan(TimeSpan.FromMinutes(10)));
    }
}
