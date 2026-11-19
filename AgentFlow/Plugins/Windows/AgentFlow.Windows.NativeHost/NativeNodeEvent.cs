using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Windows.NativeHost;

internal enum NativeNodeEventKind
{
    Sample,
    Log
}

internal sealed record NativeNodeEvent(
    NativeNodeEventKind Kind,
    string PinName,
    Sample? Sample,
    LogLevel LogLevel,
    string Message)
{
    public static NativeNodeEvent ForSample(string pinName, Sample sample) =>
        new(NativeNodeEventKind.Sample, pinName, sample, LogLevel.Trace, string.Empty);

    public static NativeNodeEvent ForLog(LogLevel level, string message) =>
        new(NativeNodeEventKind.Log, string.Empty, null, level, message);
}
