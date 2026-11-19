using System.Runtime.InteropServices;

namespace AgentFlow.Windows.NativeHost;

internal sealed class NativeNodeModule : IDisposable
{
    private NativeLibraryHandle? _library;
    private IntPtr _nodeContext;
    private bool _shutdownCalled;

    private NodeLifecycleDelegate? _initialize;
    private NodeLifecycleDelegate? _run;
    private ReceiveSampleDelegate? _receiveSample;
    private NodeLifecycleDelegate? _stop;
    private NodeLifecycleDelegate? _shutdown;
    private DestroyNodeDelegate? _destroy;
    private GetLastErrorDelegate? _getLastError;

    public bool IsCreated => _nodeContext != IntPtr.Zero;

    public void Load(string libraryPath, int expectedAbiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedAbiVersion);
        if (_library is not null)
            throw new InvalidOperationException("A native module is already loaded.");

        var library = NativeLibraryHandle.Load(libraryPath);
        try
        {
            var getAbiVersion = GetExport<GetAbiVersionDelegate>(library, "af_node_get_abi_version");
            var create = GetExport<CreateNodeDelegate>(library, "af_node_create");
            _initialize = GetExport<NodeLifecycleDelegate>(library, "af_node_initialize");
            _run = GetExport<NodeLifecycleDelegate>(library, "af_node_run");
            _receiveSample = GetExport<ReceiveSampleDelegate>(library, "af_node_receive_sample");
            _stop = GetExport<NodeLifecycleDelegate>(library, "af_node_stop");
            _shutdown = GetExport<NodeLifecycleDelegate>(library, "af_node_shutdown");
            _destroy = GetExport<DestroyNodeDelegate>(library, "af_node_destroy");
            _getLastError = GetExport<GetLastErrorDelegate>(library, "af_node_get_last_error");

            var actualAbiVersion = getAbiVersion();
            if (actualAbiVersion != expectedAbiVersion || actualAbiVersion != NativeAbi.Version)
            {
                throw new InvalidOperationException(
                    $"Native ABI mismatch in '{libraryPath}'. Expected {expectedAbiVersion}, actual {actualAbiVersion}.");
            }

            _library = library;
            _create = create;
        }
        catch
        {
            library.Dispose();
            ResetExports();
            throw;
        }
    }

    private CreateNodeDelegate? _create;

    public void Create(IntPtr hostVTable, IntPtr callbackContext)
    {
        if (_create is null)
            throw new InvalidOperationException("The native module has not been loaded.");
        if (_nodeContext != IntPtr.Zero)
            throw new InvalidOperationException("The native node context has already been created.");

        var status = _create(hostVTable, callbackContext, out var nodeContext);
        if (status != 0)
            throw new InvalidOperationException($"af_node_create failed with status {status}.");
        if (nodeContext == IntPtr.Zero)
            throw new InvalidOperationException("af_node_create returned a null node context.");

        _nodeContext = nodeContext;
        _shutdownCalled = false;
    }

    public void Initialize()
    {
        EnsureLifecycle(_initialize, "af_node_initialize");
    }

    public void Run()
    {
        EnsureLifecycle(_run, "af_node_run");
    }

    public int ReceiveSample(IntPtr sample)
    {
        if (_nodeContext == IntPtr.Zero || _receiveSample is null)
            return NativeAbi.NotInitialized;
        return _receiveSample(_nodeContext, sample);
    }

    public void Stop()
    {
        if (_nodeContext == IntPtr.Zero || _stop is null)
            return;
        EnsureStatus(_stop(_nodeContext), "af_node_stop");
    }

    public void Shutdown()
    {
        if (_nodeContext == IntPtr.Zero || _shutdownCalled || _shutdown is null)
            return;
        EnsureStatus(_shutdown(_nodeContext), "af_node_shutdown");
        _shutdownCalled = true;
    }

    public string GetLastError()
    {
        if (_nodeContext == IntPtr.Zero || _getLastError is null)
            return string.Empty;

        var ptr = _getLastError(_nodeContext);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    public void Dispose()
    {
        if (_nodeContext != IntPtr.Zero)
        {
            if (!_shutdownCalled && _shutdown is not null)
            {
                try { _shutdown(_nodeContext); }
                catch (Exception) { }
            }

            try { _destroy?.Invoke(_nodeContext); }
            catch (Exception) { }

            _nodeContext = IntPtr.Zero;
        }

        _library?.Dispose();
        _library = null;
        ResetExports();
    }

    private void EnsureLifecycle(NodeLifecycleDelegate? lifecycle, string name)
    {
        if (_nodeContext == IntPtr.Zero || lifecycle is null)
            throw new InvalidOperationException($"Native node is not created; cannot call {name}.");
        EnsureStatus(lifecycle(_nodeContext), name);
    }

    private static void EnsureStatus(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"{operation} failed with status {status}.");
    }

    private static T GetExport<T>(NativeLibraryHandle library, string exportName) where T : Delegate
    {
        var ptr = NativeLibrary.GetExport(library.DangerousGetHandle(), exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private void ResetExports()
    {
        _create = null;
        _initialize = null;
        _run = null;
        _receiveSample = null;
        _stop = null;
        _shutdown = null;
        _destroy = null;
        _getLastError = null;
    }
}

internal sealed class NativeLibraryHandle : SafeHandle
{
    private NativeLibraryHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public static NativeLibraryHandle Load(string path)
    {
        var handle = NativeLibrary.Load(path);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Native library could not be loaded: {path}");
        var safeHandle = new NativeLibraryHandle();
        safeHandle.SetHandle(handle);
        return safeHandle;
    }

    protected override bool ReleaseHandle()
    {
        NativeLibrary.Free(handle);
        return true;
    }
}


