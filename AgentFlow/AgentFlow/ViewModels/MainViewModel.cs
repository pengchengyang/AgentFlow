// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using System.Windows.Input;
using AgentFlow.Models;
using AgentFlow.Core;
using NodeState = AgentFlow.Contracts.NodeState;
using AgentFlow.Services;
using AgentFlow.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Hosting.Logging;
namespace AgentFlow.ViewModels;
/// <summary>A node entry in the component palette.</summary>
public sealed record PaletteItem(string Name, string DisplayName, string Category, string PinSummary, SolidColorBrush Accent);
internal sealed record CopiedNodeSnapshot(
    string OriginalId, string TypeName, string DisplayName, Point Location, int Priority,
    IReadOnlyDictionary<string, object?> Parameters);
internal sealed record CopiedConnectionSnapshot(
    string FromNodeId, string FromPin, string ToNodeId, string ToPin);
public partial class MainViewModel : ViewModelBase
{
    private readonly NodeRegistry _registry = new();
    private readonly PluginLoader _pluginLoader;
    private readonly EditorGraph _graph;
    private readonly NodeInstanceManager _nodeManager;
    private readonly WorkflowHost _workflowHost;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly BroadcastDemo _broadcastDemo;
    private CancellationTokenSource? _runCts;
    private int _nodeSpawnIndex;
    private readonly List<CopiedNodeSnapshot> _nodeClipboard = new();
    private readonly List<CopiedConnectionSnapshot> _connectionClipboard = new();
    private Point _copyOrigin;
    private int _pasteCount;
    private bool _isLoading;
    private bool _suppressAutoSave;
    private bool _autoSaveInFlight;
    private bool _autoSavePending;
    private readonly DispatcherTimer _autoSaveTimer;
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
    // ---- Dedicated view-models (1 view <-> 1 view-model) ----
    public NodeLibraryViewModel NodeLibrary { get; }
    public LogPanelViewModel LogPanel { get; }
    public CanvasViewModel Canvas { get; }
    [ObservableProperty]
    private NodeViewModel? _selectedNode;
    /// <summary>
    /// True only while every canvas node has been started successfully (initialized, Run returned
    /// without errors) and has not been stopped yet. Derived by <see cref="RefreshRunState"/> from the
    /// nodes' real runtime state — never set optimistically on Start.
    /// </summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>True while at least one node is still starting up (connecting) and none has failed yet.</summary>
    [ObservableProperty]
    private bool _isStarting;

