using System.Threading.Channels;

namespace AgentFlow.Windows.NativeHost;

/// <summary>
/// Serial background consumer for one native node. Native callbacks only enqueue immutable
/// managed events; this pump performs managed pin sends, logging, and TopicBus operations.
/// </summary>
internal sealed class NativeNodeBackgroundPump
{
    private readonly ChannelReader<NativeNodeEvent> _reader;
    private readonly Func<NativeNodeEvent, CancellationToken, Task> _dispatch;

    public NativeNodeBackgroundPump(
        ChannelReader<NativeNodeEvent> reader,
        Func<NativeNodeEvent, CancellationToken, Task> dispatch)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _dispatch(item, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The dispatcher records failures through the node logger. A malformed event
                    // must not terminate the pump for subsequent events.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
