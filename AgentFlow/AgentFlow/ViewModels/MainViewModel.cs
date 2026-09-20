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

    /// <summary>Localization (XAML can also use Loc.Instance directly).</summary>
    public Loc L => Loc.Instance;

    private readonly List<PaletteItem> _allPaletteItems = new();
    public ObservableCollection<PaletteItem> PaletteItems { get; } = new();
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    [ObservableProperty]
    private NodeViewModel? _selectedNode;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = Loc.Instance["Ready"];

    [ObservableProperty]
    private bool _isDarkTheme;

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

    /// <summary>Add a node from the palette.</summary>
    [RelayCommand]
    private void AddNode(PaletteItem item)
    {
        var node = new NodeViewModel(_registry.Get(item.TypeId))
        {
            Location = new Point(60 + (_nodeSpawnIndex % 6) * 60, 60 + (_nodeSpawnIndex % 6) * 40)
        };
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
        };
    }

    /// <summary>Callback when a Nodify connection drag completes.</summary>
    [RelayCommand]
    private void ConnectionCompleted(object? parameter)
    {
        var (source, target) = ExtractConnectors(parameter);
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

        Connections.Add(new ConnectionViewModel(source, target));
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
        var sp = t.GetProperty("SourceConnector")?.GetValue(parameter) ?? t.GetProperty("Item1")?.GetValue(parameter);
        var tp = t.GetProperty("TargetConnector")?.GetValue(parameter) ?? t.GetProperty("Item2")?.GetValue(parameter);
        return (sp as PinViewModel, tp as PinViewModel);
    }

    [RelayCommand]
    private void RemoveConnection(ConnectionViewModel connection) => Connections.Remove(connection);

    [RelayCommand]
    private void RemoveNode(NodeViewModel node)
    {
        var related = Connections
            .Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node))
            .ToList();
        foreach (var c in related) Connections.Remove(c);
        Nodes.Remove(node);
    }

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
            Connections.Add(new ConnectionViewModel(source, target));
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
