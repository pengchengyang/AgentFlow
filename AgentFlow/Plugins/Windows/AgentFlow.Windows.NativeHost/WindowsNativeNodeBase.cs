using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
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
    private readonly Dictionary<string, NativeBroadcastRegistration> _broadcastSubscriptions =
        new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, BroadcastSource> _broadcastSources = new();

    private INodeContext? _context;
    private ILogger? _logger;
    private ITopicBus? _topicBus;
    private NativeNodeModule? _nativeModule;
    private Channel<NativeNodeEvent>? _eventChannel;
    private NativeNodeBackgroundPump? _eventPump;
    private Task? _eventPumpTask;
    private CancellationTokenSource? _eventPumpCts;
    private IntPtr _hostVTablePointer;
    private GCHandle _callbackState;
    private SendSampleCallback? _sendSampleCallback;
    private LogCallback? _logCallback;
    private PublishBroadcastCallback? _publishBroadcastCallback;
    private SubscribeBroadcastCallback? _subscribeBroadcastCallback;
    private UnsubscribeBroadcastCallback? _unsubscribeBroadcastCallback;
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
        _topicBus = context.GetService<ITopicBus>();
        if (_topicBus is null)
            _logger.LogWarning("{Node} did not resolve an ITopicBus; native broadcasts will be unavailable.", DisplayName);
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
        _topicBus = context.GetService<ITopicBus>() ?? _topicBus;
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
            DisposeBroadcastSubscriptions();
            await StopEventPumpAsync().ConfigureAwait(false);
            Running = false;
        }
    }

    public override async Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        DisposeBroadcastSubscriptions();
        await StopEventPumpAsync().ConfigureAwait(false);
        ShutdownNativeModuleBestEffort();
        _topicBus = null;
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
        _eventPump = new NativeNodeBackgroundPump(_eventChannel.Reader, DispatchNativeEventAsync);
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
        DisposeBroadcastSubscriptions();
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
        _publishBroadcastCallback = OnNativePublishBroadcast;
        _subscribeBroadcastCallback = OnNativeSubscribeBroadcast;
        _unsubscribeBroadcastCallback = OnNativeUnsubscribeBroadcast;
        _callbackState = GCHandle.Alloc(this, GCHandleType.Normal);

        var hostVTable = new AfNodeHostVTable
        {
            StructSize = (uint)NativeAbi.HostVTableStructSize,
            AbiVersion = NativeAbi.Version,
            CallbackContext = GCHandle.ToIntPtr(_callbackState),
            SendSample = Marshal.GetFunctionPointerForDelegate(_sendSampleCallback),
            Log = Marshal.GetFunctionPointerForDelegate(_logCallback),
            PublishBroadcast = Marshal.GetFunctionPointerForDelegate(_publishBroadcastCallback),
            SubscribeBroadcast = Marshal.GetFunctionPointerForDelegate(_subscribeBroadcastCallback),
            UnsubscribeBroadcast = Marshal.GetFunctionPointerForDelegate(_unsubscribeBroadcastCallback)
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

    private async Task DispatchNativeEventAsync(NativeNodeEvent item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case NativeNodeEventKind.Log:
                _logger?.Log(item.LogLevel, "{NativeMessage}", item.Message);
                return;
            case NativeNodeEventKind.Sample:
                DispatchNativeSample(item);
                return;
            case NativeNodeEventKind.BroadcastPublish:
                await DispatchBroadcastPublishAsync(item, cancellationToken).ConfigureAwait(false);
                return;
            case NativeNodeEventKind.BroadcastSubscribe:
                DispatchBroadcastSubscribe(item);
                return;
            case NativeNodeEventKind.BroadcastUnsubscribe:
                DispatchBroadcastUnsubscribe(item);
                return;
            case NativeNodeEventKind.BroadcastReceive:
                DispatchBroadcastReceive(item);
                return;
            default:
                _logger?.LogWarning("{Node} ignored unsupported native event kind {Kind}.", DisplayName, item.Kind);
                return;
        }
    }

    private void DispatchNativeSample(NativeNodeEvent item)
    {
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

    private async Task DispatchBroadcastPublishAsync(
        NativeNodeEvent item,
        CancellationToken cancellationToken)
    {
        if (_topicBus is null)
        {
            _logger?.LogWarning(
                "{Node} ignored native broadcast publish on {Topic}; no workflow TopicBus is available.",
                DisplayName,
                item.Topic);
            return;
        }

        try
        {
            JsonNode? payload = string.IsNullOrWhiteSpace(item.PayloadJson)
                ? null
                : JsonNode.Parse(item.PayloadJson);
            var topicMessage = BroadcastTopicCodec.FromWirePayload(item.PayloadKind, payload);
            _broadcastSources.Add(topicMessage, new BroadcastSource());
            await _topicBus.PublishAsync(item.Topic, topicMessage, ct: cancellationToken)
                .ConfigureAwait(false);
            _logger?.LogTrace(
                "{Node} published native broadcast on {Topic} with payload kind {PayloadKind}.",
                DisplayName,
                item.Topic,
                item.PayloadKind);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "{Node} failed to publish native broadcast on {Topic} with payload kind {PayloadKind}.",
                DisplayName,
                item.Topic,
                item.PayloadKind);
        }
    }

    private void DispatchBroadcastSubscribe(NativeNodeEvent item)
    {
        if (_topicBus is null)
        {
            _logger?.LogWarning(
                "{Node} ignored native broadcast subscription for {Topic}; no workflow TopicBus is available.",
                DisplayName,
                item.Topic);
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Topic) || item.Receiver == IntPtr.Zero)
        {
            _logger?.LogWarning("{Node} rejected an invalid native broadcast subscription.", DisplayName);
            return;
        }

        try
        {
            DispatchBroadcastUnsubscribe(item with { Kind = NativeNodeEventKind.BroadcastUnsubscribe });

            var subscription = _topicBus.Subscribe(
                item.Topic,
                message => QueueNativeBroadcastReceiveAsync(message, item.Receiver, item.ReceiverContext));
            _broadcastSubscriptions[item.Topic] =
                new NativeBroadcastRegistration(subscription, item.Receiver, item.ReceiverContext);
            _logger?.LogDebug("{Node} subscribed to native broadcast topic {Topic}.", DisplayName, item.Topic);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "{Node} failed to subscribe to native broadcast topic {Topic}.",
                DisplayName,
                item.Topic);
        }
    }

    private void DispatchBroadcastUnsubscribe(NativeNodeEvent item)
    {
        if (!_broadcastSubscriptions.TryGetValue(item.Topic, out var registration))
            return;

        _broadcastSubscriptions.Remove(item.Topic);
        registration.Subscription.Dispose();
        _logger?.LogDebug("{Node} unsubscribed from native broadcast topic {Topic}.", DisplayName, item.Topic);
    }

    private Task QueueNativeBroadcastReceiveAsync(
        TopicMessage message,
        IntPtr receiver,
        IntPtr receiverContext)
    {
        if (message.Payload is { } payload && _broadcastSources.TryGetValue(payload, out _))
            return Task.CompletedTask;

        try
        {
            var localPayload = BroadcastTopicCodec.ToLocalPayload(message.Payload);
            var wirePayload = BroadcastTopicCodec.ToWirePayload(localPayload);
            var payloadJson = wirePayload.Payload?.ToJsonString() ?? string.Empty;
            var item = NativeNodeEvent.ForBroadcastReceive(
                message.Topic,
                wirePayload.PayloadKind,
                payloadJson,
                message.MessageId.ToString("N"),
                receiver,
                receiverContext);

            if (Volatile.Read(ref _acceptEvents) == 0 || _eventChannel is null)
                return Task.CompletedTask;

            if (!_eventChannel.Writer.TryWrite(item))
            {
                _logger?.LogWarning(
                    "{Node} native event queue is full; broadcast {MessageId} on {Topic} was dropped.",
                    DisplayName,
                    message.MessageId,
                    message.Topic);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{Node} failed to queue broadcast {MessageId} on {Topic} for the native receiver.",
                DisplayName,
                message.MessageId,
                message.Topic);
        }

        return Task.CompletedTask;
    }

    private void DispatchBroadcastReceive(NativeNodeEvent item)
    {
        try
        {
            using var lease = NativeBroadcastLease.Create(
                item.Topic,
                item.PayloadKind,
                item.PayloadJson);
            var receiver = Marshal.GetDelegateForFunctionPointer<ReceiveBroadcastCallback>(item.Receiver);
            receiver(item.ReceiverContext, lease.Pointer);
            _logger?.LogTrace(
                "{Node} delivered native broadcast {BroadcastId} on {Topic} with payload kind {PayloadKind}.",
                DisplayName,
                item.BroadcastId,
                item.Topic,
                item.PayloadKind);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "{Node} failed to deliver native broadcast {BroadcastId} on {Topic}.",
                DisplayName,
                item.BroadcastId,
                item.Topic);
        }
    }

    private static int OnNativePublishBroadcast(IntPtr callbackContext, IntPtr message)
    {
        WindowsNativeNodeBase? node = null;
        try
        {
            if (!TryResolveNode(callbackContext, message, out node, out var broadcast))
                return NativeAbi.InvalidArgument;

            if (node is null || Volatile.Read(ref node._acceptEvents) == 0 || node._eventChannel is null)
                return NativeAbi.NotInitialized;

            var item = NativeNodeEvent.ForBroadcastPublish(
                broadcast.Topic,
                broadcast.PayloadKind,
                broadcast.PayloadJson);
            if (!node._eventChannel.Writer.TryWrite(item))
            {
                node._logger?.LogWarning(
                    "{Node} native event queue is full; broadcast publish on {Topic} was rejected.",
                    node.DisplayName,
                    broadcast.Topic);
                return NativeAbi.QueueFull;
            }

            return 0;
        }
        catch (Exception ex)
        {
            node?._logger?.LogError(ex, "{Node} failed to marshal a native broadcast publish callback.", node.DisplayName);
            return NativeAbi.InternalError;
        }
    }

    private static int OnNativeSubscribeBroadcast(
        IntPtr callbackContext,
        IntPtr topicPointer,
        ulong topicLength,
        IntPtr receive,
        IntPtr receiverContext)
    {
        WindowsNativeNodeBase? node = null;
        try
        {
            if (callbackContext == IntPtr.Zero || receive == IntPtr.Zero)
                return NativeAbi.InvalidArgument;
            node = GCHandle.FromIntPtr(callbackContext).Target as WindowsNativeNodeBase;
            if (node is null)
                return NativeAbi.InvalidArgument;

            var topic = ReadUtf8(topicPointer, topicLength);
            if (string.IsNullOrWhiteSpace(topic))
                return NativeAbi.InvalidArgument;

            if (Volatile.Read(ref node._acceptEvents) == 0 || node._eventChannel is null)
                return NativeAbi.NotInitialized;

            var item = NativeNodeEvent.ForBroadcastSubscribe(topic, receive, receiverContext);
            if (!node._eventChannel.Writer.TryWrite(item))
            {
                node._logger?.LogWarning(
                    "{Node} native event queue is full; broadcast subscription for {Topic} was rejected.",
                    node.DisplayName,
                    topic);
                return NativeAbi.QueueFull;
            }

            return 0;
        }
        catch (Exception ex)
        {
            node?._logger?.LogError(ex, "{Node} failed to marshal a native broadcast subscribe callback.", node.DisplayName);
            return NativeAbi.InternalError;
        }
    }

    private static int OnNativeUnsubscribeBroadcast(
        IntPtr callbackContext,
        IntPtr topicPointer,
        ulong topicLength)
    {
        WindowsNativeNodeBase? node = null;
        try
        {
            if (callbackContext == IntPtr.Zero)
                return NativeAbi.InvalidArgument;
            node = GCHandle.FromIntPtr(callbackContext).Target as WindowsNativeNodeBase;
            if (node is null)
                return NativeAbi.InvalidArgument;

            var topic = ReadUtf8(topicPointer, topicLength);
            if (string.IsNullOrWhiteSpace(topic))
                return NativeAbi.InvalidArgument;

            if (Volatile.Read(ref node._acceptEvents) == 0 || node._eventChannel is null)
                return NativeAbi.NotInitialized;

            if (!node._eventChannel.Writer.TryWrite(NativeNodeEvent.ForBroadcastUnsubscribe(topic)))
                return NativeAbi.QueueFull;

            return 0;
        }
        catch (Exception ex)
        {
            node?._logger?.LogError(ex, "{Node} failed to marshal a native broadcast unsubscribe callback.", node.DisplayName);
            return NativeAbi.InternalError;
        }
    }

    private static bool TryResolveNode(
        IntPtr callbackContext,
        IntPtr messagePointer,
        out WindowsNativeNodeBase? node,
        out NativeBroadcastMessage broadcast)
    {
        broadcast = default;
        node = null;
        if (callbackContext == IntPtr.Zero || messagePointer == IntPtr.Zero)
            return false;

        node = GCHandle.FromIntPtr(callbackContext).Target as WindowsNativeNodeBase;
        if (node is null)
            return false;

        var native = Marshal.PtrToStructure<AfBroadcast>(messagePointer);
        if (native.StructSize < NativeAbi.BroadcastStructSize || native.AbiVersion != NativeAbi.Version)
            return false;

        var topic = ReadUtf8(native.Topic, native.TopicLength);
        var payloadKind = ReadUtf8(native.PayloadKind, native.PayloadKindLength);
        var payloadJson = ReadUtf8(native.PayloadJson, native.PayloadJsonLength);
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(payloadKind))
            return false;

        broadcast = new NativeBroadcastMessage(topic, payloadKind, payloadJson);
        return true;
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
        _publishBroadcastCallback = null;
        _subscribeBroadcastCallback = null;
        _unsubscribeBroadcastCallback = null;
    }

    private void DisposeBroadcastSubscriptions()
    {
        foreach (var registration in _broadcastSubscriptions.Values)
            registration.Subscription.Dispose();
        _broadcastSubscriptions.Clear();
    }

    private void LogWarningBestEffort(Exception exception, string message)
    {
        _logger?.LogWarning(exception, "{Node}: {Message}", DisplayName, message);
    }

    private readonly record struct NativeBroadcastMessage(
        string Topic,
        string PayloadKind,
        string PayloadJson);

    private sealed record NativeBroadcastRegistration(
        ITopicSubscription Subscription,
        IntPtr Receiver,
        IntPtr ReceiverContext);

    private sealed class BroadcastSource
    {
    }

    private sealed class NativeBroadcastLease : IDisposable
    {
        private IntPtr _messagePointer;
        private readonly List<IntPtr> _buffers = new();

        private NativeBroadcastLease(IntPtr messagePointer)
        {
            _messagePointer = messagePointer;
        }

        public IntPtr Pointer => _messagePointer;

        public static NativeBroadcastLease Create(
            string topic,
            string payloadKind,
            string payloadJson)
        {
            var lease = new NativeBroadcastLease(IntPtr.Zero);
            var topicBuffer = lease.AllocateUtf8(topic, out var topicLength);
            var kindBuffer = lease.AllocateUtf8(payloadKind, out var kindLength);
            var payloadBuffer = lease.AllocateUtf8(payloadJson, out var payloadLength);

            var native = new AfBroadcast
            {
                StructSize = (uint)NativeAbi.BroadcastStructSize,
                AbiVersion = NativeAbi.Version,
                Topic = topicBuffer,
                TopicLength = topicLength,
                PayloadKind = kindBuffer,
                PayloadKindLength = kindLength,
                PayloadJson = payloadBuffer,
                PayloadJsonLength = payloadLength
            };

            lease._messagePointer = Marshal.AllocHGlobal(NativeAbi.BroadcastStructSize);
            Marshal.StructureToPtr(native, lease._messagePointer, fDeleteOld: false);
            return lease;
        }

        private IntPtr AllocateUtf8(string value, out ulong length)
        {
            if (string.IsNullOrEmpty(value))
            {
                length = 0;
                return IntPtr.Zero;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            _buffers.Add(buffer);
            length = (ulong)bytes.Length;
            return buffer;
        }

        public void Dispose()
        {
            if (_messagePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_messagePointer);
                _messagePointer = IntPtr.Zero;
            }

            foreach (var buffer in _buffers)
                Marshal.FreeHGlobal(buffer);
            _buffers.Clear();
        }
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
