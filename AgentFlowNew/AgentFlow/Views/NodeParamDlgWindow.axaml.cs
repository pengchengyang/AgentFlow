// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParamDlgWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Controls;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// Node parameter dialog window. Presentation and window lifecycle only:
/// listens to the ViewModel's RequestClose event to close the window; no business logic.
/// </summary>
public partial class NodeParamDlgWindow : Window
{
    public NodeParamDlgWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is NodeParameterDialogViewModel vm)
            vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool result)
    {
        if (DataContext is NodeParameterDialogViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(result);
    }
}
