#nullable enable

using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class AdminErrorNotificationQueue : IAdminErrorNotificationQueue
{
    private readonly Channel<AdminErrorNotice> _channel;
    private int _droppedCount;

    public AdminErrorNotificationQueue(IOptions<AdminErrorNotificationOptions> options)
    {
        var capacity = Math.Max(16, options.Value.QueueCapacity);

        // Wait + TryWrite, which is non-blocking despite the name: only WriteAsync waits, while
        // TryWrite fails fast when the channel is full. That matters because the caller is whatever
        // thread just logged an error and must not be held up behind mail delivery.
        //
        // Not DropWrite, which sounds like the right mode and is not: it discards the incoming item
        // and still returns true, so the overflow would be invisible instead of counted.
        _channel = Channel.CreateBounded<AdminErrorNotice>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public ChannelReader<AdminErrorNotice> Reader => _channel.Reader;

    public bool TryEnqueue(AdminErrorNotice notice)
    {
        if (_channel.Writer.TryWrite(notice))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public int ExchangeDroppedCount() => Interlocked.Exchange(ref _droppedCount, 0);

    public void RestoreDroppedCount(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _droppedCount, count);
        }
    }
}