    /// <summary>True when at least one node failed to start and nothing is running / starting.</summary>
    [ObservableProperty]
    private bool _isRunFailed;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanUndoNow));
        OnPropertyChanged(nameof(CanRedoNow));
    }

    partial void OnIsStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanUndoNow));
        OnPropertyChanged(nameof(CanRedoNow));
    }

    /// <summary>Whether the workflow is running or still starting (the Start button is disabled while busy).</summary>
    public bool IsBusy => IsRunning || IsStarting;

    /// <summary>Undo is available only when there is history and the workflow is not active.</summary>
    public bool CanUndoNow => CanUndo && !IsBusy;

    /// <summary>Redo is available only when there is redo history and the workflow is not active.</summary>
    public bool CanRedoNow => CanRedo && !IsBusy;

    /// <summary>
    /// Stop is available only while a workflow is starting or running. A failed start is already
    /// stopped by Core, so the controls return to their initial state: Run enabled, Stop disabled.
    /// </summary>
    public bool CanStop => IsRunning || IsStarting;

    /// <summary>Indicator brushes: green only when the whole workflow is really running.</summary>
    private static readonly IBrush RunningIndicatorBrush = new SolidColorBrush(Color.Parse("#12B76A"));
    private static readonly IBrush StartingIndicatorBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush FailedIndicatorBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush IdleIndicatorBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));
    /// <summary>True when the graph has unsaved changes since the last save/load.</summary>
    [ObservableProperty]
    private bool _isDirty;

    private const int MaxHistoryEntries = 50;
    private readonly List<string> _undoHistory = new();
    private readonly List<string> _redoHistory = new();
    private bool _isApplyingHistory;
    private string? _dragHistorySnapshot;

    /// <summary>Whether the graph history contains an undo operation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoNow))]
    private bool _canUndo;

    /// <summary>Whether the graph history contains a redo operation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedoNow))]
    private bool _canRedo;

    // Set once the window starts closing. RunAsync re-checks it after validation so a Start
    // clicked immediately before Close cannot launch nodes after the shutdown stop has completed.
    private bool _shuttingDown;

    // True from the moment RunAsync publishes its run session until its finally block. The close
    // path uses it to stop a start that is still inside StartAsync even though IsRunning/ IsStarting
    // (refreshed every 300 ms) have not caught up yet.
    private bool _runInProgress;

    // Monotonic revision of in-memory edits. Auto-save captures it before serializing so a change
    // made while the write is in flight is not marked as saved by the completing write.
    private int _changeVersion;

    private void MarkDirty()
    {
        IsDirty = true;
        _changeVersion++;
    }

    /// <summary>Capture a complete graph snapshot for the undo / redo history.</summary>
    private string? CaptureHistorySnapshot()
    {
        if (_isLoading || _isApplyingHistory)
            return null;

        SyncGraphState();
        return GraphSerializer.Serialize(_graph);
    }

    /// <summary>Record a completed edit when its before/after snapshots differ.</summary>
    private void RecordHistory(string? beforeSnapshot)
    {
        if (beforeSnapshot is null || _isLoading || _isApplyingHistory)
            return;

        var afterSnapshot = CaptureHistorySnapshot();
        if (afterSnapshot is null ||
            string.Equals(beforeSnapshot, afterSnapshot, StringComparison.Ordinal))
        {
            return;
        }

        _undoHistory.Add(beforeSnapshot);
        TrimHistory(_undoHistory);
        _redoHistory.Clear();
        UpdateHistoryAvailability();
    }

    private static void TrimHistory(List<string> history)
    {
        while (history.Count > MaxHistoryEntries)
            history.RemoveAt(0);
    }

    private void UpdateHistoryAvailability()
    {
        CanUndo = _undoHistory.Count > 0;
        CanRedo = _redoHistory.Count > 0;
    }

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _dragHistorySnapshot = null;
        UpdateHistoryAvailability();
    }

    /// <summary>Restore a snapshot and keep the graph dirty so the restored state is persisted.</summary>
    private void ApplyHistorySnapshot(string snapshot)
    {
        _autoSaveTimer.Stop();
        _isApplyingHistory = true;
        try
        {
            var document = GraphDeserializer.Deserialize(snapshot);
            LoadGraphFromDocument(document);
        }
        finally
        {
            _isApplyingHistory = false;
        }

        MarkDirty();
        AutoSaveNow();
    }

    /// <summary>Restore the most recent snapshot from the undo stack.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (!CanUndo || !CanEditGraphNow())
            return;

        string? currentSnapshot;
        try
        {
            currentSnapshot = CaptureHistorySnapshot();
        }
        catch (Exception ex)
        {
            StatusText = L["ErrorPrefix"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Capturing undo history failed.");
            return;
        }

        if (currentSnapshot is null)
            return;

        var previousSnapshot = _undoHistory[^1];
        try
        {
            ApplyHistorySnapshot(previousSnapshot);
            _undoHistory.RemoveAt(_undoHistory.Count - 1);
            _redoHistory.Add(currentSnapshot);
            TrimHistory(_redoHistory);
            UpdateHistoryAvailability();
            StatusText = "Undo";
            _logger.LogTrace("Undo applied.");
        }
        catch (Exception ex)
        {
            StatusText = L["ErrorPrefix"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Undo failed.");
        }
    }

    /// <summary>Reapply the most recently undone snapshot.</summary>
    [RelayCommand]
    private void Redo()
    {
        if (!CanRedo || !CanEditGraphNow())
            return;

        string? currentSnapshot;
        try
        {
            currentSnapshot = CaptureHistorySnapshot();
        }
        catch (Exception ex)
        {
            StatusText = L["ErrorPrefix"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Capturing redo history failed.");
            return;
        }

        if (currentSnapshot is null)
            return;

        var nextSnapshot = _redoHistory[^1];
        try
        {
            ApplyHistorySnapshot(nextSnapshot);
            _redoHistory.RemoveAt(_redoHistory.Count - 1);
            _undoHistory.Add(currentSnapshot);
            TrimHistory(_undoHistory);
            UpdateHistoryAvailability();
            StatusText = "Redo";
            _logger.LogTrace("Redo applied.");
        }
        catch (Exception ex)
        {
            StatusText = L["ErrorPrefix"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Redo failed.");
        }
    }

    /// <summary>Capture the pre-drag state once, before pointer movement updates node coordinates.</summary>
    public void BeginNodeDragHistory()
    {
        _dragHistorySnapshot = CaptureHistorySnapshot();
    }

    /// <summary>Commit a node drag as one undoable action.</summary>
    public void EndNodeDragHistory()
    {
        var beforeSnapshot = _dragHistorySnapshot;
        _dragHistorySnapshot = null;
        RecordHistory(beforeSnapshot);
    }
    [ObservableProperty]
    private string _statusText = Loc.Instance["Ready"];

    /// <summary>Text for the running indicator (fixed "Running" when active, "Ready" when idle).</summary>
    [ObservableProperty]
    private string _runningText = "Ready";

    /// <summary>Opacity of the 3 animated dots (only one is fully visible at a time).</summary>
    [ObservableProperty] private double _dot1Opacity = 0.3;
    [ObservableProperty] private double _dot2Opacity = 0.3;
    [ObservableProperty] private double _dot3Opacity = 0.3;

    /// <summary>Brush for the running icon (green when running, gray when idle).</summary>
    [ObservableProperty]
    private IBrush? _runningIconBrush;

    /// <summary>Blur radius of the glow effect around the running icon (pulsing when active).</summary>
    [ObservableProperty]
    private double _pulseBlur = 0;

    private readonly DispatcherTimer _runTimer;
    private int _dotStep;
    /// <summary>Whether the current run already reported a start failure in the status line (edge trigger).</summary>
    private bool _runFailureNotified;
    /// <summary>Full path of the currently open / saved JSON workflow file.</summary>
    [ObservableProperty]
    private string _currentFilePath = "";
    [ObservableProperty]
    private bool _isDarkTheme;
    /// <summary>Whether the bottom log panel is expanded (toggled by the Logs button).</summary>
    [ObservableProperty]
    private bool _isLogPanelOpen;
    /// <summary>Whether the log panel fills the whole canvas area (double-click toggle).</summary>
    [ObservableProperty]
    private bool _isLogPanelExpanded;
    public void ToggleLogPanelExpanded() => IsLogPanelExpanded = !IsLogPanelExpanded;

    /// <summary>
    /// Bottom inset the canvas reserves while the log panel is docked, so canvas content is never
    /// drawn underneath it. Zero when the panel is closed or when it fills the whole area.
    /// </summary>
    public Thickness CanvasLogInset => IsLogPanelOpen && !IsLogPanelExpanded
        ? new Thickness(0, 0, 0, BoolToPanelHeightConverter.DockedHeight)
        : default;

    /// <summary>Closing the panel also clears its expanded state, otherwise the canvas stays hidden.</summary>
    partial void OnIsLogPanelOpenChanged(bool value)
    {
        if (!value)
            IsLogPanelExpanded = false;
        OnPropertyChanged(nameof(CanvasLogInset));
    }

    partial void OnIsLogPanelExpandedChanged(bool value) => OnPropertyChanged(nameof(CanvasLogInset));

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
                || item.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
                || (item.DisplayName?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.Category?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false))
                PaletteItems.Add(item);
        }
    }
    public MainViewModel()
    {
        _loggerFactory = AppLog.CreateLoggerFactory();
        _logger = _loggerFactory.CreateLogger(nameof(MainViewModel));

        _pluginLoader = new PluginLoader(_loggerFactory.CreateLogger(nameof(PluginLoader)));
        _nodeManager = new NodeInstanceManager(_pluginLoader, _loggerFactory.CreateLogger(nameof(NodeInstanceManager)));
        _graph = new EditorGraph(_registry, _loggerFactory, _pluginLoader);
        _workflowHost = new WorkflowHost(
            _registry,
            _pluginLoader,
            _graph,
            _nodeManager,
            _loggerFactory);

        // Generic TopicBus demo: the GUI and Node.Broadcast.Demo share only topic strings.
        // Subscribe before LoadPlugins so the demo node's initial message is not lost.
        _broadcastDemo = new BroadcastDemo(
            message => Log(message, "INFO", "Broadcast.Demo"));

        _store = DefaultWorkflowStore ?? new LocalWorkflowStore();
        _graph.GraphChanged += RefreshPinConnections;
        LoadPlugins();
        // Debounce auto-save: a node drag updates Location on every pointer move, so writing the
        // whole document synchronously for every event made large graphs freeze the UI.
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            AutoSaveNow();
        };
        // Auto-persist graph after structural changes (add/remove node, wire, drag move). The
        // collection handlers also mark the graph dirty; while loading, both are suppressed so a
        // half-built document is never written over the user's file.
        Nodes.CollectionChanged += (_, _) =>
        {
            if (!_isLoading && !_suppressAutoSave)
            {
                MarkDirty();
                ScheduleAutoSave();
            }
            RefreshRunState();
        };
        Connections.CollectionChanged += (_, _) =>
        {
            if (!_isLoading && !_suppressAutoSave)
            {
                MarkDirty();
                ScheduleAutoSave();
            }
        };
        // Restore the previous graph on startup.
        AutoLoad();
        // Dedicated view-models (composed per view).
        NodeLibrary = new NodeLibraryViewModel(this);
        LogPanel = new LogPanelViewModel(this);
        Canvas = new CanvasViewModel(this);

        // Run indicator: the state itself is derived from the nodes' real runtime state (re-derived
        // on every tick, so a node that connects later or fails asynchronously shows up within
        // ~300 ms); this timer only animates it (dots marquee + glow pulse).
        _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _runTimer.Tick += (_, _) =>
        {
            RefreshRunState();
            if (!IsBusy)
                return;
            _dotStep = (_dotStep + 1) % 3;
            Dot1Opacity = _dotStep == 0 ? 1.0 : 0.25;
            Dot2Opacity = _dotStep == 1 ? 1.0 : 0.25;
            Dot3Opacity = _dotStep == 2 ? 1.0 : 0.25;
            // Glow pulse: blur radius alternates between 2 and 16 for more visible breathing.
            PulseBlur = (_dotStep % 2 == 0) ? 16 : 2;
        };
        _runTimer.Start();
        RefreshRunState();
    }
    // ---------- Language / theme switching ----------
    [RelayCommand]
    private void SwitchLanguage(string lang) => Loc.Instance.Switch(lang);
    [RelayCommand]
    private void ToggleTheme()
    {
        App.ToggleTheme();
    }
    private void LoadPlugins()
    {
        var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        _workflowHost.LoadPlugins(pluginDir);
        var nodeTypes = _workflowHost.GetNodeTypes();
        foreach (var d in nodeTypes)
        {
            var pins = string.Join(", ", d.InputPins.Select(p => $"in:{p.Name}:{p.DataType}")
                .Concat(d.OutputPins.Select(p => $"out:{p.Name}:{p.DataType}")));
            _allPaletteItems.Add(new PaletteItem(d.Name, d.DisplayName, d.Category, pins, CategoryColors.AccentByInitial(d.Name)));
        }
        OnSearchTextChanged(SearchText);
        StatusText = L.Fmt("NodesLoaded", nodeTypes.Count);
    }
    /// <summary>Add a node from the palette (auto-positioned for click-to-add).</summary>
    [RelayCommand]
    private void AddNode(PaletteItem item)
        => AddNodeAt(item, new Point(60 + (_nodeSpawnIndex % 6) * 60, 60 + (_nodeSpawnIndex % 6) * 40));
    /// <summary>Create a node from a palette item at a specific graph-space location (drag &amp; drop).</summary>
    public void AddNodeAt(PaletteItem item, Point graphLocation)
    {
        // Mutating the graph while a node is starting/running would remove it from the manager's
        // lifetime, so Stop could never release its resources. Palette click / drag-drop must obey
        // the same gate as delete/connect/clear.
        if (!CanEditGraphNow()) return;
        var historySnapshot = CaptureHistorySnapshot();
        // Create a contract-layer instance; NodeModel and EditorGraph share the same BaseNode instance.
        var editorNode = _workflowHost.CreateNode(item.Name, graphLocation.X, graphLocation.Y);
        var instance = editorNode.RuntimeNode;
        var node = new NodeViewModel(new NodeModel(instance)) { Location = graphLocation, Runtime = editorNode };
        node.Id = node.Runtime.Id;
        node.RefreshPinTypes();
        _nodeSpawnIndex++;
        HookSelection(node);
        Nodes.Add(node);
        _logger.LogTrace("Added node {Name} #{InstanceId} at ({X:0.##},{Y:0.##})", node.Name, node.Id, graphLocation.X, graphLocation.Y);
        RecordHistory(historySnapshot);
    }
    /// <summary>Copy all selected nodes (and wires whose endpoints are both selected) to an in-app clipboard.</summary>
    public bool CopySelection()
    {
        var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
        if (selectedNodes.Count == 0)
            return false;

        _nodeClipboard.Clear();
        _connectionClipboard.Clear();
        _copyOrigin = new Point(
            selectedNodes.Min(n => n.Location.X),
            selectedNodes.Min(n => n.Location.Y));
        _pasteCount = 0;

        foreach (var node in selectedNodes)
        {
            var parameters = node.Parameters.ToDictionary(p => p.Key, p => p.ToValue());
            _nodeClipboard.Add(new CopiedNodeSnapshot(
                node.Id, node.Name, node.DisplayName, node.Location, node.Priority, parameters));
        }

        var selectedIds = selectedNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var connection in Connections)
        {
            if (selectedIds.Contains(connection.Source.Node.Id)
                && selectedIds.Contains(connection.Target.Node.Id))
            {
                _connectionClipboard.Add(new CopiedConnectionSnapshot(
                    connection.Source.Node.Id,
                    connection.Source.Name,
                    connection.Target.Node.Id,
                    connection.Target.Name));
            }
        }

        _logger.LogTrace("Copied {NodeCount} node(s) and {ConnectionCount} connection(s).",
            _nodeClipboard.Count, _connectionClipboard.Count);
        return true;
    }

    public bool HasCopiedNodes => _nodeClipboard.Count > 0;

    /// <summary>Paste the copied nodes and their internal wires as one undoable edit.</summary>
    public bool PasteSelection(Point? graphLocation = null)
    {
        if (!CanEditGraphNow() || _nodeClipboard.Count == 0)
            return false;

        var historySnapshot = CaptureHistorySnapshot();
        var offset = graphLocation.HasValue ? 0 : 24 * (_pasteCount + 1);
        var origin = graphLocation ?? new Point(_copyOrigin.X + offset, _copyOrigin.Y + offset);
        _pasteCount++;

        var pastedNodes = new List<NodeViewModel>();
        var idMap = new Dictionary<string, NodeViewModel>(StringComparer.Ordinal);
        foreach (var copied in _nodeClipboard)
        {
            var location = new Point(
                origin.X + (copied.Location.X - _copyOrigin.X),
                origin.Y + (copied.Location.Y - _copyOrigin.Y));
            var editorNode = _workflowHost.CreateNode(
                copied.TypeName,
                location.X,
                location.Y,
                copied.Parameters,
                displayName: copied.DisplayName,
                priority: copied.Priority);
            var node = new NodeViewModel(new NodeModel(editorNode.RuntimeNode))
            {
                DisplayName = copied.DisplayName,
                Priority = copied.Priority,
                Location = location,
                Runtime = editorNode
            };
            node.Id = node.Runtime.Id;
            node.ApplyParameterValues(copied.Parameters);
            node.RefreshPinTypes();
            HookSelection(node);
            Nodes.Add(node);
            pastedNodes.Add(node);
            idMap[copied.OriginalId] = node;
        }

        foreach (var copied in _connectionClipboard)
        {
            if (!idMap.TryGetValue(copied.FromNodeId, out var fromNode)
                || !idMap.TryGetValue(copied.ToNodeId, out var toNode))
                continue;
            var source = fromNode.Outputs.FirstOrDefault(p => p.Name == copied.FromPin);
            var target = toNode.Inputs.FirstOrDefault(p => p.Name == copied.ToPin);
            if (source is null || target is null)
            {
                _logger.LogWarning("Copied wire {FromPin} -> {ToPin} could not be restored.", copied.FromPin, copied.ToPin);
                continue;
            }
            Establish(source, target);
        }

        SelectNodes(pastedNodes);
        RecordHistory(historySnapshot);
        _logger.LogTrace("Pasted {NodeCount} node(s).", pastedNodes.Count);
        return pastedNodes.Count > 0;
    }

    public void ToggleSelection(NodeViewModel node)
    {
        node.IsSelected = !node.IsSelected;
        if (!node.IsSelected)
            SelectedNode = Nodes.LastOrDefault(n => n.IsSelected);
    }

    /// <summary>Raise a node to the top of the visual stacking order.</summary>
    public void BringToFront(NodeViewModel node)
        => node.ZIndex = Nodes.Count == 0 ? 0 : Nodes.Max(n => n.ZIndex) + 1;

    /// <summary>Replace the selection, or add to it when <paramref name="additive"/> is true.</summary>
    public void SelectNodes(IEnumerable<NodeViewModel> nodes, bool additive = false)
    {
        var targets = nodes.ToHashSet();
        foreach (var n in Nodes)
            n.IsSelected = targets.Contains(n) || (additive && n.IsSelected);
        SelectedNode = Nodes.LastOrDefault(n => n.IsSelected);
    }

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
            // Persist when the node is dragged to a new location, or when the user edits the
            // display name / priority outside the parameter dialog.
            if (e.PropertyName == nameof(NodeViewModel.Location)
                || e.PropertyName == nameof(NodeViewModel.DisplayName)
                || e.PropertyName == nameof(NodeViewModel.Priority))
            {
                MarkDirty();
                ScheduleAutoSave();
            }
        };
    }
    /// <summary>
    /// Re-apply one node's edited parameters immediately instead of waiting for the next autosave.
    /// If a parameter changed a pin's DataType, every wire touching that node is removed because
    /// the old connection can no longer be trusted; the GUI and Core must both drop it before the
    /// pin labels are refreshed.
    /// </summary>
    private void ApplyNodeParameters(NodeViewModel node)
    {
        if (node.Runtime is null)
            return;

        var runtime = node.Runtime;
        var values = node.Parameters.ToDictionary(p => p.Key, p => p.ToValue());
        var pinsBefore = runtime.RuntimeNode.InputPins
            .Concat(runtime.RuntimeNode.OutputPins)
            .Select(pin => (Pin: pin, Type: pin.DataType))
            .ToList();

        runtime.RuntimeNode.Configure(values);
        // Configure may derive DisplayName from node parameters (for example the FACT
        // Launcher position), so synchronize it before updating the Core graph.
        node.DisplayName = runtime.RuntimeNode.DisplayName ?? node.DisplayName;
        _workflowHost.UpdateNode(
            runtime.Id, node.DisplayName, node.Priority,
            node.Location.X, node.Location.Y, values);

        if (pinsBefore.Any(entry => entry.Pin.DataType != entry.Type))
        {
            var affected = Connections
                .Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node))
                .ToList();
            foreach (var connection in affected)
                Teardown(connection);
        }

        node.RefreshPinTypes();
        RefreshPinConnections();
    }

    /// <summary>After the Core layer connects / disconnects, refresh every pin's IsConnected visual state.</summary>
    private void RefreshPinConnections()
    {
        var snapshot = _workflowHost.GetWorkflowSnapshot();
        foreach (var node in Nodes)
        {
            foreach (var pin in node.Inputs)
            {
                pin.IsConnected = snapshot.Connections.Any(c =>
                    c.ToNodeId == node.Id && c.ToPinName == pin.Name);
            }
            foreach (var pin in node.Outputs)
            {
                pin.IsConnected = snapshot.Connections.Any(c =>
                    c.FromNodeId == node.Id && c.FromPinName == pin.Name);
            }
        }
    }
    /// <summary>Schedule a debounced auto-save; no-op while a document is being loaded/cleared.</summary>
    private void ScheduleAutoSave()
    {
        if (_isLoading || _suppressAutoSave) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }
    /// <summary>Write the current graph to disk (no-op while we are itself loading).</summary>
    private async void AutoSaveNow()
    {
        if (_isLoading || _suppressAutoSave) return;
        if (_autoSaveInFlight)
        {
            _autoSavePending = true;
            return;
        }

        _autoSaveInFlight = true;
        var version = _changeVersion;
        try
        {
            await SaveSnapshotAsync();
            // Auto-save is a real save: clear the dirty flag unless the document changed while the
            // write was in flight (then the pending timer keeps it dirty and saves again).
            if (_changeVersion == version)
                IsDirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-save failed: {Message}", ex.Message);
            StatusText = $"{L["ErrorPrefix"]}: {ex.Message}";
        }
        finally
        {
            _autoSaveInFlight = false;
            if (_autoSavePending)
            {
                _autoSavePending = false;
                AutoSaveNow();
            }
        }
    }
    /// <summary>Serialize and write the current graph to the current file, or the store when none is set.</summary>
    private async Task SaveSnapshotAsync()
    {
        SyncGraphState();
        var json = GraphSerializer.Serialize(_graph);
        if (!string.IsNullOrEmpty(CurrentFilePath))
        {
            var temp = CurrentFilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, CurrentFilePath, overwrite: true);
        }
        else
        {
            await _store.SaveAsync(json);
        }
    }
    /// <summary>Reload the graph from disk if it exists.</summary>
    private async void AutoLoad()
    {
        bool loaded = false;
        // Suppress collection-driven dirty/auto-save for the whole async load, not only after the
        // store returns. Otherwise edits made while a remote store is still loading would be
        // wiped by the final IsDirty = false below.
        _isLoading = true;
        try
        {
            var json = await _store.LoadAsync();
            if (string.IsNullOrEmpty(json)) return;
            var doc = GraphDeserializer.Deserialize(json);
            LoadGraphFromDocument(doc);
            ClearHistory();
            loaded = true;
            StatusText = $"{L["LoadedFrom"]}: {_store.Description}";
        }
        catch (Exception ex)
        {
            Log($"[{L["LoadFailed"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Auto-load failed: {Message}", ex.Message);
        }
        finally
        {
            _isLoading = false;
            if (loaded)
                IsDirty = false;
        }
    }
    /// <summary>Called by the canvas when a connection drag is dropped on an input pin.</summary>
    public bool TryCreateConnection(PinViewModel source, PinViewModel target)
    {
        // Wiring while nodes are starting/running can deliver a sample to the wrong target or drop
        // one in the disconnect window; require a stop first, like structural edits.
        if (!CanEditGraphNow()) return false;

        // Normalize direction: source must be the output, target the input.
        if (source.IsInput && target.IsOutput)
            (source, target) = (target, source);
        if (!source.IsOutput || !target.IsInput)
        {
            Log(L["ErrMustOutToIn"], "WARN");
            _logger.LogWarning("Connection rejected: {Message}", L["ErrMustOutToIn"]);
            return false;
        }
        var error = ValidateConnection(source, target);
        if (error is not null)
        {
            // Invalid wiring is a normal user-operation rejection, not a runtime failure.
            Log($"[{L["ConnRejected"]}] {error}", "WARN");
            _logger.LogWarning("Connection rejected: {Message}", error);
            return false;
        }
        var historySnapshot = CaptureHistorySnapshot();
        var established = Establish(source, target);
        if (established)
            RecordHistory(historySnapshot);
        return established;
    }
    /// <summary>Detach the single wire connected to this input pin (drag input pin onto empty canvas).</summary>
    public void DisconnectInputPin(PinViewModel input)
    {
        if (!CanEditGraphNow()) return;
        var conn = Connections.FirstOrDefault(c => ReferenceEquals(c.Target, input));
        if (conn is null) return;
        var historySnapshot = CaptureHistorySnapshot();
        Teardown(conn);
        RecordHistory(historySnapshot);
    }
    /// <summary>
    /// Establish the connection in the Core layer: the output pin saves a reference to the input pin
    /// and the input pin saves a reference back to the output pin (bidirectional ConnectedPin).
    /// The GUI only adds a visual wire.
    /// </summary>
    private bool Establish(PinViewModel source, PinViewModel target)
    {
        bool connected;
        try
        {
            connected = _workflowHost.Connect(source.Node.Runtime!.Id, source.Name, target.Node.Runtime!.Id, target.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Missing or stale pin metadata is an expected rejection from the editing surface.
            StatusText = L["ErrPinOccupied"];
            Log($"[{L["ConnRejected"]}] {ex.Message}", "WARN");
            _logger.LogWarning(ex, "Core rejected connection {From}.{FromPin} -> {To}.{ToPin}",
                source.Node.Name, source.Name, target.Node.Name, target.Name);
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected Core failures remain errors because they may indicate graph corruption.
            StatusText = L["ErrPinOccupied"];
            Log($"[{L["ConnRejected"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Core rejected connection {From}.{FromPin} -> {To}.{ToPin}",
                source.Node.Name, source.Name, target.Node.Name, target.Name);
            return false;
        }

        if (!connected)
        {
            StatusText = L["ErrPinOccupied"];
            Log($"[{L["ConnRejected"]}] {L["ErrPinOccupied"]}", "WARN");
            _logger.LogWarning("Connection rejected because a pin is already connected: {From}.{FromPin} -> {To}.{ToPin}",
                source.Node.Name, source.Name, target.Node.Name, target.Name);
            return false;
        }
        Connections.Add(new ConnectionViewModel(source, target));
        _logger.LogTrace("Connected {From}.{FromPin} -> {To}.{ToPin}", source.Node.Name, source.Name, target.Node.Name, target.Name);
        return true;
    }
    /// <summary>Remove the connection in the Core layer and delete the visual wire.</summary>
    private void Teardown(ConnectionViewModel connection)
    {
        _workflowHost.DisconnectInput(connection.Target.Node.Id, connection.Target.Name);
        Connections.Remove(connection);
        _logger.LogTrace("Disconnected {From}.{FromPin} -> {To}.{ToPin}", connection.Source.Node.Name, connection.Source.Name, connection.Target.Node.Name, connection.Target.Name);
    }
    private string? ValidateConnection(PinViewModel source, PinViewModel target)
    {
        if (!source.IsOutput || !target.IsInput)
            return L["ErrMustOutToIn"];
        if (ReferenceEquals(source.Node, target.Node))
            return L["ErrSelfConnect"];
        if (target.DataType != source.DataType)
            return $"{L["ErrTypeMismatch"]}: {source.TypeName} -> {target.TypeName}";
        // One-to-one rule: an output pin connects to exactly one input pin.
        if (Connections.Any(c => ReferenceEquals(c.Source, source)))
            return $"{L["ErrPinOccupied"]}: {source.Name}";
        if (Connections.Any(c => ReferenceEquals(c.Target, target)))
            return $"{L["ErrPinOccupied"]}: {target.Name}";
        return null;
    }
    [RelayCommand]
    public void RemoveConnection(ConnectionViewModel connection)
    {
        if (!CanEditGraphNow()) return;
        var historySnapshot = CaptureHistorySnapshot();
        Teardown(connection);
        RecordHistory(historySnapshot);
    }
    [RelayCommand]
    private void RemoveSelectedConnections()
    {
        if (!CanEditGraphNow()) return;
        var selected = Connections.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        var historySnapshot = CaptureHistorySnapshot();
        foreach (var c in selected) Teardown(c);
        RecordHistory(historySnapshot);
    }
    /// <summary>
    /// Guard for structural edits (delete a node, clear the canvas) and for changing wires: mutating
    /// the graph while a node is still starting or running would remove it from the manager, after
    /// which StopAsync can never stop it — the run task and its resources (ports, sockets, files)
    /// would leak. Changing a wire mid-run can also deliver a sample to the wrong target. Reject the
    /// edit with an error instead of leaking / misdelivering silently.
    /// </summary>
    public bool CanEditGraphNow()
    {
        if (!_workflowHost.GetRuntimeSnapshot().Nodes.Any(n => n.Connecting || n.State == NodeState.Running))
            return true;

        var message = L["StopBeforeEdit"];
        StatusText = message;
        // Editing while a workflow is active is rejected as a warning. The runtime itself is healthy;
        // this protects resources and prevents graph mutations during execution.
        Log(message, "WARN");
        _logger.LogWarning("{Message}", message);
        return false;
    }

    /// <summary>Delete key: remove selected wires first, then the selected node.</summary>
    [RelayCommand]
    public void DeleteSelection()
    {
        if (!CanEditGraphNow()) return;
        var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
        var hasSelectedConnections = SelectedConnections.Count > 0;
        if (selectedNodes.Count == 0 && !hasSelectedConnections)
            return;
        var historySnapshot = CaptureHistorySnapshot();
        foreach (var c in SelectedConnections.ToList())
        {
            SelectedConnections.Remove(c);
            Teardown(c);
        }
        // Delete all selected nodes (multi-select supported).
        foreach (var n in selectedNodes)
            RemoveNodeCore(n);
        SelectedNode = null;
        RecordHistory(historySnapshot);
    }
    [RelayCommand]
    public void RemoveNode(NodeViewModel node)
    {
        if (!CanEditGraphNow()) return;
        var historySnapshot = CaptureHistorySnapshot();
        RemoveNodeCore(node);
        RecordHistory(historySnapshot);
    }

    private void RemoveNodeCore(NodeViewModel node)
    {
        // WorkflowHost delegates node and plugin-instance removal to Core.
        if (node.Runtime is not null)
            _workflowHost.RemoveNode(node.Runtime.Id);
        // Remove visual wires.
        var related = Connections
            .Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node))
            .ToList();
        foreach (var c in related) Connections.Remove(c);
        Nodes.Remove(node);
        _logger.LogTrace("Removed node #{InstanceId} ({Name})", node.Id, node.Name);
    }

    // ---------- Node commands (right-click menu) ----------
    /// <summary>Snapshot of the commands the given node exposes to the editor context menu.</summary>
    public IReadOnlyList<NodeCommandInfo> GetNodeCommands(NodeViewModel node)
        => node is null ? Array.Empty<NodeCommandInfo>() : _workflowHost.GetNodeCommands(node.InstanceId);

    /// <summary>
    /// Execute a node command through Core. When the command requests confirmation the
    /// view layer shows a dialog first; execution then uses the same node context as run/stop.
    /// </summary>
    public async Task ExecuteNodeCommandAsync(NodeViewModel node, NodeCommandInfo command)
    {
        if (node is null || command is null) return;
        try
        {
            if (command.RequiresConfirmation)
            {
                var confirmed = await RequestConfirmationAsync(
                    command.Title, $"Run '{command.Title}' on node '{node.Title}'?");
                if (!confirmed) return;
            }

            await _workflowHost.ExecuteNodeCommandAsync(node.InstanceId, command.Id);
            _logger.LogTrace("Node command {CommandId} executed on #{InstanceId}",
                command.Id, node.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node command {CommandId} failed on #{InstanceId}",
                command.Id, node.InstanceId);
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
        }
    }

    /// <summary>
    /// Execute the same command on every selected node that exposes it. Confirmation is requested
    /// once for the whole selection instead of once per node.
    /// </summary>
    public async Task ExecuteNodeCommandForSelectionAsync(NodeViewModel contextNode, NodeCommandInfo command)
    {
        if (contextNode is null || command is null) return;

        var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
        if (selectedNodes.Count == 0)
            selectedNodes.Add(contextNode);

        var targets = selectedNodes
            .Where(n => GetNodeCommands(n).Any(c => c.Id == command.Id && c.IsEnabled))
            .ToList();
        if (targets.Count == 0)
            return;

        if (command.RequiresConfirmation)
        {
            var message = targets.Count == 1
                ? $"Run '{command.Title}' on node '{targets[0].Title}'?"
                : $"Run '{command.Title}' on {targets.Count} selected nodes?";
            var confirmed = await RequestConfirmationAsync(command.Title, message);
            if (!confirmed)
                return;
        }

        foreach (var target in targets)
        {
            try
            {
                await _workflowHost.ExecuteNodeCommandAsync(target.InstanceId, command.Id);
                _logger.LogTrace("Node command {CommandId} executed on #{InstanceId}",
                    command.Id, target.InstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Node command {CommandId} failed on #{InstanceId}",
                    command.Id, target.InstanceId);
                Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            }
        }
    }
    /// <summary>Raised when a generic confirmation dialog should be shown by the view layer.</summary>
    public event EventHandler<ConfirmDialogViewModel>? ConfirmRequested;

    private Task<bool> RequestConfirmationAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new ConfirmDialogViewModel(title, message, "Confirm", "Cancel");
        dialog.RequestClose += (_, confirmed) => tcs.TrySetResult(confirmed);
        if (ConfirmRequested is null)
            tcs.TrySetResult(true); // Headless fallback: no view attached.
        else
            ConfirmRequested.Invoke(this, dialog);
        return tcs.Task;
    }
    /// <summary>Whether the current user is logged in as Admin (Admin-only features gate on this).</summary>
    [ObservableProperty]
    private bool _isAdmin = true; // Start with Admin permissions until the user explicitly signs in as another role.
    /// <summary>Bottom-right lock icon: unlocked (🔓) for Admin, locked (🔒) for all other accounts.</summary>
    public string LockIcon => IsAdmin ? "🔓" : "🔒";
    partial void OnIsAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(LockIcon));
        _logger.LogTrace("Login state changed: {Role}", value ? "Admin" : "User");
    }
    /// <summary>Whether the right node-library Sidebar is visible (Admin only).</summary>
    [ObservableProperty]
    private bool _isSidebarVisible = true; // Admin-only panel is available immediately with the default Admin session.
    /// <summary>Toggle the right node-library Sidebar (Admin only).</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;
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
    public event EventHandler<NodeParamDlgViewModel>? DialogRequested;
    /// <summary>Double-click a node: create the parameter dialog ViewModel and ask the View layer to show it.</summary>
    [RelayCommand]
    private void OpenNodeParameters(NodeViewModel? node)
    {
        if (node is null) return;
        if (!CanEditGraphNow()) return;
        var dialog = new NodeParamDlgViewModel(node);
        var historySnapshot = CaptureHistorySnapshot();
        // Parameter / display-name edits must mark the document dirty and persist through the same
        // debounced auto-save path as structural changes.
        dialog.NodeChanged += (_, _) =>
        {
            ApplyNodeParameters(node);
            MarkDirty();
            ScheduleAutoSave();
            RecordHistory(historySnapshot);
            historySnapshot = CaptureHistorySnapshot();
        };
        DialogRequested?.Invoke(this, dialog);
    }
    [RelayCommand]
    private void ToggleLogPanel() => IsLogPanelOpen = !IsLogPanelOpen;

    /// <summary>Helper: append a structured log entry to the UI panel (thread-safe).</summary>
    private void Log(string message, string level = "INFO", string source = "Main")
        => UiLogStore.Add(new UiLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Source = source,
            Message = UiLogEntry.Flatten(message),
            RawMessage = message,
        });

    /// <summary>Release GUI subscriptions once the window is actually closing.</summary>
    public void DisposeBroadcastDemo() => _broadcastDemo.Dispose();

    [RelayCommand]
    private void ClearLog() => UiLogStore.Clear();
    /// <summary>Raised when the clear-canvas confirmation dialog should be shown (View layer listens to open a window).</summary>
    public event EventHandler<ConfirmDialogViewModel>? ClearRequested;
    /// <summary>Clear the whole canvas: show a confirmation first; only on confirm delete all nodes and wires and clear the Core layer (including the PluginLoader instance container).</summary>
    [RelayCommand]
    private void ClearGraph()
    {
        if (!CanEditGraphNow()) return;
        var dialog = new ConfirmDialogViewModel(
            "Clear Canvas",
            "Clear the entire canvas and all underlying data?",
            "Confirm", "Cancel");
        dialog.RequestClose += (_, confirmed) =>
        {
            if (!confirmed) return;
            if (!CanEditGraphNow()) return;
            // Suppress the collection-change auto-save while the visual and Core graphs are
            // temporarily out of sync, then persist the actually-cleared graph once.
            _autoSaveTimer.Stop();
            var historySnapshot = CaptureHistorySnapshot();
            _suppressAutoSave = true;
            try
            {
                Nodes.Clear();
                Connections.Clear();
                SelectedNode = null;
                _workflowHost.Clear();
            }
            finally
            {
                _suppressAutoSave = false;
            }
            MarkDirty();
            RecordHistory(historySnapshot);
            AutoSaveNow();
            Log("Canvas cleared.");
            _logger.LogTrace("Canvas cleared.");
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
                // The display name is user-editable; pass it (not the type id) so saving/autosave
                // never overwrites the canvas title with e.g. "node.llm.react".
                _workflowHost.UpdateNode(n.Runtime.Id, n.DisplayName, n.Priority, n.Location.X, n.Location.Y, values);
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
        _autoSaveTimer.Stop();
        var wasLoading = _isLoading;
        _isLoading = true;
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Nodes.Clear();
            Connections.Clear();
            _workflowHost.Clear();
            foreach (var spec in doc.Nodes)
            {
                if (string.IsNullOrEmpty(spec.Id) || !nodeIds.Add(spec.Id))
                    throw new InvalidDataException($"Duplicate or empty node id: '{spec.Id}'.");
                // Create a contract-layer instance; NodeModel and EditorGraph share the same instance.
                // name (type id) now lives inside the logic blob (no longer duplicated on the outer node object).
                var name = JsonValueReader.GetString(spec.Logic, "name");
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException($"Node '{spec.Id}' is missing name in logic.");
                var displayName = JsonValueReader.GetString(spec.Logic, "displayName") ?? "";
                var instanceId = JsonValueReader.GetInt(spec.Logic, "instanceId");
                var dependsOn = JsonValueReader.GetInt(spec.Logic, "dependsOn");
                // PluginLoader now owns restore: create node/pin instances, reserve the saved
                // instance id, set dependsOn and deserialize the logic blob.
                var instance = _pluginLoader.CreateNodeInstance(name, instanceId, dependsOn, spec.Logic);
                var node = new NodeViewModel(new NodeModel(instance))
                {
                    DisplayName = displayName,
                    Priority = spec.Priority,
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
                // The Core layer builds a runtime node from the same instance. Pin ids are assigned
                // after the whole document has been read (see EnsurePinIds below), so a later node's
                // restored id can never collide with an id allocated for an earlier node.
                node.Runtime = _graph.AddNode(instance, spec.X, spec.Y,
                    node.Parameters.ToDictionary(p => p.Key, p => p.ToValue()),
                    spec.Id, displayName, spec.Priority, assignPinIds: false);
                node.Id = node.Runtime.Id;
                node.RefreshPinTypes();
                HookSelection(node);
                Nodes.Add(node);
            }
            // Every node restored its saved pin ids first; now hand out ids for the remaining pins.
            _graph.EnsurePinIds();
            // Restore connections by pin instance id. Core locates the runtime pins; the UI only
            // maps them back to the visual PinViewModels so a wire can be drawn.
            foreach (var spec in doc.Connections)
            {
                var from = _graph.FindPinById(spec.FromPinId);
                var to = _graph.FindPinById(spec.ToPinId);
                if (from is null || to is null)
                    throw new InvalidDataException(
                        $"Connection {spec.FromPinId} -> {spec.ToPinId} references a pin that does not exist.");
                if (!from.Value.IsOutput || to.Value.IsOutput)
                    throw new InvalidDataException(
                        $"Connection {spec.FromPinId} -> {spec.ToPinId} is not output -> input.");
                var sourceNode = Nodes.FirstOrDefault(n => n.Id == from.Value.Node.Id);
                var targetNode = Nodes.FirstOrDefault(n => n.Id == to.Value.Node.Id);
                if (sourceNode is null || targetNode is null)
                    throw new InvalidDataException(
                        $"Connection {spec.FromPinId} -> {spec.ToPinId} references a missing node.");
                var source = sourceNode.Outputs.FirstOrDefault(p => p.Name == from.Value.PinName);
                var target = targetNode.Inputs.FirstOrDefault(p => p.Name == to.Value.PinName);
                if (source is null || target is null)
                    throw new InvalidDataException(
                        $"Connection {spec.FromPinId} -> {spec.ToPinId} references an unknown pin name.");
                if (!_graph.TryConnectByPinId(spec.FromPinId, spec.ToPinId))
                    throw new InvalidDataException(
                        $"Connection {spec.FromPinId} -> {spec.ToPinId} could not be restored (pin already connected?).");
                Connections.Add(new ConnectionViewModel(source, target));
            }
        }
        catch
        {
            // A node / parameter / pin failed halfway through the document: roll the canvas back to
            // an empty state instead of leaving a half-built graph that can neither run nor be
            // repaired from the UI. The caller logs the original exception.
            _workflowHost.Clear();
            Nodes.Clear();
            Connections.Clear();
            throw;
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }
    // ---------- Run / save / load ----------
    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy || _shuttingDown) return;
        // Validate BEFORE starting, so a validation failure doesn't flash the indicator. ToGraph
        // runs user/plugin Configure code and can throw; keep it inside a catch so the async
        // command never leaves an unobserved exception on the UI dispatcher.
        WorkflowGraph graph;
        IReadOnlyList<string> validationErrors;
        try
        {
            graph = ToGraph();
            validationErrors = WorkflowValidation.Validate(_registry, graph);
        }
        catch (Exception ex)
        {
            StatusText = L["ValidationFailed"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Preparing the workflow failed: {Message}", ex.Message);
            return;
        }
        if (validationErrors.Count > 0)
        {
            StatusText = L["ValidationFailed"];
            foreach (var err in validationErrors)
                Log($"[{L["ErrorPrefix"]}] {err}", "ERROR");
            return;
        }
        if (_shuttingDown) return;
        // Do NOT light the "running" indicator here: it is derived from the nodes' real state further
        // down (and every 300 ms afterwards), so it turns green only once every node is started.
        _runFailureNotified = false;
        StatusText = L["RunStateStarting"];
        _logger.LogTrace("Run started ({NodeCount} nodes).", Nodes.Count);
        _runCts = new CancellationTokenSource();
        // Yield to the UI thread so the Stop icon renders before the run starts.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => { }, Avalonia.Threading.DispatcherPriority.Render);
        if (_shuttingDown)
        {
            _runCts?.Dispose();
            _runCts = null;
            return;
        }
        _runInProgress = true;
        try
        {
            // WorkflowHost owns the shared Core start sequence: stop any previous session,
            // pre-create pin contexts, and launch every node concurrently.
            await Task.Run(() => _workflowHost.StartAsync(_runCts.Token));
            RefreshRunState();
            StatusText = IsRunning ? L["Running"]
                : IsStarting ? L["RunStateStarting"]
                : IsRunFailed ? L["RunFailed"]
                : L["Ready"];
            _logger.LogTrace("Run launched ({State}).", IsRunning ? "running" : "starting");
        }
        catch (OperationCanceledException)
        {
            StatusText = L["RunCancelled"];
            _logger.LogWarning("Run cancelled.");
        }
        catch (Exception ex)
        {
            StatusText = L["RunFailed"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Run failed: {Message}", ex.Message);
        }
        finally
        {
            RefreshRunState();
            _runInProgress = false;
            // NodeInstanceManager owns the linked token source it created from this parent. Do not
            // dispose the parent while background runs may still be alive; Stop/auto-stop cancels
            // the manager's linked source instead. Dropping the reference is enough.
            _runCts = null;
        }
    }
    [RelayCommand]
    private async Task Stop()
    {
        _logger.LogTrace("Stop requested.");
        _runCts?.Cancel();
        try
        {
            await Task.Run(() => _workflowHost.StopAsync());
        }
        catch (Exception ex)
        {
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
        }
        finally
        {
            _runFailureNotified = false;
            RefreshRunState();
            StatusText = L["Ready"];
        }
    }

    /// <summary>Stop an active workflow from the window-close path without throwing.</summary>
    public async Task StopForShutdownAsync()
    {
        _shuttingDown = true;
        _autoSaveTimer.Stop();
        // The UI flags are refreshed every 300 ms, so a close issued immediately after Start could
        // otherwise see CanStop == false while nodes are already initializing/launching. Failed
        // nodes also keep a non-idle state until Core finishes its automatic stop, so shutdown must
        // still join that cleanup path even though the Stop button is intentionally disabled.
        var hasActiveOrFailedNodes = _workflowHost.GetRuntimeSnapshot().Nodes.Any(n => n.State != NodeState.Idle);
        if (!CanStop && !_runInProgress && !hasActiveOrFailedNodes) return;
        await Stop();
    }

    /// <summary>
    /// Invoke every node's final exit callback after Stop and any pending save have succeeded.
    /// Failures are logged but do not prevent the application from closing.
    /// </summary>
    public async Task ShutdownForExitAsync()
    {
        try
        {
            await Task.Run(() => _workflowHost.ShutdownAsync());
        }
        catch (Exception ex)
        {
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Application shutdown callback failed.");
        }
    }
    /// <summary>Allow the editor to be used again when the close sequence is aborted (save failed).</summary>
    public void CancelShutdown() => _shuttingDown = false;

    /// <summary>
    /// Re-derive the run indicator from the nodes' real runtime state, and mirror every node's state
    /// onto its view-model first so the canvas header indicators and the status bar can never
    /// disagree. "Running" means every canvas node was started successfully and still reports
    /// Running = true; a started node that drops its Running flag is treated as failed. While any
    /// node is still connecting the indicator shows "starting" (this holds for every node type, not
    /// just socket clients), and a failed node shows "failed".
    /// </summary>
    private void RefreshRunState()
    {
        foreach (var n in Nodes) n.NotifyRunningChanged();

        var runtime = _workflowHost.GetRuntimeSnapshot();
        bool anyFailed = runtime.IsFailed;
        bool allRunning = runtime.IsRunning;
        bool starting = runtime.IsStarting;
        bool failed = !allRunning && anyFailed;

        IsRunning = allRunning;
        IsStarting = starting;
        IsRunFailed = failed;

        if (allRunning)
        {
            RunningText = L["RunStateRunning"];
            RunningIconBrush = RunningIndicatorBrush;
        }
        else if (failed)
        {
            RunningText = L["RunStateFailed"];
            RunningIconBrush = FailedIndicatorBrush;
            Dot1Opacity = Dot2Opacity = Dot3Opacity = 0;
            PulseBlur = 0;
            // Core stops the complete workflow after any node failure; report it once.
            if (!_runFailureNotified)
            {
                _runFailureNotified = true;
                StatusText = L["RunFailed"];
                _logger.LogWarning("Run failed: a node failed and the workflow was stopped automatically.");
            }
        }
        else if (starting)
        {
            RunningText = L["RunStateStarting"];
            RunningIconBrush = StartingIndicatorBrush;
        }
        else
        {
            RunningText = L["RunStateReady"];
            RunningIconBrush = IdleIndicatorBrush;
            Dot1Opacity = Dot2Opacity = Dot3Opacity = 0;
            PulseBlur = 0;
        }
    }
    [RelayCommand]
    private async Task Save() => await SaveCurrentAsync();

    /// <summary>
    /// Save to the currently opened file, or to the configured store when the document has never
    /// been saved to a path. Returns false on failure; callers (Save command, close prompt) keep the
    /// dirty flag set so the user can retry instead of silently losing edits.
    /// </summary>
    public async Task<bool> SaveCurrentAsync()
    {
        if (!CanEditGraphNow()) return false;
        _autoSaveTimer.Stop();
        var version = _changeVersion;
        try
        {
            await SaveSnapshotAsync();
            if (_changeVersion != version)
            {
                // A change landed while the write was in flight; the snapshot on disk is already
                // stale, so keep the dirty flag and let the user/auto-save write again.
                return false;
            }
            IsDirty = false;
            var target = string.IsNullOrEmpty(CurrentFilePath) ? _store.Description : CurrentFilePath;
            StatusText = $"{L["SavedTo"]}: {target}";
            _logger.LogTrace("Saved graph to {Target}", target);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"{L["ErrorPrefix"]}: {ex.Message}";
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Saving workflow failed.");
            return false;
        }
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
    public bool SaveToPath(string path)
    {
        if (!CanEditGraphNow()) return false;
        _autoSaveTimer.Stop();
        try
        {
            SyncGraphState();
            GraphSerializer.Save(_graph, path);
            CurrentFilePath = path;
            IsDirty = false;
            StatusText = $"{L["SavedTo"]}: {path}";
            _logger.LogTrace("Saved graph to {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"{L["ErrorPrefix"]}: {ex.Message}";
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Saving workflow to {Path} failed.", path);
            return false;
        }
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
        if (!CanEditGraphNow()) return;
        var previousPath = CurrentFilePath;
        var previousDirty = IsDirty;
        string? backup = null;
        try
        {
            SyncGraphState();
            backup = GraphSerializer.Serialize(_graph);
            var doc = GraphDeserializer.Load(path);
            LoadGraphFromDocument(doc);
            ClearHistory();
            CurrentFilePath = path;
            IsDirty = false;
            StatusText = $"{L["LoadedFrom"]}: {path}";
            _logger.LogTrace("Loaded graph from {Path} ({NodeCount} nodes)", path, Nodes.Count);
        }
        catch (Exception ex)
        {
            // Loading is destructive (LoadGraphFromDocument clears before parsing). Restore the
            // in-memory document so a malformed/corrupt file cannot silently erase the open graph.
            if (backup is not null)
            {
                try
                {
                    LoadGraphFromDocument(GraphDeserializer.Deserialize(backup));
                    CurrentFilePath = previousPath;
                    IsDirty = previousDirty;
                    _logger.LogWarning("Restored the previous graph after a failed load: {Message}", ex.Message);
                }
                catch (Exception restoreEx)
                {
                    // The backup itself is not loadable (very unlikely): at least do not leave the
                    // previous file association pointing at a now-empty canvas that Save could overwrite.
                    CurrentFilePath = string.Empty;
                    _logger.LogError(restoreEx, "Restoring the previous graph failed.");
                }
            }
            StatusText = L["LoadFailed"];
            Log($"[{L["ErrorPrefix"]}] {ex.Message}", "ERROR");
            _logger.LogError(ex, "Loading workflow from {Path} failed.", path);
        }
    }
    [RelayCommand]
    private void Load()
    {
        if (!CanEditGraphNow()) return;
        var path = Path.Combine(AppContext.BaseDirectory, "workflow.json");
        if (!File.Exists(path))
        {
            StatusText = L["NoWorkflowFile"];
            return;
        }
        LoadFromPath(path);
    }
}
