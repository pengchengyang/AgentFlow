// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Windows.Input;
using AgentFlow.Models;
using AgentFlow.Broadcast;
using AgentFlow.Core;
using BaseNode = AgentFlow.Contracts.BaseNode;
using INodeContext = AgentFlow.Contracts.INodeContext;
using AgentFlow.Services;
using AgentFlow.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AgentFlow.ViewModels;

/// <summary>A node entry in the component palette.</summary>
public sealed record PaletteItem(string TypeId, string DisplayName, string Category, string PinSummary, SolidColorBrush Accent);

public partial class MainViewModel : ViewModelBase
{
    private readonly NodeRegistry _registry = new();
    private readonly PluginLoader _pluginLoader;
    private readonly EditorGraph _graph;
    private readonly NodeInstanceManager _nodeManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly InProcessGuiBridge _guiBridge = new();
    private CancellationTokenSource? _runCts;
    private int _nodeSpawnIndex;
    private bool _isLoading;
    private readonly IWorkflowStore _store;


    /// <summary>Optional override store; the Web (WASM) host sets this before the app starts.</summary>
    public static IWorkflowStore? DefaultWorkflowStore { get; set; }

    /// <summary>Localization (XAML can also use Loc.Instance directly).</summary>
    public Loc L => Loc.Instance;

    private readonly List<PaletteItem> _allPaletteItems = new();
    public ObservableCollection<PaletteItem> PaletteItems { get; } = new();
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    public ObservableCollection<ConnectionViewModel> SelectedConnections { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    // ---- Dedicated view-models (1 view <-> 1 view-model) ----
    public TopBarViewModel TopBar { get; }
    public NodeLibraryViewModel NodeLibrary { get; }
    public LogPanelViewModel LogPanel { get; }
    public InspectorViewModel Inspector { get; }
    public CanvasViewModel Canvas { get; }

    [ObservableProperty]
    private NodeViewModel? _selectedNode;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>True while the workflow is idle (not running). Drives the Start button enabled state.</summary>
    public bool IsIdle => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
    }
    /// <summary>True when the graph has unsaved changes since the last save/load.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _statusText = Loc.Instance["Ready"];

    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>Whether the bottom log panel is expanded (toggled by the Logs button).</summary>
    [ObservableProperty]
    private bool _isLogPanelOpen;

