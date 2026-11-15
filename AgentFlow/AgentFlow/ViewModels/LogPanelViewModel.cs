// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogPanelViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Windows.Input;
using AgentFlow.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// View-model for the bottom log panel. Shows structured log entries with multi-level filtering
/// and text search. Thin facade over <see cref="MainViewModel"/>.
/// </summary>
public sealed partial class LogPanelViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _runIdFilter = "";

    /// <summary>When true, the log list follows newly appended entries. Manual scrolling turns it off.</summary>
    [ObservableProperty] private bool _isAutoScrollEnabled = true;

    /// <summary>Raised once after <see cref="FilteredLogs"/> has been rebuilt.</summary>
    public event EventHandler? LogsRefreshed;

    /// <summary>Focus the log panel on one RunId (or a batch prefix). Empty clears the filter.</summary>
    public void SetRunIdFilter(string runId) => RunIdFilter = runId ?? "";

    [RelayCommand]
    private void ClearRunIdFilter() => RunIdFilter = "";

    /// <summary>Currently selected level filters. "ALL" means show everything.</summary>
    public ObservableCollection<string> SelectedLevels { get; } = new() { "ALL" };

    public LogPanelViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLogPanelOpen))
                OnPropertyChanged(nameof(IsLogPanelOpen));
            else if (e.PropertyName == nameof(MainViewModel.IsLogPanelExpanded))
                OnPropertyChanged(nameof(IsLogPanelExpanded));
        };
        UiLogStore.Entries.CollectionChanged += (_, _) => RefreshFilter();
        RefreshFilter();
    }

    public ObservableCollection<UiLogEntry> FilteredLogs { get; } = new();
    public ICommand ClearLogCommand => _main.ClearLogCommand;
    /// <summary>Toggles the log panel between default height and full-canvas height.</summary>
    public ICommand ToggleExpandedCommand => new RelayCommand(ToggleExpanded);

    /// <summary>Direct host callback used by the panel-level double-click gesture.</summary>
    public void ToggleExpanded() => _main.ToggleLogPanelExpanded();
    public ICommand ToggleLevelCommand => new RelayCommand<string>(ToggleLevel);
    public bool IsLogPanelOpen => _main.IsLogPanelOpen;
    /// <summary>Whether the log panel fills the whole canvas area (forwarded from Main).</summary>
    public bool IsLogPanelExpanded => _main.IsLogPanelExpanded;

    partial void OnSearchTextChanged(string value) => RefreshFilter();
    partial void OnRunIdFilterChanged(string value) => RefreshFilter();

    private void ToggleLevel(string? level)
    {
        if (string.IsNullOrEmpty(level)) return;

        if (level == "ALL")
        {
            // Toggle "ALL": if it's already selected, clear all (show nothing? no, keep ALL as default).
            // Simpler: clicking ALL resets to show everything.
            SelectedLevels.Clear();
            SelectedLevels.Add("ALL");
        }
        else
        {
            // Remove "ALL" when a specific level is toggled.
            SelectedLevels.Remove("ALL");

            if (SelectedLevels.Contains(level))
                SelectedLevels.Remove(level);
            else
                SelectedLevels.Add(level);

            // If nothing selected, go back to ALL.
            if (SelectedLevels.Count == 0)
                SelectedLevels.Add("ALL");
        }
        RefreshFilter();
        OnPropertyChanged(nameof(SelectedLevels));
    }

    private void RefreshFilter()
    {
        FilteredLogs.Clear();
        var showAll = SelectedLevels.Contains("ALL");
        foreach (var entry in UiLogStore.Entries)
        {
            if (!showAll && !SelectedLevels.Contains(entry.Level)) continue;
            if (!string.IsNullOrWhiteSpace(RunIdFilter)
                && !entry.RunId.Contains(RunIdFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(SearchText)
                && !MatchesSearch(entry, SearchText)) continue;
            FilteredLogs.Add(entry);
        }

        LogsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private static bool MatchesSearch(UiLogEntry entry, string searchText)
        => entry.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || entry.RunId.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || entry.Tag.Contains(searchText, StringComparison.OrdinalIgnoreCase);
}
