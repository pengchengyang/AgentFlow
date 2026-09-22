using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// View-model for the right-side node library. Thin facade over <see cref="MainViewModel"/>.
/// 1 view &lt;-&gt; 1 view-model.
/// </summary>
public sealed class NodeLibraryViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

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
    }

    public ObservableCollection<PaletteItem> PaletteItems => _main.PaletteItems;

    public string SearchText
    {
        get => _main.SearchText;
        set => _main.SearchText = value;
    }

    /// <summary>Whether this sidebar is visible (Admin login only).</summary>
    public bool IsSidebarVisible => _main.IsSidebarVisible;

    public ICommand AddNodeCommand => _main.AddNodeCommand;
}