    /// <summary>Reset the canvas zoom/pan back to its default transform (delegates to the canvas view-model).</summary>
    [RelayCommand]
    private void ResetZoom() => Canvas.ResetZoom();

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value)
    {
        PaletteItems.Clear();
        foreach (var item in _allPaletteItems)
        {
            if (string.IsNullOrWhiteSpace(value)
                || item.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase)
                || item.TypeId.Contains(value, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(value, StringComparison.OrdinalIgnoreCase))
                PaletteItems.Add(item);
        }
    }

    public MainViewModel()
    {
        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new UiLoggerProvider(Logs));

        });

        _pluginLoader = new PluginLoader(_loggerFactory.CreateLogger(nameof(PluginLoader)));
        _nodeManager = new NodeInstanceManager(_pluginLoader);

        _graph = new EditorGraph(_registry, _loggerFactory, _pluginLoader, _guiBridge);

        _store = DefaultWorkflowStore ?? new LocalWorkflowStore();
        _graph.GraphChanged += RefreshPinConnections;

        LoadPlugins();

        // Auto-persist graph on every structural change (add/remove node, wire, or drag move).
        Nodes.CollectionChanged += (_, _) => { IsDirty = true; AutoSave(); };
        Connections.CollectionChanged += (_, _) => { IsDirty = true; AutoSave(); };

        // Restore the previous graph on startup.
        AutoLoad();

        // Show messages that nodes publish to the external GUI through the reusable broadcast DLL.
        BroadcastHub.Instance.Register(this);

        // Dedicated view-models (composed per view).
        TopBar = new TopBarViewModel(this);
        NodeLibrary = new NodeLibraryViewModel(this);
        LogPanel = new LogPanelViewModel(this);
        Inspector = new InspectorViewModel(this);
        Canvas = new CanvasViewModel(this);
    }

    [BroadcastHandler("result")]
    private void OnGuiResult(BroadcastMessage msg)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Logs.Add($"[GuiBridge] topic={msg.Topic} payload={msg.Payload}"));
    }

    // ---------- Language / theme switching ----------

    [RelayCommand]
    private void SwitchLanguage(string lang) => Loc.Instance.Switch(lang);

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void LoadPlugins()
    {
        var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        _pluginLoader.LoadFromDirectory(pluginDir, _registry);

        foreach (var d in _registry.Nodes.OrderBy(n => n.Category).ThenBy(n => n.DisplayName))
        {
            var pins = string.Join(", ", d.Pins.Select(p =>
                $"{(p.Direction == PinDirection.Input ? "in" : "out")}:{p.Name}:{p.DataType.Name}"));
            _allPaletteItems.Add(new PaletteItem(d.TypeId, d.DisplayName, d.Category, pins, CategoryColors.Accent(d.Category)));
        }
        OnSearchTextChanged(SearchText);
        StatusText = L.Fmt("NodesLoaded", _registry.Nodes.Count);
    }

    /// <summary>Add a node from the palette (auto-positioned for click-to-add).</summary>
    [RelayCommand]
    private void AddNode(PaletteItem item)
        => AddNodeAt(item, new Point(60 + (_nodeSpawnIndex % 6) * 60, 60 + (_nodeSpawnIndex % 6) * 40));

    /// <summary>Create a node from a palette item at a specific graph-space location (drag &amp; drop).</summary>
    public void AddNodeAt(PaletteItem item, Point graphLocation)
    {
        // Create a contract-layer instance; NodeModel and EditorGraph share the same BaseNode instance.
        var instance = _pluginLoader.CreateNodeInstance(item.TypeId);
        var node = new NodeViewModel(new NodeModel(instance)) { Location = graphLocation };
        // The Core layer builds a runtime node from the same instance (holds BaseNode + runtime pins)
        node.Runtime = _graph.AddNode(instance, graphLocation.X, graphLocation.Y);
        node.Id = node.Runtime.Id;
        _nodeSpawnIndex++;
        HookSelection(node);
        Nodes.Add(node);
    }
    public void ToggleSelection(NodeViewModel node)
    {
        node.IsSelected = !node.IsSelected;
        if (node.IsSelected)
            SelectedNode = node;
    }


    /// <summary>Raise a node to the top of the visual stacking order.</summary>
    public void BringToFront(NodeViewModel node)
        => node.ZIndex = Nodes.Count == 0 ? 0 : Nodes.Max(n => n.ZIndex) + 1;
    /// <summary>Single-selection mode: select only the given node and deselect every other node.</summary>
    public void SelectOnly(NodeViewModel? node)
    {
        foreach (var n in Nodes)
            n.IsSelected = ReferenceEquals(n, node);
        SelectedNode = node;
    }

    private void HookSelection(NodeViewModel node)
    {
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeViewModel.IsSelected) && node.IsSelected)
                SelectedNode = node;
            // Persist when the node is dragged to a new location.
            if (e.PropertyName == nameof(NodeViewModel.Location))
            {
                IsDirty = true;
                AutoSave();
            }
        };
    }

    /// <summary>After the Core layer connects / disconnects, refresh every pin's IsConnected visual state.</summary>
    private void RefreshPinConnections()
    {
        foreach (var node in Nodes)
        {
            foreach (var pin in node.Inputs)
            {
                var connected = _graph.Connections.Any(c =>
                    c.ToNode.Id == node.Id && c.ToPin == pin.Name);
                pin.IsConnected = connected;
            }
            foreach (var pin in node.Outputs)
            {
                var connected = _graph.Connections.Any(c =>
                    c.FromNode.Id == node.Id && c.FromPin == pin.Name);
                pin.IsConnected = connected;
            }
        }
    }

    /// <summary>Write the current graph to disk (no-op while we are itself loading).</summary>
    private async void AutoSave()
    {
        if (_isLoading) return;
        try
        {
            SyncGraphState();
            await _store.SaveAsync(GraphSerializer.Serialize(_graph));
        }
        catch { /* best-effort persistence */ }
    }

    /// <summary>Reload the graph from disk if it exists.</summary>
    private async void AutoLoad()
    {
        try
        {
            var json = await _store.LoadAsync();
            if (string.IsNullOrEmpty(json)) return;
            _isLoading = true;
            var doc = GraphDeserializer.Deserialize(json);
            LoadGraphFromDocument(doc);
            StatusText = $"{L["LoadedFrom"]}: {_store.Description}";
        }
        catch (Exception ex)
        {
            Logs.Add($"[{L["LoadFailed"]}] {ex.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Called by the canvas when a connection drag is dropped on an input pin.</summary>
    public bool TryCreateConnection(PinViewModel source, PinViewModel target)
    {
        // Normalize direction: source must be the output, target the input.
        if (source.IsInput && target.IsOutput)
            (source, target) = (target, source);

        if (!source.IsOutput || !target.IsInput)
        {
            Logs.Add(L["ErrMustOutToIn"]);
            return false;
        }

        var error = ValidateConnection(source, target);
        if (error is not null)
        {
            Logs.Add($"[{L["ConnRejected"]}] {error}");
            return false;
        }

        Establish(source, target);
        return true;
    }

    /// <summary>Detach the single wire connected to this input pin (drag input pin onto empty canvas).</summary>
    public void DisconnectInputPin(PinViewModel input)
    {
        var conn = Connections.FirstOrDefault(c => ReferenceEquals(c.Target, input));
        if (conn is null) return;

        Teardown(conn);
    }

    /// <summary>
    /// Establish the connection in the Core layer: the output pin saves a reference to the input pin
    /// and the input pin saves a reference back to the output pin (bidirectional ConnectedPin).
    /// The GUI only adds a visual wire.
    /// </summary>
    private void Establish(PinViewModel source, PinViewModel target)
    {
        _graph.Connect(source.Node.Runtime!, source.Name, target.Node.Runtime!, target.Name);
        Connections.Add(new ConnectionViewModel(source, target));
    }
    /// <summary>Remove the connection in the Core layer and delete the visual wire.</summary>
    private void Teardown(ConnectionViewModel connection)
    {
        var conn = _graph.Connections.FirstOrDefault(c =>
            c.FromNode.Id == connection.Source.Node.Id && c.FromPin == connection.Source.Name &&
            c.ToNode.Id == connection.Target.Node.Id && c.ToPin == connection.Target.Name);
        if (conn is not null)
            _graph.Disconnect(conn);
        Connections.Remove(connection);
    }

    private string? ValidateConnection(PinViewModel source, PinViewModel target)
    {
        if (!source.IsOutput || !target.IsInput)
            return L["ErrMustOutToIn"];
        if (ReferenceEquals(source.Node, target.Node))
            return L["ErrSelfConnect"];
        if (!target.DataType.IsAssignableFrom(source.DataType))
            return $"{L["ErrTypeMismatch"]}: {source.TypeName} -> {target.TypeName}";
        if (Connections.Any(c => ReferenceEquals(c.Target, target)))
            return $"{L["ErrPinOccupied"]}: {target.Name}";
        return null;
    }


    [RelayCommand]
    public void RemoveConnection(ConnectionViewModel connection) => Teardown(connection);

    [RelayCommand]
    private void RemoveSelectedConnections()
    {
        var selected = Connections.Where(c => c.IsSelected).ToList();
        foreach (var c in selected) Teardown(c);
    }

    /// <summary>Delete key: remove selected wires first, then the selected node.</summary>
    [RelayCommand]
    public void DeleteSelection()
    {
        foreach (var c in SelectedConnections.ToList())
        {
            SelectedConnections.Remove(c);
            Teardown(c);
        }
        // Delete all selected nodes (multi-select supported).
        var selected = Nodes.Where(n => n.IsSelected).ToList();
        foreach (var n in selected)
            RemoveNode(n);
        SelectedNode = null;
    }

    [RelayCommand]
    public void RemoveNode(NodeViewModel node)
    {
        // 1. Remove the node instance from the PluginLoader container (PluginLoader also releases its pins).
        if (node.Runtime?.RuntimeNode is { } instance)
            _pluginLoader.DeleteNodeInstance(instance);

        // 2. The Core layer removes the node and all its wires (output pins auto-disconnect their references).
        if (node.Runtime is not null)
            _graph.RemoveNode(node.Runtime);

        // Remove visual wires.
        var related = Connections
            .Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node))
            .ToList();
        foreach (var c in related) Connections.Remove(c);
        Nodes.Remove(node);
    }



    /// <summary>Whether the current user is logged in as Admin (Admin-only features gate on this).</summary>
    [ObservableProperty]
    private bool _isAdmin = true; // Dev: default account is Admin so admin-only UI is available on open.

    /// <summary>Bottom-right lock icon: unlocked (🔓) for Admin, locked (🔒) for all other accounts.</summary>
    public string LockIcon => IsAdmin ? "🔓" : "🔒";

    partial void OnIsAdminChanged(bool value) => OnPropertyChanged(nameof(LockIcon));

    /// <summary>Whether the right node-library Sidebar is visible (Admin only).</summary>
    [ObservableProperty]
    private bool _isSidebarVisible = true; // Dev: default to open.

    /// <summary>Arrow glyph for the Sidebar toggle: left chevron when open (click to hide), right when closed (click to show).</summary>
    public string SidebarToggleArrow => IsSidebarVisible ? "‹" : "›";

    /// <summary>Toggle the right node-library Sidebar (Admin only).</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    partial void OnIsSidebarVisibleChanged(bool value) => OnPropertyChanged(nameof(SidebarToggleArrow));

    /// <summary>Raised when the login dialog should be shown (View layer listens to open a window).</summary>
    public event EventHandler<LoginDialogViewModel>? LoginRequested;

    /// <summary>Top-right Login button: show the login dialog and update admin/Sidebar state by role.</summary>
    [RelayCommand]
    private void OpenLogin()
    {
        var dialog = new LoginDialogViewModel();
        dialog.RequestClose += (_, isAdmin) =>
        {
            IsAdmin = isAdmin;
            IsSidebarVisible = isAdmin;
        };
        LoginRequested?.Invoke(this, dialog);
    }

    /// <summary>Raised when a node parameter dialog should be shown (View layer listens to open a window).</summary>
    public event EventHandler<NodeParameterDialogViewModel>? DialogRequested;

    /// <summary>Double-click a node: create the parameter dialog ViewModel and ask the View layer to show it.</summary>
    [RelayCommand]
    private void OpenNodeParameters(NodeViewModel? node)
    {
        if (node is null) return;
        DialogRequested?.Invoke(this, new NodeParameterDialogViewModel(node));
    }
    /// <summary>Context-menu Send: execute the given node once and push data downstream.</summary>
    [RelayCommand]
    private async Task SendNode(NodeViewModel node)
    {
        if (node.Runtime is null) return;
        try
        {
            await _graph.SendAsync(node.Runtime);
            Logs.Add($"[Send] {node.Title} executed.");
        }
        catch (Exception ex)
        {
            Logs.Add($"[{L["ErrorPrefix"]}] {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleLogPanel() => IsLogPanelOpen = !IsLogPanelOpen;

    [RelayCommand]
    private void ClearLog() => Logs.Clear();
    /// <summary>Raised when the clear-canvas confirmation dialog should be shown (View layer listens to open a window).</summary>
    public event EventHandler<ConfirmDialogViewModel>? ClearRequested;

    /// <summary>Clear the whole canvas: show a confirmation first; only on confirm delete all nodes and wires and clear the Core layer (including the PluginLoader instance container).</summary>
    [RelayCommand]
    private void ClearGraph()
    {
        var dialog = new ConfirmDialogViewModel(
            "Clear Canvas",
            "Clear the entire canvas and all underlying data? This cannot be undone.",
            "Confirm", "Cancel");
        dialog.RequestClose += (_, confirmed) =>
        {
            if (!confirmed) return;
            Nodes.Clear();
            Connections.Clear();
            SelectedNode = null;
            _graph.Clear();
            Logs.Add("Canvas cleared.");
        };
        ClearRequested?.Invoke(this, dialog);
    }

    // ---------- Graph <-> ViewModel mapping ----------

    /// <summary>
    /// Push current UI state (position, name, priority, property-panel values) from the
    /// NodeViewModels into the Core EditorGraph so the serializer / engine sees the latest state.
    /// </summary>
    private void SyncGraphState()
    {
        foreach (var n in Nodes)
        {
            if (n.Runtime is not null)
            {
                var values = n.Parameters.ToDictionary(p => p.Key, p => p.ToValue());
                n.Runtime.RuntimeNode.Configure(values);
                _graph.UpdateNode(n.Runtime, n.Name, n.Priority, n.Location.X, n.Location.Y, values);
            }
        }
    }

    public WorkflowGraph ToGraph()
    {
        SyncGraphState();
        return _graph.ToWorkflowGraph();
    }

    /// <summary>
    /// Rebuild the canvas and Core graph from a layered <see cref="GraphDocument"/>.
    /// For every node the contract-layer <see cref="Contracts.BaseNode.DeserializeParameters"/>
    /// restores the logical / runtime state; UI state is applied around it.
    /// </summary>
    public void LoadGraphFromDocument(GraphDocument doc)
    {
        Nodes.Clear();
        Connections.Clear();
        _graph.Clear();

        var map = new Dictionary<string, NodeViewModel>();
        foreach (var spec in doc.Nodes)
        {
            // Create a contract-layer instance; NodeModel and EditorGraph share the same instance.
            // typeId now lives inside the logic blob (no longer duplicated on the outer node object).
            var typeId = spec.Logic?["typeId"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"Node '{spec.Id}' is missing typeId in logic.");
            var nodeName = spec.Logic?["name"]?.GetValue<string>() ?? "";
            var instanceId = spec.Logic?["instanceId"]?.GetValue<int>() ?? 0;
            var dependsOn = spec.Logic?["dependsOn"]?.GetValue<int>() ?? 0;

            // PluginLoader now owns restore: create node/pin instances, reserve the saved
            // instance id, set dependsOn and deserialize the logic blob.
            var instance = _pluginLoader.CreateNodeInstance(typeId, instanceId, dependsOn, spec.Logic);

            var node = new NodeViewModel(new NodeModel(instance))
            {
                Name = nodeName,
                Location = new Point(spec.X, spec.Y)
            };

            // Reflect restored logical values into the property panel (best effort).
            var restored = new Dictionary<string, object?>();
            foreach (var p in instance.NodeParameters)
            {
                if (!string.IsNullOrEmpty(p.Name))
                    restored[p.Name] = p.Value;
            }
            node.ApplyParameterValues(restored);

            // The Core layer builds a runtime node from the same instance.
            node.Runtime = _graph.AddNode(instance, spec.X, spec.Y,
                node.Parameters.ToDictionary(p => p.Key, p => p.ToValue()),
                spec.Id, nodeName);
            node.Id = node.Runtime.Id;
            HookSelection(node);
            Nodes.Add(node);
            map[spec.Id] = node;
        }

        // Restore connections by pin instance id. Core locates the runtime pins; the UI only
        // maps them back to the visual PinViewModels so a wire can be drawn.
        foreach (var spec in doc.Connections)
        {
            var from = _graph.FindPinById(spec.FromPinId);
            var to = _graph.FindPinById(spec.ToPinId);
            if (from is null || to is null) continue;
            if (!from.Value.IsOutput || to.Value.IsOutput) continue;

            var sourceNode = Nodes.FirstOrDefault(n => n.Id == from.Value.Node.Id);
            var targetNode = Nodes.FirstOrDefault(n => n.Id == to.Value.Node.Id);
            if (sourceNode is null || targetNode is null) continue;

            var source = sourceNode.Outputs.FirstOrDefault(p => p.Name == from.Value.PinName);
            var target = targetNode.Inputs.FirstOrDefault(p => p.Name == to.Value.PinName);
            if (source is null || target is null) continue;

            if (_graph.TryConnectByPinId(spec.FromPinId, spec.ToPinId))
                Connections.Add(new ConnectionViewModel(source, target));
        }
    }

    // ---------- Run / save / load ----------

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;

        // Validate BEFORE marking running, so a validation failure doesn't flash the button.
        var graph = ToGraph();
        var validationErrors = WorkflowValidation.Validate(_registry, graph);
        if (validationErrors.Count > 0)
        {
            StatusText = L["ValidationFailed"];
            foreach (var err in validationErrors)
                Logs.Add($"[{L["ErrorPrefix"]}] {err}");
            return;
        }

        IsRunning = true;
        StatusText = L["Running"];
        _runCts = new CancellationTokenSource();

        // Yield to the UI thread so the Stop icon renders before the run starts.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => { }, Avalonia.Threading.DispatcherPriority.Render);

        try
        {
            var contexts = BuildNodeContexts();
            await Task.Run(() => _nodeManager.RunAsync(node => contexts[node], _runCts.Token));
            StatusText = L["RunCompleted"];
        }
        catch (OperationCanceledException)
        {
            StatusText = L["RunCancelled"];
        }
        catch (Exception ex)
        {
            StatusText = L["RunFailed"];
            Logs.Add($"[{L["ErrorPrefix"]}] {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private async Task Stop()
    {
        _runCts?.Cancel();
        try
        {
            await Task.Run(() => _nodeManager.StopAsync(GetNodeContext));
        }
        catch (Exception ex)
        {
            Logs.Add($"[{L["ErrorPrefix"]}] {ex.Message}");
        }
    }

    /// <summary>Build a node context for the given runtime instance so NodeInstanceManager can run it.</summary>
    private INodeContext GetNodeContext(BaseNode node) => _graph.CreateContext(node);

    /// <summary>
    /// Pre-create contexts for every managed instance and bind them to the runtime pins before a
    /// run. This ensures downstream input pins already have a context when an upstream node pushes
    /// data, even if the downstream node has not been reached in the dependency-ordered loop yet.
    /// </summary>
    private Dictionary<BaseNode, INodeContext> BuildNodeContexts()
    {
        var contexts = new Dictionary<BaseNode, INodeContext>();
        foreach (var node in _pluginLoader.Instances)
            contexts[node] = _graph.CreateContext(node);
        return contexts;
    }

    [RelayCommand]
    private async void Save()
    {
        SyncGraphState();
        await _store.SaveAsync(GraphSerializer.Serialize(_graph));
        IsDirty = false;
        StatusText = $"{L["SavedTo"]}: {_store.Description}";
    }

    /// <summary>Raised when the user clicks the Save-As button; the View layer opens a file picker.</summary>
    public event EventHandler<string>? SaveAsRequested;

    /// <summary>Save-As: raise an event so the View layer can show a native file-picker dialog.</summary>
    [RelayCommand]
    private void SaveAs()
    {
        var suggested = $"AgentFlow_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
        SaveAsRequested?.Invoke(this, suggested);
    }

    /// <summary>
    /// Persist the current graph to an explicit path chosen by the user.
    /// Called by the View layer after the file picker returns a valid path.
    /// </summary>
    public void SaveToPath(string path)
    {
        SyncGraphState();
        GraphSerializer.Save(_graph, path);
        IsDirty = false;
        StatusText = $"{L["SavedTo"]}: {path}";
    }

    /// <summary>Raised when the user clicks the Open button; the View layer opens a file picker.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Open: raise an event so the View layer can show a native file-open picker.</summary>
    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Load a workflow from an explicit path chosen by the user.
    /// Called by the View layer after the file picker returns a valid path.
    /// </summary>
    public void LoadFromPath(string path)
    {
        var doc = GraphDeserializer.Load(path);
        LoadGraphFromDocument(doc);
        IsDirty = false;
        StatusText = $"{L["LoadedFrom"]}: {path}";
    }

    [RelayCommand]
    private void Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "workflow.json");
        if (!File.Exists(path))
        {
            StatusText = L["NoWorkflowFile"];
            return;
        }
        try
        {
            LoadGraphFromDocument(GraphDeserializer.Load(path));
            StatusText = $"{L["LoadedFrom"]}: {path}";
        }
        catch (Exception ex)
        {
            StatusText = L["LoadFailed"];
            Logs.Add($"[{L["ErrorPrefix"]}] {ex.Message}");
        }
    }
}





