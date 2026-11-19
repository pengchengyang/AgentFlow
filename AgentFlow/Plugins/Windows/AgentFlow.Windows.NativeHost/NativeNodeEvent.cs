using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Windows.NativeHost;

internal enum NativeNodeEventKind
{
    Sample,
    Log,
    BroadcastPublish,
    BroadcastSubscribe,
    BroadcastUnsubscribe,
    BroadcastReceive
}

internal sealed record NativeNodeEvent(
    NativeNodeEventKind Kind,
    string PinName,
    Sample? Sample,
    LogLevel LogLevel,
    string Message,
    string Topic,
    string PayloadKind,
    string PayloadJson,
    string BroadcastId,
    IntPtr Receiver,
    IntPtr ReceiverContext)
{
    public static NativeNodeEvent ForSample(string pinName, Sample sample) =>
        new(NativeNodeEventKind.Sample, pinName, sample, LogLevel.Trace, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, IntPtr.Zero, IntPtr.Zero);

    public static NativeNodeEvent ForLog(LogLevel level, string message) =>
        new(NativeNodeEventKind.Log, string.Empty, null, level, message,
            string.Empty, string.Empty, string.Empty, string.Empty, IntPtr.Zero, IntPtr.Zero);

    public static NativeNodeEvent ForBroadcastPublish(string topic, string payloadKind, string payloadJson) =>
        new(NativeNodeEventKind.BroadcastPublish, string.Empty, null, LogLevel.Trace, string.Empty,
            topic, payloadKind, payloadJson, string.Empty, IntPtr.Zero, IntPtr.Zero);

    public static NativeNodeEvent ForBroadcastSubscribe(string topic, IntPtr receiver, IntPtr receiverContext) =>
        new(NativeNodeEventKind.BroadcastSubscribe, string.Empty, null, LogLevel.Trace, string.Empty,
            topic, string.Empty, string.Empty, string.Empty, receiver, receiverContext);

    public static NativeNodeEvent ForBroadcastUnsubscribe(string topic) =>
        new(NativeNodeEventKind.BroadcastUnsubscribe, string.Empty, null, LogLevel.Trace, string.Empty,
            topic, string.Empty, string.Empty, string.Empty, IntPtr.Zero, IntPtr.Zero);

    public static NativeNodeEvent ForBroadcastReceive(
        string topic,
        string payloadKind,
        string payloadJson,
        string broadcastId,
        IntPtr receiver,
        IntPtr receiverContext) =>
        new(NativeNodeEventKind.BroadcastReceive, string.Empty, null, LogLevel.Trace, string.Empty,
            topic, payloadKind, payloadJson, broadcastId, receiver, receiverContext);
}
