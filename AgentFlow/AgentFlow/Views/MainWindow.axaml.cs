// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Controls;
using Avalonia.Interactivity;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Only prompt when there are unsaved changes.
        if (DataContext is not MainWindowViewModel mvm || mvm.Main is not { IsDirty: true })
            return;

        e.Cancel = true;

        var dialog = new CloseConfirmDialogViewModel(
            "Unsaved Changes",
            "The current workflow has unsaved changes. What would you like to do?");

        var win = new CloseConfirmDialogWindow { DataContext = dialog };
        int result = await win.ShowDialog<int>(this);

        switch (result)
        {
            case 1: // Discard / exit without saving
                mvm.Main.IsDirty = false;
                Close();
                break;
            case 2: // Save then exit
                var path = Path.Combine(AppContext.BaseDirectory, "workflow.json");
                mvm.Main.SaveToPath(path);
                Close();
                break;
            // case 0 (Cancel): do nothing, window stays open.
        }
    }
}
