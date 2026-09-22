using System.Windows.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// View-model for the top bar. Thin facade over <see cref="MainViewModel"/>
/// exposing only the members the top bar needs. 1 view &lt;-&gt; 1 view-model.
/// </summary>
public sealed class TopBarViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public TopBarViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRunning))
                OnPropertyChanged(nameof(IsRunning));
        };
    }

    public ICommand RunCommand => _main.RunCommand;
    public ICommand StopCommand => _main.StopCommand;
    public ICommand ResetZoomCommand => _main.ResetZoomCommand;
    public ICommand OpenLoginCommand => _main.OpenLoginCommand;
    public bool IsRunning => _main.IsRunning;
}