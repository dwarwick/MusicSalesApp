#nullable enable

namespace MusicSalesApp.Helpers;

/// <summary>
/// Answers one question: did this upload fail because the browser can no longer read the file the
/// visitor picked, rather than because anything on the server is wrong?
///
/// <para>
/// Blazor Server streams a chosen file over the circuit on demand, so the browser must still be
/// able to open it when the server gets round to reading - which can be minutes later in a large
/// batch. If the file was moved, renamed, or had its permissions changed in the meantime, or an
/// iOS photo-library reference expired, the browser answers <c>NotReadableError</c> and the
/// framework surfaces it as an <see cref="InvalidOperationException"/> from
/// <c>RemoteJSDataStream</c>.
/// </para>
///
/// <para>
/// Two things follow, and both are the point of this type. It is not a server fault, so it must not
/// reach <see cref="Services.AdminErrorNotificationSink"/> and email the admin - it did on
/// 2026-09-07. And it cannot be retried against the same <c>IBrowserFile</c>, because the handle
/// behind it is dead: the only way forward is for the visitor to pick the file again.
/// </para>
/// </summary>
public static class BrowserFileTransfer
{
    /// <summary>
    /// The browser's own error name, which the framework passes through verbatim in the message.
    /// There is no exception type or error code to match on - the string is all there is.
    /// </summary>
    private const string NotReadableErrorName = "NotReadableError";

    /// <summary>
    /// What to tell the visitor. Says re-select rather than "try again", because retrying the same
    /// selection fails identically.
    /// </summary>
    public const string UserMessage =
        "The browser could not read that file. It may have been moved, renamed, or closed since you "
        + "chose it. Please select it again.";

    /// <summary>
    /// True when <paramref name="exception"/> means the browser can no longer read a file that was
    /// already chosen.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, for the same reason as
    /// <see cref="CircuitTeardown.IsExpected(System.Exception?)"/>: <see cref="InvalidOperationException"/>
    /// is far too common a type to downgrade wholesale, so the browser's error name has to be
    /// present as well. An <see cref="AggregateException"/> qualifies only if <em>every</em> inner
    /// exception does - one real fault travelling alongside is still a real fault.
    /// </remarks>
    public static bool IsFileNoLongerReadable(Exception? exception) => exception switch
    {
        null => false,
        AggregateException aggregate =>
            aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsFileNoLongerReadable),
        InvalidOperationException invalid =>
            invalid.Message.Contains(NotReadableErrorName, StringComparison.Ordinal),
        _ => false
    };
}
