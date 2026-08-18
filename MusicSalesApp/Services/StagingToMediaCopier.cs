#nullable enable
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Moves a blob from the staging container into the media container.
///
/// <para>
/// Trivial-sounding, and it is not: the two containers are on <em>different storage accounts</em>.
/// Song media lives on a Premium account, which offers no Queue service at all, so the queues and the
/// staging container had to go on a Standard general-purpose one. Media never moves between them, so
/// every staging-to-media transfer is a cross-account copy that needs a source SAS rather than the
/// same-account server-side rename it looks like.
/// </para>
///
/// <para>
/// This exists as its own service for one reason: <c>MusicSalesApp.Functions/CLAUDE.md</c> names the
/// cross-account SAS copy as one of exactly two things unit tests cannot reach, verifiable only on a
/// deployed environment. A second, independently-maintained copy of logic nobody can test locally is
/// the textbook way to end up with one that quietly stopped working - so there is one copy, used by
/// both the audio pipeline and the lyrics pipeline.
/// </para>
/// </summary>
public interface IStagingToMediaCopier
{
    /// <summary>
    /// Mints one container-scoped read SAS over staging, to be reused for every copy in a single
    /// assembly.
    ///
    /// <para>
    /// Per assembly rather than per blob, because it is the same reader reading the same container
    /// within a few seconds; minting one per copy would multiply the token count for no extra
    /// safety.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The staging container cannot sign a SAS, which means it was not built from a key-based
    /// connection string.
    /// </exception>
    string CreateStagingReadSasQuery(BlobContainerClient staging);

    /// <summary>
    /// Copies one blob across accounts and waits for it to land.
    /// </summary>
    /// <param name="budget">
    /// The whole assembly's budget, shared by every copy in it, used only to describe the timeout in
    /// the exception. Each copy used to carry its own generous timeout, which bounded them
    /// individually but not together - several copies could each sit just inside their own limit and
    /// still outlast the caller waiting on all of them.
    /// </param>
    /// <param name="budgetToken">The token carrying that budget.</param>
    /// <param name="requestAborted">
    /// The original request token, used only to tell "the budget ran out" apart from "the caller hung
    /// up", so the log says which one happened.
    /// </param>
    Task CopyAsync(
        BlobContainerClient sourceContainer,
        string sourcePath,
        BlobContainerClient destinationContainer,
        string destinationPath,
        string sourceSasQuery,
        TimeSpan budget,
        CancellationToken budgetToken,
        CancellationToken requestAborted);
}

/// <inheritdoc />
public sealed class StagingToMediaCopier : IStagingToMediaCopier
{
    private static readonly TimeSpan CopyPollInterval = TimeSpan.FromSeconds(2);

    private readonly IOptions<MediaProcessingOptions> _options;

    public StagingToMediaCopier(IOptions<MediaProcessingOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public string CreateStagingReadSasQuery(BlobContainerClient staging)
    {
        // Needs a shared-key credential. The app authenticates both accounts with account-key
        // connection strings, so this holds; the guard makes the failure legible if that changes.
        if (!staging.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                $"Cannot generate a SAS for staging container '{staging.Name}'. "
                + "Cross-account assembly requires a key-based connection string.");
        }

        var lifetime = _options.Value.StagingSasLifetime;
        var sasUri = staging.GenerateSasUri(
            BlobContainerSasPermissions.Read,
            DateTimeOffset.UtcNow.Add(lifetime));

        return sasUri.Query.TrimStart('?');
    }

    /// <inheritdoc />
    public async Task CopyAsync(
        BlobContainerClient sourceContainer,
        string sourcePath,
        BlobContainerClient destinationContainer,
        string destinationPath,
        string sourceSasQuery,
        TimeSpan budget,
        CancellationToken budgetToken,
        CancellationToken requestAborted)
    {
        var sourceBlob = sourceContainer.GetBlobClient(sourcePath);
        var destinationBlob = destinationContainer.GetBlobClient(destinationPath);
        var sourceUri = new UriBuilder(sourceBlob.Uri) { Query = sourceSasQuery }.Uri;

        var operation = await destinationBlob.StartCopyFromUriAsync(
            sourceUri,
            new BlobCopyFromUriOptions(),
            budgetToken);

        // A same-account copy usually reports completion immediately; a cross-account one moves
        // real bytes, so expect to poll here.
        if (operation.HasCompleted)
        {
            return;
        }

        try
        {
            await operation.WaitForCompletionAsync(CopyPollInterval, budgetToken);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Copying '{sourcePath}' into '{destinationContainer.Name}/{destinationPath}' "
                + $"did not finish inside the {budget.TotalMinutes:0.##}-minute assembly budget.");
        }
    }
}
