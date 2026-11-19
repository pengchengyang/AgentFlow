using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Windows.NativeHost;

/// <summary>Pin metadata generated from a native node manifest.</summary>
public sealed record NativePinDefinition(
    string Name,
    DataType DataType,
    PinDirection Direction,
    bool Required = false);

/// <summary>
/// Base class for generated proxies that host a Windows VC++ node DLL through the stable
/// AfNode ABI. Native callbacks only enqueue managed events; one serial background pump per
/// node performs BasePin.SendSample and logging on the managed side.
/// </summary>
public abstract class WindowsNativeNodeBase : BaseNode
{
    private const int EventQueueCapacity = 1024;
    private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, BasePin> _inputPinsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BasePin> _outputPinsByName = new(StringComparer.Ordinal);

    private INodeContext? _context;
    private ILogger? _logger;
    private NativeNodeModule? _nativeModule;
    private Channel<NativeNodeEvent>? _eventChannel;
    private NativeNodeBackgroundPump? _eventPump;
    private Task? _eventPumpTask;
    private CancellationTokenSource? _eventPumpCts;
    private IntPtr _hostVTablePointer;
    private GCHandle _callbackState;
    private SendSampleCallback? _sendSampleCallback;
    private LogCallback? _logCallback;
    private int _acceptEvents;

    protected abstract string ModuleId { get; }
    protected abstract string NativeBinaryFileName { get; }
    protected abstract int ExpectedAbiVersion { get; }
    protected abstract IReadOnlyList<NativePinDefinition> GeneratedPins { get; }

    public override void AddPins()
    {
        _inputPinsByName.Clear();
        _outputPinsByName.Clear();

        foreach (var definition in GeneratedPins)
        {
            var pin = new BasePin(
                definition.Name,
                definition.DataType,
                definition.Direction,
                definition.Required);

            if (definition.Direction == PinDirection.Input)
            {
                AddInputPin(pin);
                _inputPinsByName[definition.Name] = pin;
            }
            else
            {
                AddOutputPin(pin);
                _outputPinsByName[definition.Name] = pin;
            }
        }
    }

    public override async Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = context.Logger;
        Running = false;

        await ResetNativeRuntimeAsync(LogWarningBestEffort).ConfigureAwait(false);
        StartEventPump();

        try
        {
            ValidateManifest();
            LoadNativeModule();
        }
        catch
        {
            await ResetNativeRuntimeAsync(LogWarningBestEffort).ConfigureAwait(false);
            throw;
        }

        _logger?.LogInformation(
            "{Node} initialized native module {ModuleId} from {NativeBinary}.",
            DisplayName,
            ModuleId,
            NativeBinaryFileName);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        if (_nativeModule is null || !_nativeModule.IsCreated)
            throw new InvalidOperationException($"{DisplayName} cannot run before native initialization.");

        _nativeModule.Run();
        Running = true;
        _logger?.LogInformation("{Node} native node is running.", DisplayName);
        return Task.CompletedTask;
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;

