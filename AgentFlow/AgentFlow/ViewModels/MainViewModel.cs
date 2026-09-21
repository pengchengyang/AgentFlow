using System.Collections.ObjectModel;
using AgentFlow.Contracts;
using AgentFlow.Core;
using AgentFlow.Services;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nodify.Avalonia.Connections;

namespace AgentFlow.ViewModels;

/// <summary>A node entry in the component palette.</summary>
public sealed record PaletteItem(string TypeId, string DisplayName, string Category, string PinSummary, SolidColorBrush Accent);

public partial class MainViewModel : ViewModelBase
{
    private readonly NodeRegistry _registry = new();
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
    /// <summary>The pending (drag-in-progress) connection; lets us redirect its start anchor when dragging from a connected input.</summary>
    public PendingConnection PendingConnection { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

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

        LoadPlugins();

        // Auto-persist graph on every structural change (add/remove node, wire, or drag move).
        Nodes.CollectionChanged += (_, _) => AutoSave();
        Connections.CollectionChanged += (_, _) => AutoSave();

        // Restore the previous graph on startup.
        AutoLoad();

        // Show messages that nodes publish to the external GUI in the log (GuiBridge demo).
        _guiBridge.Subscribe("result", msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Logs.Add($"[GuiBridge] topic={msg.Topic} payload={msg.Payload}")));
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
        new PluginLoader(_loggerFactory.CreateLogger(nameof(PluginLoader)))
            .LoadFromDirectory(pluginDir, _registry);

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
        var node = new NodeViewModel(_registry.Get(item.TypeId)) { Location = graphLocation };
        _nodeSpawnIndex++;
        HookSelection(node);
        Nodes.Add(node);
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

    /// <summary>Callback when a Nodify connection drag starts.</summary>
    [RelayCommand]
    private void ConnectionStarted(object? parameter)
    {
        // 从已连接的输入脚拖起时，把预览线起点改到上游输出脚，让线从 out 画向鼠标。
        var pin = ExtractConnectors(parameter).source as PinViewModel;
        if (pin is { IsInput: true, Source: { } upstream })
            PendingConnection.Source = upstream;
    }

    /// <summary>Callback when a Nodify connection drag completes.</summary>
    [RelayCommand]
    private void ConnectionCompleted(object? parameter)
    {
        var (source, target) = ExtractConnectors(parameter);

        // 从已连接的输入脚拖到画布空白：断开该连接
        if (source is { Direction: PinDirection.Input } && target is null)
        {
            DisconnectInput(source);
            return;
        }

        if (source is null || target is null) return;

        // Normalize direction: source must be the output, target the input.
        if (source.Direction == PinDirection.Input && target.Direction == PinDirection.Output)
            (source, target) = (target, source);

        var error = ValidateConnection(source, target);
        if (error is not null)
        {
            Logs.Add($"[{L["ConnRejected"]}] {error}");
            return;
        }

        Establish(source, target);
    }

    /// <summary>Detach the single wire connected to this input pin (drag input pin onto empty canvas).</summary>
    private void DisconnectInput(PinViewModel input)
    {
        var conn = Connections.FirstOrDefault(c => ReferenceEquals(c.Target, input));
        if (conn is not null) Teardown(conn);
    }

    /// <summary>
    /// Create a wire both visually and in the pin model: the output pin stores a reference
    /// to the input pin so it can Send data "over the wire".
    /// </summary>
    private void Establish(PinViewModel source, PinViewModel target)
    {
        source.ConnectTo(target);
        Connections.Add(new ConnectionViewModel(source, target));
    }

    /// <summary>Remove one wire and detach the pin references on both ends.</summary>
    private void Teardown(ConnectionViewModel connection)
    {
        connection.Source.DisconnectTarget(connection.Target);
        Connections.Remove(connection);
    }

