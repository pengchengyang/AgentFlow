// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="RunManagerView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentFlow.Services;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class RunManagerView : UserControl
{
    private DateTimeOffset _lastEmptyPress;
    private Point _lastEmptyPos;
    private bool _hasLastEmptyPress;
    private DateTimeOffset _lastRowPress;
    private Point _lastRowPos;
    private RunBatchWorkflowItemViewModel? _lastRowItem;
    private bool _hasLastRowPress;

    private readonly DispatcherTimer _runIconTimer;
    private readonly HashSet<RunBatchWorkflowItemViewModel> _observedWorkflowItems = new();
    private RunManagerViewModel? _runManager;
    private double _runIconAngle;

    public RunManagerView()
    {
        InitializeComponent();

        _runIconTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _runIconTimer.Tick += (_, _) =>
        {
            _runIconAngle = (_runIconAngle + 6.0) % 360.0;
            ApplyRunIconAngle(_runIconAngle);
        };

        DataContextChanged += (_, _) => BindRunManager();
        AttachedToVisualTree += (_, _) => BindRunManager();
        DetachedFromVisualTree += (_, _) =>
        {
            _runIconTimer.Stop();
            UnbindRunManager();
        };

        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void BindRunManager()
    {
        if (_runManager is not null)
            UnbindRunManager();

        _runManager = DataContext as RunManagerViewModel;
        if (_runManager is null)
            return;

        _runManager.WorkflowItems.CollectionChanged += OnWorkflowItemsChanged;
        foreach (var item in _runManager.WorkflowItems)
            ObserveWorkflowItem(item);

        UpdateRunIconAnimation();
    }

    private void UnbindRunManager()
    {
        if (_runManager is null)
            return;

        _runManager.WorkflowItems.CollectionChanged -= OnWorkflowItemsChanged;
        foreach (var item in _observedWorkflowItems)
            item.PropertyChanged -= OnWorkflowItemPropertyChanged;
        _observedWorkflowItems.Clear();
        _runManager = null;
    }

    private void OnWorkflowItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var value in e.OldItems)
            {
                if (value is RunBatchWorkflowItemViewModel item)
                    UnobserveWorkflowItem(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var value in e.NewItems)
            {
                if (value is RunBatchWorkflowItemViewModel item)
                    ObserveWorkflowItem(item);
            }
        }

        UpdateRunIconAnimation();
    }

    private void ObserveWorkflowItem(RunBatchWorkflowItemViewModel item)
    {
        if (_observedWorkflowItems.Add(item))
            item.PropertyChanged += OnWorkflowItemPropertyChanged;
    }

    private void UnobserveWorkflowItem(RunBatchWorkflowItemViewModel item)
    {
        if (!_observedWorkflowItems.Remove(item))
            return;

        item.PropertyChanged -= OnWorkflowItemPropertyChanged;
    }

    private void OnWorkflowItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunBatchWorkflowItemViewModel.IsRunning))
            UpdateRunIconAnimation();
    }

    private void UpdateRunIconAnimation()
    {
        var hasRunningRow = _runManager?.WorkflowItems.Any(item => item.IsRunning) == true;
        if (hasRunningRow)
        {
            if (!_runIconTimer.IsEnabled)
                _runIconTimer.Start();
            ApplyRunIconAngle(_runIconAngle);
            return;
        }

        _runIconTimer.Stop();
        _runIconAngle = 0;
        ApplyRunIconAngle(0);
    }

    private void ApplyRunIconAngle(double angle)
    {
        foreach (var icon in WorkflowListBox
                     .GetVisualDescendants()
                     .OfType<Avalonia.Controls.Shapes.Path>()
                     .Where(path => path.Classes.Contains("batchRunningIcon")))
        {
            icon.RenderTransform = new RotateTransform(angle);
        }
    }

    private async void OpenRowFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RunBatchWorkflowItemViewModel item }
            || DataContext is not RunManagerViewModel viewModel)
            return;

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
                return;

            IStorageFolder? startDir = null;
            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                var containingFolder = Path.GetDirectoryName(item.FilePath);
                if (!string.IsNullOrWhiteSpace(containingFolder))
                    startDir = await topLevel.StorageProvider.TryGetFolderFromPathAsync(containingFolder);
            }

            startDir ??= await topLevel.StorageProvider.TryGetFolderFromPathAsync(AppContext.BaseDirectory);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.Instance["SelectBatchWorkflow"],
                SuggestedStartLocation = startDir,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("AgentFlow RW Workflow") { Patterns = new[] { "*.rw" } }
                }
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                viewModel.SetWorkflowFile(item, path);
        }
        catch (Exception ex)
        {
            viewModel.ReportFilePickerError(ex.Message);
        }
    }

    private void InstanceCountBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender is not TextBox { DataContext: RunBatchWorkflowItemViewModel item })
            return;

        item.CommitInstanceCountText();
        e.Handled = true;
    }

    private void AddWorkflowRow_Click(object? sender, RoutedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is RunManagerViewModel viewModel
                && viewModel.SelectedWorkflowItem is { } item)
            {
                WorkflowListBox.ScrollIntoView(item);
            }
        });
    }

    /// <summary>
    /// Double-clicking empty Batch Run space uses the same expand/collapse behavior as Logs.
    /// Clicks on buttons, selectors, scrollbars and workflow rows are ignored.
    /// </summary>
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is StyledElement source)
        {
            if (source is Visual visual)
            {
                if (visual is Button or ComboBox or TextBox
                    || visual.FindAncestorOfType<Button>() != null
                    || visual.FindAncestorOfType<ComboBox>() != null
                    || visual.FindAncestorOfType<TextBox>() != null
                    || visual is ScrollBar
                    || visual.FindAncestorOfType<ScrollBar>() != null)
                    return;
            }

            if (source.DataContext is RunBatchWorkflowItemViewModel rowItem
                && DataContext is RunManagerViewModel rowViewModel)
            {
                _hasLastEmptyPress = false;

                // Double-clicking a workflow row jumps the log panel to that row's instances.
                var rowNow = DateTimeOffset.UtcNow;
                var rowPos = e.GetPosition(this);
                var rowDouble = _hasLastRowPress
                    && ReferenceEquals(_lastRowItem, rowItem)
                    && (rowNow - _lastRowPress).TotalMilliseconds < 500
                    && Math.Abs(rowPos.X - _lastRowPos.X) < 10
                    && Math.Abs(rowPos.Y - _lastRowPos.Y) < 10;
                _lastRowPress = rowNow;
                _lastRowPos = rowPos;
                _lastRowItem = rowItem;
                _hasLastRowPress = true;
                if (rowDouble)
                {
                    _hasLastRowPress = false;
                    rowViewModel.RequestLogFilter(rowItem);
                }

                return;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var pos = e.GetPosition(this);
        var isDouble = _hasLastEmptyPress
            && (now - _lastEmptyPress).TotalMilliseconds < 500
            && Math.Abs(pos.X - _lastEmptyPos.X) < 10
            && Math.Abs(pos.Y - _lastEmptyPos.Y) < 10;
        _lastEmptyPress = now;
        _lastEmptyPos = pos;
        _hasLastEmptyPress = true;

        if (isDouble)
        {
            _hasLastEmptyPress = false;
            if (DataContext is RunManagerViewModel viewModel)
                viewModel.ToggleExpanded();
        }
    }
}