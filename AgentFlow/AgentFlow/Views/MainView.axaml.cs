// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.DialogRequested -= OnDialogRequested;
            _viewModel.NewWorkflowRequested -= OnNewWorkflowRequested;
            _viewModel.ConfirmRequested -= OnNewWorkflowRequested;
            _viewModel.LoginRequested -= OnLoginRequested;
            _viewModel.SaveAsRequested -= OnSaveAsRequested;
            _viewModel.OpenRequested -= OnOpenRequested;
            _viewModel = null;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.DialogRequested += OnDialogRequested;
            vm.NewWorkflowRequested += OnNewWorkflowRequested;
            vm.ConfirmRequested += OnNewWorkflowRequested;
            vm.LoginRequested += OnLoginRequested;
            vm.SaveAsRequested += OnSaveAsRequested;
            vm.OpenRequested += OnOpenRequested;
            _viewModel = vm;
        }
    }

    /// <summary>Open the node parameter dialog above the top-level window. Presentation and window lifecycle only; no business logic.</summary>
    private async void OnDialogRequested(object? sender, NodeParamDlgViewModel dialog)
    {
        var win = new NodeParamDlgWindow { DataContext = dialog };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    /// <summary>Open the new-workflow confirmation dialog (modal) above the top-level window. Presentation and window lifecycle only.</summary>
    private async void OnNewWorkflowRequested(object? sender, ConfirmDialogViewModel dialog)
    {
        var win = new ConfirmDialogWindow { DataContext = dialog };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    /// <summary>Open the centered login dialog (modal) above the top-level window. Presentation and window lifecycle only.</summary>
    private async void OnLoginRequested(object? sender, LoginDialogViewModel dialog)
    {
        var win = new LoginDialogWindow { DataContext = dialog };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    /// <summary>Open a native Save-As picker; default folder is the executable directory, default name is the suggested timestamped file.</summary>
    private async void OnSaveAsRequested(object? sender, string suggestedFileName)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return;

            var startDir = await topLevel.StorageProvider.TryGetFolderFromPathAsync(AppContext.BaseDirectory);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Workflow",
                SuggestedStartLocation = startDir,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "rw",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("AgentFlow RW Workflow") { Patterns = new[] { "*.rw" } }
                }
            });

            if (file?.TryGetLocalPath() is { } path && DataContext is MainViewModel vm)
                vm.SaveToPath(path);
        }
        catch (Exception ex)
        {
            // A platform file picker can fail independently of the workflow; report it without
            // tearing down the app from an async-void event handler.
            if (DataContext is MainViewModel vm)
                vm.StatusText = $"{vm.L["ErrorPrefix"]}: {ex.Message}";
        }
    }


    /// <summary>Open a native file-open picker; default folder is the executable directory. Loads the chosen workflow.</summary>
    private async void OnOpenRequested(object? sender, System.EventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return;

            var startDir = await topLevel.StorageProvider.TryGetFolderFromPathAsync(AppContext.BaseDirectory);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Workflow",
                SuggestedStartLocation = startDir,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("AgentFlow RW Workflow") { Patterns = new[] { "*.rw" } },
                    new FilePickerFileType("Legacy JSON Workflow") { Patterns = new[] { "*.json" } }
                }
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && DataContext is MainViewModel vm)
                vm.LoadFromPath(path);
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vm)
                vm.StatusText = $"{vm.L["ErrorPrefix"]}: {ex.Message}";
        }
    }
}

