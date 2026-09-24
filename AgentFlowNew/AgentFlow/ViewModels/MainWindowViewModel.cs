// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainWindowViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.ViewModels;

/// <summary>
/// Root view-model for the main application window. Owns the shared <see cref="MainViewModel"/>
/// which the main content view binds to. Keeps the 1 view &lt;-&gt; 1 view-model mapping for MainWindow.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public MainViewModel Main { get; }

    public MainWindowViewModel()
    {
        Main = new MainViewModel();
    }
}