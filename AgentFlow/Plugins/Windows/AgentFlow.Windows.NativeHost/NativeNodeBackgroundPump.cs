using System.Threading.Channels;

namespace AgentFlow.Windows.NativeHost;

/// <summary>
/// Serial background consumer for one native node. Native callbacks only enqueue immutable
/// managed events; this pump is the only place that calls BasePin.SendSample or the node logger.
/// </summary>
internal sealed class NativeNodeBackgroundPump
{
    private readonly ChannelReader<NativeNodeEvent> _reader;
    private readonly Action<NativeNodeEvent> _dispatch;

    public NativeNodeBackgroundPump(
        ChannelReader<NativeNodeEvent> reader,
        Action<NativeNodeEvent> dispatch)
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
                    _dispatch(item);
                }
                catch
                {
                    // The event dispatcher records its own failures through the node logger.
                    // Keeping the pump alive is more important than a single malformed event.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
