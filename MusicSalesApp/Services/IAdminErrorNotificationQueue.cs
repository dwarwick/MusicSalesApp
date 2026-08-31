#nullable enable

using System.Threading.Channels;

namespace MusicSalesApp.Services;

/// <summary>
/// Hand-off between the Serilog sink (which runs on whatever thread logged) and the background
/// dispatcher that sends the mail.
/// </summary>
public interface IAdminErrorNotificationQueue
{
    /// <summary>
    /// Enqueues a notice without ever blocking the caller. Returns false when the queue is full,
    /// which is counted rather than awaited: a logging call must not be slowed by an SMTP server.
    /// </summary>
    bool TryEnqueue(AdminErrorNotice notice);

    ChannelReader<AdminErrorNotice> Reader { get; }

    /// <summary>
    /// Number of notices dropped because the queue was full, reset when read.
    /// </summary>
    int ExchangeDroppedCount();

    /// <summary>
    /// Puts a previously exchanged count back, for a report that was built but never delivered.
    /// Additive rather than assigning, so drops arriving in the meantime are not overwritten.
    /// </summary>
    void RestoreDroppedCount(int count);
}
