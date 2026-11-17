using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace GuidCreator.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        GuidText.Text = Guid.NewGuid().ToString("D").ToUpperInvariant();
        CopyButton.IsEnabled = true;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GuidText.Text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(GuidText.Text);
        }
    }
}


