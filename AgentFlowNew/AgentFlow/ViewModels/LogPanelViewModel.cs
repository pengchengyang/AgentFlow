// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogPanelViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// View-model for the bottom log panel. Thin facade over <see cref="MainViewModel"/>.
/// 1 view &lt;-&gt; 1 view-model.
/// </summary>
public sealed class LogPanelViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public LogPanelViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLogPanelOpen))
                OnPropertyChanged(nameof(IsLogPanelOpen));
        };
    }

    public ObservableCollection<string> Logs => _main.Logs;
    public ICommand ClearLogCommand => _main.ClearLogCommand;
    public bool IsLogPanelOpen => _main.IsLogPanelOpen;
}