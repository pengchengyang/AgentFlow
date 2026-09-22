namespace AgentFlow.ViewModels;

/// <summary>
/// View-model for the right-side inspector / properties panel.
/// Thin facade over <see cref="MainViewModel"/>. 1 view &lt;-&gt; 1 view-model.
/// </summary>
public sealed class InspectorViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public InspectorViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedNode))
                OnPropertyChanged(nameof(SelectedNode));
        };
    }

    /// <summary>The currently selected node whose properties are shown (null when none).</summary>
    public NodeViewModel? SelectedNode => _main.SelectedNode;
}