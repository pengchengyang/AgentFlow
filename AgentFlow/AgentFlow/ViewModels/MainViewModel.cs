using System.Collections.ObjectModel;
using AgentFlow.Models;
using AgentFlow.Broadcast;
using AgentFlow.Core;
using AgentFlow.Services;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly InProcessGuiBridge _guiBridge = new();
    private CancellationTokenSource? _runCts;
    private int _nodeSpawnIndex;
    private bool _isLoading;

    private string WorkflowPath => Path.Combine(AppContext.BaseDirectory, "workflow.json");

    /// <summary>Localization (XAML can also use Loc.Instance directly).</summary>
    public Loc L => Loc.Instance;

    private readonly List<PaletteItem> _allPaletteItems = new();
    public ObservableCollection<PaletteItem> PaletteItems { get; } = new();
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    public ObservableCollection<ConnectionViewModel> SelectedConnections { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    // ---- Dedicated view-models (1 view &lt;-&gt; 1 view-model) ----
    public TopBarViewModel TopBar { get; }
    public NodeLibraryViewModel NodeLibrary { get; }
    public LogPanelViewModel LogPanel { get; }
    public InspectorViewModel Inspector { get; }
    public CanvasViewModel Canvas { get; }

    [ObservableProperty]
    private NodeViewModel? _selectedNode;

    [ObservableProperty]
    private bool _isRunning;

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

        _graph = new EditorGraph(_registry, _loggerFactory, _pluginLoader, _guiBridge);
        _graph.GraphChanged += RefreshPinConnections;

        LoadPlugins();

        // Auto-persist graph on every structural change (add/remove node, wire, or drag move).
        Nodes.CollectionChanged += (_, _) => AutoSave();
        Connections.CollectionChanged += (_, _) => AutoSave();

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
        // 创建契约层实例，并让 NodeModel / EditorGraph 共享同一个 BaseNode 实例。
        var instance = _pluginLoader.CreateNodeInstance(item.TypeId);
        var node = new NodeViewModel(new NodeModel(instance)) { Location = graphLocation };
        // Core 层用同一个实例构建运行时节点（持有 BaseNode + 运行时 pin）
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
                AutoSave();
        };
    }

    /// <summary>Core 层连接/断开后，刷新所有 pin 的 IsConnected 外观。</summary>
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
    private void AutoSave()
    {
        if (_isLoading) return;
        try { ToGraph().Save(WorkflowPath); }
        catch { /* best-effort persistence */ }
    }

    /// <summary>Reload the graph from disk if it exists.</summary>
    private void AutoLoad()
    {
        if (!File.Exists(WorkflowPath)) return;
        try
        {
            _isLoading = true;
            LoadGraph(WorkflowGraph.Load(WorkflowPath));
            StatusText = $"{L["LoadedFrom"]}: {WorkflowPath}";
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

        // 断开 input 对应的上游输出 pin 的 ConnectedInput 引用
        if (conn.Source.Node.Runtime?.Outputs.TryGetValue(conn.Source.Name, out var op) == true)
            op.ConnectedInput = null;

        Teardown(conn);
    }

    /// <summary>
    /// 在 Core 层建立连接：输出 pin 保存输入 pin 引用。
    /// GUI 只负责加一条视觉连线。
    /// </summary>
    private void Establish(PinViewModel source, PinViewModel target)
    {
        _graph.Connect(source.Node.Runtime!, source.Name, target.Node.Runtime!, target.Name);
        // 让 source 的真实输出 pin 记录下已连接的下游输入 pin 引用（target 中的真实 BasePin）。
        source.Node.Runtime!.Outputs[source.Name].ConnectedInput =
            target.Node.Runtime!.Inputs[target.Name];
        Connections.Add(new ConnectionViewModel(source, target));
    }

    /// <summary>在 Core 层移除连接并删视觉线。</summary>
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
        // 删除所有被选中的节点（支持多选）。
        var selected = Nodes.Where(n => n.IsSelected).ToList();
        foreach (var n in selected)
            RemoveNode(n);
        SelectedNode = null;
    }

    [RelayCommand]
    public void RemoveNode(NodeViewModel node)
    {
        // 1. 同步从 PluginLoader 实例容器中移除本节点实例
        if (node.Runtime?.RuntimeNode is { } instance)
            _pluginLoader.DeleteNodeInstance(instance);

        // 2. 所有上游输出 pin 连到本节点任一输入 pin 的，其 ConnectedInput 置 null
        foreach (var c in Connections.Where(c => ReferenceEquals(c.Target.Node, node)))
        {
            if (c.Source.Node.Runtime?.Outputs.TryGetValue(c.Source.Name, out var op) == true)
                op.ConnectedInput = null;
        }

        // Core 层删除节点及其所有连线（输出 pin 自动断开对端引用）
        if (node.Runtime is not null)
            _graph.RemoveNode(node.Runtime);

        // 同步删视觉连线
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

    /// <summary>双击节点：创建参数配置对话框 ViewModel 并请求 View 层弹出窗口。</summary>
    [RelayCommand]
    private void OpenNodeParameters(NodeViewModel? node)
    {
        if (node is null) return;
        DialogRequested?.Invoke(this, new NodeParameterDialogViewModel(node));
    }
    /// <summary>上下文菜单 Send：让指定节点立即执行一次并向下游推送数据。</summary>
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

    /// <summary>清空整个画布：先弹出确认框，仅确认后删除所有节点与连线并清空 Core 层（含 PluginLoader 实例容器）的底层数据。</summary>
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

    public WorkflowGraph ToGraph()
    {
        foreach (var n in Nodes)
        {
            if (n.Runtime is not null)
                _graph.UpdateNode(n.Runtime, n.Name, n.Priority, n.Location.X, n.Location.Y,
                    n.Parameters.ToDictionary(p => p.Key, p => p.ToValue()));
        }
        return _graph.ToWorkflowGraph();
    }

    public void LoadGraph(WorkflowGraph graph)
    {
        Nodes.Clear();
        Connections.Clear();
        _graph.Clear();

        var map = new Dictionary<string, NodeViewModel>();
        foreach (var spec in graph.Nodes)
        {
            // 创建契约层实例，并让 NodeModel / EditorGraph 共享同一个 BaseNode 实例。
            var instance = _pluginLoader.CreateNodeInstance(spec.TypeId);
            var node = new NodeViewModel(new NodeModel(instance))
            {
                Name = spec.Name ?? "",
                Priority = spec.Priority,
                Location = new Point(spec.X, spec.Y)
            };
            node.ApplyParameterValues(spec.Parameters);
            // Core 层用同一个实例构建运行时节点
            node.Runtime = _graph.AddNode(instance, spec.X, spec.Y,
                node.Parameters.ToDictionary(p => p.Key, p => p.ToValue()),
                spec.Id, spec.Name, spec.Priority);
            node.Id = node.Runtime.Id;
            HookSelection(node);
            Nodes.Add(node);
            map[spec.Id] = node;
        }

        foreach (var spec in graph.Connections)
        {
            var source = map[spec.FromNode].Outputs.First(p => p.Name == spec.FromPin);
            var target = map[spec.ToNode].Inputs.First(p => p.Name == spec.ToPin);
            Establish(source, target);
        }
    }

    // ---------- Run / save / load ----------

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        StatusText = L["Running"];
        _runCts = new CancellationTokenSource();
        try
        {
            var graph = ToGraph();
            var validationErrors = WorkflowValidation.Validate(_registry, graph);
            if (validationErrors.Count > 0)
            {
                StatusText = L["ValidationFailed"];
                foreach (var err in validationErrors)
                    Logs.Add($"[{L["ErrorPrefix"]}] {err}");
                return;
            }
            var engine = new WorkflowEngine(_registry, _loggerFactory, _guiBridge);
            await Task.Run(() => engine.RunAsync(graph, _runCts.Token));
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
    private void Stop() => _runCts?.Cancel();

    [RelayCommand]
    private void Save()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "workflow.json");
        ToGraph().Save(path);
        StatusText = $"{L["SavedTo"]}: {path}";
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
            LoadGraph(WorkflowGraph.Load(path));
            StatusText = $"{L["LoadedFrom"]}: {path}";
        }
        catch (Exception ex)
        {
            StatusText = L["LoadFailed"];
            Logs.Add($"[{L["ErrorPrefix"]}] {ex.Message}");
        }
    }
}