    private string? ValidateConnection(PinViewModel source, PinViewModel target)
    {
        if (source.Direction != PinDirection.Output || target.Direction != PinDirection.Input)
            return L["ErrMustOutToIn"];
        if (ReferenceEquals(source.Node, target.Node))
            return L["ErrSelfConnect"];
        if (!target.Definition.DataType.IsAssignableFrom(source.Definition.DataType))
            return $"{L["ErrTypeMismatch"]}: {source.TypeName} -> {target.TypeName}";
        if (Connections.Any(c => ReferenceEquals(c.Target, target)))
            return $"{L["ErrPinOccupied"]}: {target.Name}";
        return null;
    }

    private static (PinViewModel? source, PinViewModel? target) ExtractConnectors(object? parameter)
    {
        if (parameter is null) return (null, null);
        var t = parameter.GetType();

        // Nodify passes a ValueTuple<object, object> (Item1 = source, Item2 = target), whose
        // Item1/Item2 are FIELDS, while other producers may use properties. Read both so the
        // source/target pins are never lost.
        object? Read(string name)
        {
            var prop = t.GetProperty(name);
            if (prop is not null) return prop.GetValue(parameter);
            return t.GetField(name)?.GetValue(parameter);
        }

        var sp = Read("SourceConnector") ?? Read("Item1");
        var tp = Read("TargetConnector") ?? Read("Item2");
        return (sp as PinViewModel, tp as PinViewModel);
    }

    [RelayCommand]
    private void RemoveConnection(ConnectionViewModel connection) => Teardown(connection);

    [RelayCommand]
    private void RemoveSelectedConnections()
    {
        var selected = Connections.Where(c => c.IsSelected).ToList();
        foreach (var c in selected) Teardown(c);
    }

    /// <summary>Delete key: remove selected wires first, then the selected node.</summary>
    [RelayCommand]
    private void DeleteSelection()
    {
        foreach (var c in SelectedConnections.ToList())
        {
            SelectedConnections.Remove(c);
            Teardown(c);
        }
        if (SelectedNode is not null)
            RemoveNode(SelectedNode);
    }

    [RelayCommand]
    private void RemoveNode(NodeViewModel node)
    {
        // Detach every wire touching this node so the opposite pins drop their references.
        foreach (var pin in node.Inputs.Concat(node.Outputs))
            pin.DetachAll();

        var related = Connections
            .Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node))
            .ToList();
        foreach (var c in related) Connections.Remove(c);
        Nodes.Remove(node);
    }

    [RelayCommand]
    private void ToggleLogPanel() => IsLogPanelOpen = !IsLogPanelOpen;

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    // ---------- Graph <-> ViewModel mapping ----------

    public WorkflowGraph ToGraph()
    {
        var graph = new WorkflowGraph();
        foreach (var n in Nodes)
        {
            graph.Nodes.Add(new NodeSpec
            {
                Id = n.Id,
                TypeId = n.TypeId,
                X = n.Location.X,
                Y = n.Location.Y,
                Name = n.Name,
                Priority = n.Priority,
                Parameters = n.Parameters.ToDictionary(p => p.Key, p => p.ToValue())
            });
        }
        foreach (var c in Connections)
        {
            graph.Connections.Add(new ConnectionSpec
            {
                FromNode = c.Source.Node.Id,
                FromPin = c.Source.Name,
                ToNode = c.Target.Node.Id,
                ToPin = c.Target.Name
            });
        }
        return graph;
    }

    public void LoadGraph(WorkflowGraph graph)
    {
        Nodes.Clear();
        Connections.Clear();

        var map = new Dictionary<string, NodeViewModel>();
        foreach (var spec in graph.Nodes)
        {
            var node = new NodeViewModel(_registry.Get(spec.TypeId))
            {
                Id = spec.Id,
                Name = spec.Name ?? "",
                Priority = spec.Priority,
                Location = new Point(spec.X, spec.Y)
            };
            node.ApplyParameterValues(spec.Parameters);
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