        try
        {
            _nativeModule?.Stop();
        }
        finally
        {
            await StopEventPumpAsync().ConfigureAwait(false);
            Running = false;
        }
    }

    public override async Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        await StopEventPumpAsync().ConfigureAwait(false);
        ShutdownNativeModuleBestEffort();
        Running = false;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, null))
            return;

        if (!_inputPinsByName.TryGetValue(pin.Name, out var knownPin) || !ReferenceEquals(knownPin, pin))
            return;

        if (value is null)
        {
            _logger?.LogWarning("{Node} ignored a null sample on {Pin}.", DisplayName, pin.Name);
            return;
        }

        if (_nativeModule is null || !_nativeModule.IsCreated)
        {
            _logger?.LogWarning("{Node} ignored input on {Pin}: the native node is not initialized.", DisplayName, pin.Name);
            return;
        }

        try
        {
            using var nativeSample = NativeSampleLease.FromManagedSample(value, pin.Name);
            var status = _nativeModule.ReceiveSample(nativeSample.Pointer);
            if (status != 0)
            {
                _logger?.LogError(
                    "{Node} native receive failed with status {Status} on {Pin}: {NativeError}",
                    DisplayName,
                    status,
                    pin.Name,
                    _nativeModule.GetLastError());
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{Node} failed to marshal input on {Pin}.", DisplayName, pin.Name);
        }
    }

    private void StartEventPump()
    {
        _eventChannel = Channel.CreateBounded<NativeNodeEvent>(new BoundedChannelOptions(EventQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _acceptEvents = 1;
        _eventPumpCts = new CancellationTokenSource();
        _eventPump = new NativeNodeBackgroundPump(_eventChannel.Reader, DispatchNativeEvent);
        _eventPumpTask = Task.Run(() => _eventPump.RunAsync(_eventPumpCts.Token));
    }

    private async Task StopEventPumpAsync()
    {
        Volatile.Write(ref _acceptEvents, 0);
        _eventChannel?.Writer.TryComplete();

        var pumpTask = _eventPumpTask;
        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.WaitAsync(PumpDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning(
                    "{Node} event pump did not drain within {Timeout}; cancelling the remaining queue.",
                    DisplayName,
                    PumpDrainTimeout);
                _eventPumpCts?.Cancel();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "{Node} event pump stopped with an error.", DisplayName);
            }
        }

        _eventPumpCts?.Cancel();
        _eventPumpCts?.Dispose();
        _eventPumpCts = null;
        _eventPump = null;
        _eventPumpTask = null;
        _eventChannel = null;
    }

    private async Task ResetNativeRuntimeAsync(Action<Exception, string> onError)
    {
        await StopEventPumpAsync().ConfigureAwait(false);
        try
        {
            ShutdownNativeModule();
        }
        catch (Exception ex)
        {
            onError(ex, "Failed to shut down the native module while resetting the node.");
        }
    }

    private void LoadNativeModule()
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(WindowsNativeNodeBase).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            throw new InvalidOperationException("Unable to resolve the NativeHost plugin directory.");

        var moduleDirectory = Path.Combine(pluginDirectory, "Windows", "win-x64", ModuleId);
        var libraryPath = Path.Combine(moduleDirectory, NativeBinaryFileName);
        if (!File.Exists(libraryPath))
            throw new FileNotFoundException($"Native node binary was not found: {libraryPath}", libraryPath);

        _sendSampleCallback = OnNativeSendSample;
        _logCallback = OnNativeLog;
        _callbackState = GCHandle.Alloc(this, GCHandleType.Normal);

        var hostVTable = new AfNodeHostVTable
        {
            StructSize = (uint)NativeAbi.HostVTableStructSize,
            AbiVersion = NativeAbi.Version,
            CallbackContext = GCHandle.ToIntPtr(_callbackState),
            SendSample = Marshal.GetFunctionPointerForDelegate(_sendSampleCallback),
            Log = Marshal.GetFunctionPointerForDelegate(_logCallback)
        };

        _hostVTablePointer = Marshal.AllocHGlobal(NativeAbi.HostVTableStructSize);
        Marshal.StructureToPtr(hostVTable, _hostVTablePointer, fDeleteOld: false);

        _nativeModule = new NativeNodeModule();
        try
        {
            _nativeModule.Load(libraryPath, ExpectedAbiVersion);
            _nativeModule.Create(_hostVTablePointer, GCHandle.ToIntPtr(_callbackState));
            _nativeModule.Initialize();
        }
        catch
        {
            _nativeModule.Dispose();
            _nativeModule = null;
            FreeNativeHostObjects();
            throw;
        }
    }

    private void ValidateManifest()
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(WindowsNativeNodeBase).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            throw new InvalidOperationException("Unable to resolve the NativeHost plugin directory.");

        var manifestPath = Path.Combine(pluginDirectory, "Windows", "win-x64", ModuleId, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Native node manifest was not found: {manifestPath}", manifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("moduleId").GetString(), ModuleId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Native manifest moduleId does not match proxy module '{ModuleId}'.");
        if (!string.Equals(root.GetProperty("nodeType").GetString(), Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Native manifest nodeType does not match proxy node '{Name}'.");
        if (!string.Equals(root.GetProperty("nativeBinary").GetString(), NativeBinaryFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Native manifest nativeBinary does not match proxy binary '{NativeBinaryFileName}'.");
        if (root.GetProperty("abiVersion").GetInt32() != ExpectedAbiVersion)
            throw new InvalidOperationException($"Native manifest abiVersion does not match proxy ABI version {ExpectedAbiVersion}.");
    }

    private void DispatchNativeEvent(NativeNodeEvent item)
    {
        if (item.Kind == NativeNodeEventKind.Log)
        {
            _logger?.Log(item.LogLevel, "{NativeMessage}", item.Message);
            return;
        }

        if (!_outputPinsByName.TryGetValue(item.PinName, out var pin))
        {
            _logger?.LogError(
                "{Node} ignored a native sample for unknown output pin {Pin}.",
                DisplayName,
                item.PinName);
            return;
        }

        if (item.Sample is null)
        {
            _logger?.LogWarning("{Node} ignored a null native sample on {Pin}.", DisplayName, item.PinName);
            return;
        }

        if (item.Sample.Type != pin.DataType)
        {
            _logger?.LogError(
                "{Node} rejected a native {SampleType} sample on {Pin}; pin type is {PinType}.",
                DisplayName,
                item.Sample.Type,
                item.PinName,
                pin.DataType);
            return;
        }

        pin.SendSampleWithDispatchLog(item.Sample, "native callback", LogLevel.Trace);
    }

    private static int OnNativeSendSample(
        IntPtr callbackContext,
        IntPtr pinName,
        ulong pinNameLength,
        IntPtr sample)
    {
        WindowsNativeNodeBase? node = null;
        try
        {
            if (callbackContext == IntPtr.Zero)
                return NativeAbi.InvalidArgument;
            node = GCHandle.FromIntPtr(callbackContext).Target as WindowsNativeNodeBase;
            if (node is null || pinName == IntPtr.Zero || sample == IntPtr.Zero)
                return NativeAbi.InvalidArgument;

            var name = ReadUtf8(pinName, pinNameLength);
            var afSample = Marshal.PtrToStructure<AfSample>(sample);
            var managedSample = ConvertToManagedSample(afSample, name);
            if (managedSample is null)
            {
                node._logger?.LogWarning(
                    "{Node} rejected a native sample with an unsupported layout on {Pin}.",
                    node.DisplayName,
                    name);
                return NativeAbi.InvalidArgument;
            }

            if (Volatile.Read(ref node._acceptEvents) == 0 || node._eventChannel is null)
                return NativeAbi.NotInitialized;

            if (!node._eventChannel.Writer.TryWrite(NativeNodeEvent.ForSample(name, managedSample)))
            {
                node._logger?.LogWarning(
                    "{Node} native event queue is full; sample on {Pin} was rejected.",
                    node.DisplayName,
                    name);
                return NativeAbi.QueueFull;
            }

            return 0;
        }
        catch (Exception ex)
        {
            node?._logger?.LogError(ex, "{Node} failed to marshal a native callback sample.", node.DisplayName);
            return NativeAbi.InternalError;
        }
    }

    private static void OnNativeLog(
        IntPtr callbackContext,
        int level,
        IntPtr message,
        ulong messageLength)
    {
        WindowsNativeNodeBase? node = null;
        try
        {
            if (callbackContext == IntPtr.Zero)
                return;
            node = GCHandle.FromIntPtr(callbackContext).Target as WindowsNativeNodeBase;
            if (node is null || Volatile.Read(ref node._acceptEvents) == 0 || node._eventChannel is null)
                return;

            var text = message == IntPtr.Zero ? string.Empty : ReadUtf8(message, messageLength);
            var managedLevel = level switch
            {
                AfLogLevel.Trace => LogLevel.Trace,
                AfLogLevel.Debug => LogLevel.Debug,
                AfLogLevel.Info => LogLevel.Information,
                AfLogLevel.Warning => LogLevel.Warning,
                AfLogLevel.Error => LogLevel.Error,
                AfLogLevel.Critical => LogLevel.Critical,
                _ => LogLevel.Information
            };

            node._eventChannel.Writer.TryWrite(NativeNodeEvent.ForLog(managedLevel, text));
        }
        catch (Exception ex)
        {
            node?._logger?.LogError(ex, "{Node} failed to marshal a native log event.", node.DisplayName);
        }
    }

    private static string ReadUtf8(IntPtr pointer, ulong length)
    {
        if (pointer == IntPtr.Zero || length == 0)
            return string.Empty;
        if (length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), "Native UTF-8 payload is too large.");

        var bytes = new byte[(int)length];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static Sample? ConvertToManagedSample(AfSample sample, string pinName)
    {
        if (sample.StructSize < NativeAbi.SampleStructSize || sample.AbiVersion != NativeAbi.Version)
            return null;

        Sample managed = sample.Type switch
        {
            AfSampleType.String => StringSample.CreateSample(),
            AfSampleType.Int32 => Int32Sample.CreateSample(),
            AfSampleType.Int64 => Int64Sample.CreateSample(),
            AfSampleType.Bool => BoolSample.CreateSample(),
            AfSampleType.Float => FloatSample.CreateSample(),
            AfSampleType.Double => DoubleSample.CreateSample(),
            AfSampleType.Binary => BinSample.CreateSample(),
            _ => null!
        };

        if (managed is null)
            return null;

        switch (sample.Type)
        {
            case AfSampleType.String:
                managed.SetValue(ReadUtf8(sample.Data, sample.DataSize), pinName);
                break;
            case AfSampleType.Int32:
                managed.SetValue(sample.Int32Value, pinName);
                break;
            case AfSampleType.Int64:
                managed.SetValue(sample.Int64Value, pinName);
                break;
            case AfSampleType.Bool:
                managed.SetValue(sample.BoolValue != 0, pinName);
                break;
            case AfSampleType.Float:
                managed.SetValue(sample.FloatValue, pinName);
                break;
            case AfSampleType.Double:
                managed.SetValue(sample.DoubleValue, pinName);
                break;
            case AfSampleType.Binary:
                managed.SetValue(ReadBytes(sample.Data, sample.DataSize), pinName);
                break;
        }

        return managed;
    }

    private static byte[] ReadBytes(IntPtr pointer, ulong length)
    {
        if (length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), "Native binary payload is too large.");
        if (length == 0)
            return Array.Empty<byte>();
        if (pointer == IntPtr.Zero)
            throw new ArgumentException("Native binary payload pointer is null.", nameof(pointer));

        var bytes = new byte[(int)length];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return bytes;
    }

    private void ShutdownNativeModuleBestEffort()
    {
        try { ShutdownNativeModule(); }
        catch (Exception ex) { LogWarningBestEffort(ex, "Failed to shut down the native module."); }
    }

    private void ShutdownNativeModule()
    {
        try
        {
            _nativeModule?.Shutdown();
        }
        finally
        {
            _nativeModule?.Dispose();
            _nativeModule = null;
            FreeNativeHostObjects();
        }
    }

    private void FreeNativeHostObjects()
    {
        if (_hostVTablePointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_hostVTablePointer);
            _hostVTablePointer = IntPtr.Zero;
        }

        if (_callbackState.IsAllocated)
            _callbackState.Free();

        _sendSampleCallback = null;
        _logCallback = null;
    }

    private void LogWarningBestEffort(Exception exception, string message)
    {
        _logger?.LogWarning(exception, "{Node}: {Message}", DisplayName, message);
    }

    private sealed class NativeSampleLease : IDisposable
    {
        private GCHandle _pinnedPayload;
        private IntPtr _samplePointer;

        private NativeSampleLease(IntPtr samplePointer, GCHandle pinnedPayload)
        {
            _samplePointer = samplePointer;
            _pinnedPayload = pinnedPayload;
        }

        public IntPtr Pointer => _samplePointer;

        public static NativeSampleLease FromManagedSample(Sample sample, string pinName)
        {
            var afSample = new AfSample
            {
                StructSize = (uint)NativeAbi.SampleStructSize,
                AbiVersion = NativeAbi.Version
            };
            GCHandle pinned = default;
            byte[]? payload = null;

            switch (sample.Type)
            {
                case DataType.STRING:
                    afSample.Type = AfSampleType.String;
                    payload = Encoding.UTF8.GetBytes((sample as StringSample)?.StringValue ?? string.Empty);
                    break;
                case DataType.INT32:
                    afSample.Type = AfSampleType.Int32;
                    afSample.Int32Value = sample.Int32Value ?? 0;
                    break;
                case DataType.INT64:
                    afSample.Type = AfSampleType.Int64;
                    afSample.Int64Value = sample.Int64Value ?? 0;
                    break;
                case DataType.BOOL:
                    afSample.Type = AfSampleType.Bool;
                    afSample.BoolValue = sample.BoolValue == true ? 1 : 0;
                    break;
                case DataType.FLOAT:
                    afSample.Type = AfSampleType.Float;
                    afSample.FloatValue = sample.FloatValue ?? 0f;
                    break;
                case DataType.DOUBLE:
                    afSample.Type = AfSampleType.Double;
                    afSample.DoubleValue = sample.DoubleValue ?? 0d;
                    break;
                case DataType.BIN:
                    afSample.Type = AfSampleType.Binary;
                    payload = sample.BinValue ?? Array.Empty<byte>();
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported native sample type {sample.Type}.");
            }

            if (payload is { Length: > 0 })
            {
                pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
                afSample.Data = pinned.AddrOfPinnedObject();
                afSample.DataSize = (ulong)payload.Length;
            }

            var samplePointer = Marshal.AllocHGlobal(NativeAbi.SampleStructSize);
            Marshal.StructureToPtr(afSample, samplePointer, fDeleteOld: false);
            return new NativeSampleLease(samplePointer, pinned);
        }

        public void Dispose()
        {
            if (_samplePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_samplePointer);
                _samplePointer = IntPtr.Zero;
            }

            if (_pinnedPayload.IsAllocated)
                _pinnedPayload.Free();
        }
    }
}
