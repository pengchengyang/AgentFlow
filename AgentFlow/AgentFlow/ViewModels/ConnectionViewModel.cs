namespace AgentFlow.ViewModels;

/// <summary>Connection ViewModel: output pin -> input pin.</summary>
public partial class ConnectionViewModel : ViewModelBase
{
    public PinViewModel Source { get; }
    public PinViewModel Target { get; }

    public ConnectionViewModel(PinViewModel source, PinViewModel target)
    {
        Source = source;
        Target = target;
    }
}
