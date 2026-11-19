using System.Runtime.InteropServices;

namespace AgentFlow.Windows.NativeHost;

internal static class NativeAbi
{
    public const uint Version = 1;
    public const int InvalidArgument = -1;
    public const int NotInitialized = -2;
    public const int AbiMismatch = -3;
    public const int QueueFull = -4;
    public const int NativeError = -5;
    public const int InternalError = -6;
    public const int NotSupported = -7;

    public static readonly int SampleStructSize = Marshal.SizeOf<AfSample>();
    public static readonly int BroadcastStructSize = Marshal.SizeOf<AfBroadcast>();
    public static readonly int HostVTableStructSize = Marshal.SizeOf<AfNodeHostVTable>();
}

internal static class AfSampleType
{
    public const int Unknown = 0;
    public const int String = 1;
    public const int Int32 = 2;
    public const int Int64 = 3;
    public const int Bool = 4;
    public const int Float = 5;
    public const int Double = 6;
    public const int Binary = 7;
}

internal static class AfLogLevel
{
    public const int Trace = 0;
    public const int Debug = 1;
    public const int Info = 2;
    public const int Warning = 3;
    public const int Error = 4;
    public const int Critical = 5;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AfSample
{
    public uint StructSize;
    public uint AbiVersion;
    public int Type;
    public int BoolValue;
    public int Int32Value;
    public long Int64Value;
    public float FloatValue;
    public double DoubleValue;
    public IntPtr Data;
    public ulong DataSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AfBroadcast
{
    public uint StructSize;
    public uint AbiVersion;
    public IntPtr Topic;
    public ulong TopicLength;
    public IntPtr PayloadKind;
    public ulong PayloadKindLength;
    public IntPtr PayloadJson;
    public ulong PayloadJsonLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AfNodeHostVTable
{
    public uint StructSize;
    public uint AbiVersion;
    public IntPtr CallbackContext;
    public IntPtr SendSample;
    public IntPtr Log;
    public IntPtr PublishBroadcast;
    public IntPtr SubscribeBroadcast;
    public IntPtr UnsubscribeBroadcast;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetAbiVersionDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CreateNodeDelegate(IntPtr hostVTable, IntPtr callbackContext, out IntPtr nodeContext);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NodeLifecycleDelegate(IntPtr nodeContext);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ReceiveSampleDelegate(IntPtr nodeContext, IntPtr sample);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void DestroyNodeDelegate(IntPtr nodeContext);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetLastErrorDelegate(IntPtr nodeContext);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SendSampleCallback(
    IntPtr callbackContext,
    IntPtr pinName,
    ulong pinNameLength,
    IntPtr sample);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void LogCallback(
    IntPtr callbackContext,
    int level,
    IntPtr message,
    ulong messageLength);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PublishBroadcastCallback(
    IntPtr callbackContext,
    IntPtr message);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ReceiveBroadcastCallback(
    IntPtr receiverContext,
    IntPtr message);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SubscribeBroadcastCallback(
    IntPtr callbackContext,
    IntPtr topic,
    ulong topicLength,
    IntPtr receive,
    IntPtr receiverContext);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int UnsubscribeBroadcastCallback(
    IntPtr callbackContext,
    IntPtr topic,
    ulong topicLength);
