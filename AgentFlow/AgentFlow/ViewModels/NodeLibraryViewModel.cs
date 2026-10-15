// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeLibraryViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// One category section of the component palette (design: "INPUT" / "PROCESS" / "OUTPUT").
/// Pure presentation grouping derived from <see cref="PaletteItem.Category"/>.
/// </summary>
public sealed class PaletteGroup
{
    public string Title { get; }
    public IReadOnlyList<PaletteItem> Items { get; }

    public PaletteGroup(string title, IReadOnlyList<PaletteItem> items)
    {
        Title = title;
        Items = items;
    }
}

/// <summary>
/// View-model for the right-side node library. Thin facade over <see cref="MainViewModel"/>.
/// 1 view &lt;-&gt; 1 view-model. The grouped view is a read-only projection of
/// <see cref="MainViewModel.PaletteItems"/> — it holds no business state of its own.
/// </summary>
public sealed class NodeLibraryViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    /// <summary>Preferred section order, matching the design; unknown categories follow.</summary>
    private static readonly string[] SectionOrder = { "Input", "Process", "Output" };

    public NodeLibraryViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SearchText))
                OnPropertyChanged(nameof(SearchText));
            else if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible))
                OnPropertyChanged(nameof(IsSidebarVisible));
        };
        // Rebuild the section view whenever the (search-filtered) palette changes.
        _main.PaletteItems.CollectionChanged += (_, _) => RebuildGroups();
        RebuildGroups();
    }

    public ObservableCollection<PaletteItem> PaletteItems => _main.PaletteItems;

    /// <summary>Palette items grouped by category for the sectioned list.</summary>
    public ObservableCollection<PaletteGroup> Groups { get; } = new();

    public string SearchText
    {
        get => _main.SearchText;
        set => _main.SearchText = value;
    }

    /// <summary>Whether this Sidebar is visible (Admin login only).</summary>
    public bool IsSidebarVisible => _main.IsSidebarVisible;

    public ICommand AddNodeCommand => _main.AddNodeCommand;

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var g in _main.PaletteItems
                     .GroupBy(i => i.Category ?? string.Empty)
                     .OrderBy(g => SectionRank(g.Key))
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Groups.Add(new PaletteGroup(g.Key.ToUpperInvariant(), g.ToList()));
        }
    }

    private static int SectionRank(string category)
    {
        for (int i = 0; i < SectionOrder.Length; i++)
            if (string.Equals(SectionOrder[i], category, StringComparison.OrdinalIgnoreCase))
                return i;
        return SectionOrder.Length;
    }
}
