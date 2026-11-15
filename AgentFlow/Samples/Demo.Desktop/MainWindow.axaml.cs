using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using AgentFlow.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Hosting.Logging;
using Microsoft.Extensions.Logging;

namespace Demo.Desktop;

public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory = AppLog.CreateLoggerFactory();
    private WorkflowHost? _workflowHost;
    private string? _workflowPath;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private async void OpenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
                throw new InvalidOperationException("Storage provider is unavailable.");

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open AgentFlow Workflow",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("AgentFlow Workflow") { Patterns = new[] { "*.rw", "*.json" } }
                }
            });

            if (files.Count == 0)
                return;

            _workflowPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(_workflowPath))
                throw new InvalidOperationException("The selected workflow does not have a local file path.");

            WorkflowPathText.Text = _workflowPath;
            RunButton.IsEnabled = true;
            StatusText.Text = "Workflow selected. Press Run.";
            AppendLog($"Selected workflow: {_workflowPath}");
        }
        catch (Exception exception)
        {
            ShowError("Open workflow failed", exception);
        }
    }

    private async void RunButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isRunning || string.IsNullOrWhiteSpace(_workflowPath))
            return;

        try
        {
            if (!File.Exists(_workflowPath))
                throw new FileNotFoundException("Workflow file was not found.", _workflowPath);

            RunButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            StatusText.Text = "Running...";
            AppendLog("Loading plugins and workflow...");

            var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
            if (!Directory.Exists(pluginDirectory))
                throw new DirectoryNotFoundException($"Plugin directory was not found: {pluginDirectory}");

            _workflowHost = WorkflowHost.Create(
                _loggerFactory,
                pluginDirectory);
            _workflowHost.LoadWorkflowFile(_workflowPath);

            var nodeTypes = _workflowHost.GetNodeTypes();
            AppendLog($"Registered node types: {nodeTypes.Count}");

            await _workflowHost.PrepareExternalNodesAsync();
            var validationErrors = _workflowHost.Validate();
            if (validationErrors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

            await _workflowHost.InitializeAsync();
            await _workflowHost.StartAsync();

            _isRunning = true;
            StopButton.IsEnabled = true;
            StatusText.Text = "Workflow started.";
            AppendLog("Workflow started. Use Stop to end it.");
        }
        catch (Exception exception)
        {
            StopButton.IsEnabled = false;
            RunButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
            StatusText.Text = "Run failed.";
            ShowError("Run workflow failed", exception);
        }
    }

    private async void StopButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_workflowHost is null)
            return;

        try
        {
            StatusText.Text = "Stopping...";
            await _workflowHost.StopAsync();
            await _workflowHost.ShutdownAsync();
            _isRunning = false;
            StopButton.IsEnabled = false;
            RunButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
            StatusText.Text = "Workflow stopped.";
            AppendLog("Workflow stopped and shutdown completed.");
        }
        catch (Exception exception)
        {
            ShowError("Stop workflow failed", exception);
        }
    }

    private void ClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        LogTextBox.Text = string.Empty;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_workflowHost is null)
            return;

        try
        {
            _workflowHost.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _workflowHost.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AppendLog($"Shutdown error: {exception.Message}");
        }
        finally
        {
            _loggerFactory.Dispose();
        }
    }

    private void ShowError(string title, Exception exception)
    {
        StatusText.Text = $"{title}: {exception.Message}";
        AppendLog(title + ": " + exception);
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogTextBox.Text = string.IsNullOrEmpty(LogTextBox.Text)
            ? line
            : LogTextBox.Text + Environment.NewLine + line;
        LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
    }
}
