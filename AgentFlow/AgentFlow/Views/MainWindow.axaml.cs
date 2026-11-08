// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeRestoreIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateMaximizeRestoreIcon();
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeIcon is null || RestoreIcon is null)
            return;

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestoreButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();

    private const double TitleBarDragThresholdPixels = 3;
    private const int TopSnapThresholdPixels = 8;
    private const int TopSnapReleaseThresholdPixels = 18;
    private bool _isTitleBarDragging;
    private bool _titleBarDragMoved;
    private PixelPoint _titleBarDragStartPointer;
    private PixelPoint _titleBarDragStartWindow;
    private bool _topSnapArmed;

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            MaxRestoreButton_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Native restore-drag keeps the familiar Explorer-like behavior from maximized to normal.
            BeginMoveDrag(e);
            e.Handled = true;
            return;
        }

        if (WindowState != WindowState.Normal)
            return;

        _isTitleBarDragging = true;
        _titleBarDragMoved = false;
        _topSnapArmed = false;
        _titleBarDragStartPointer = this.PointToScreen(e.GetPosition(this));
        _titleBarDragStartWindow = Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isTitleBarDragging)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndTitleBarDrag(e.Pointer);
            return;
        }

        var pointer = this.PointToScreen(e.GetPosition(this));
        int deltaX = pointer.X - _titleBarDragStartPointer.X;
        int deltaY = pointer.Y - _titleBarDragStartPointer.Y;

        if (!_titleBarDragMoved &&
            Math.Abs(deltaX) + Math.Abs(deltaY) < TitleBarDragThresholdPixels)
        {
            return;
        }

        _titleBarDragMoved = true;

        // Match Explorer: entering the top edge arms the snap preview. Maximize only on release.
        var screen = Screens.ScreenFromPoint(pointer)
                     ?? Screens.ScreenFromWindow(this);
        bool inTopSnapZone = false;
        if (screen is not null)
        {
            int distanceFromTop = pointer.Y - screen.WorkingArea.Y;
            inTopSnapZone = _topSnapArmed
                ? distanceFromTop <= TopSnapReleaseThresholdPixels
                : distanceFromTop <= TopSnapThresholdPixels;
            _topSnapArmed = inTopSnapZone;
        }
        else
        {
            _topSnapArmed = false;
        }

        int targetY = inTopSnapZone && screen is not null
            ? screen.WorkingArea.Y
            : _titleBarDragStartWindow.Y + deltaY;
        Position = new PixelPoint(
            _titleBarDragStartWindow.X + deltaX,
            targetY);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isTitleBarDragging)
            return;

        bool shouldMaximize = _topSnapArmed;
        EndTitleBarDrag(e.Pointer);
        if (shouldMaximize)
            WindowState = WindowState.Maximized;

        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _topSnapArmed = false;
        _isTitleBarDragging = false;
        _titleBarDragMoved = false;
    }

    private void EndTitleBarDrag(IPointer pointer)
    {
        _isTitleBarDragging = false;
        _topSnapArmed = false;
        _titleBarDragMoved = false;
        pointer.Capture(null);
    }

    /// <summary>
    /// Window-level undo / redo shortcuts. Text editors keep their native Ctrl+Z / Ctrl+Y behavior.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        // Preserve native text editing history when focus is inside a TextBox.
        if (e.Source is Visual source &&
            (source is TextBox || source.FindAncestorOfType<TextBox>() is not null))
        {
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Alt) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (e.Key == Key.Z && viewModel.Main.CanUndoNow)
        {
            viewModel.Main.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && viewModel.Main.CanRedoNow)
        {
            viewModel.Main.RedoCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool _allowClose;
    private bool _closing;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_allowClose) return;
        if (DataContext is not MainWindowViewModel mvm || mvm.Main is null) return;
        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        _closing = true;
        e.Cancel = true;
        var main = mvm.Main;
        try
        {
            // Ask about unsaved changes first; the workflow keeps running while the user decides.
            bool saveRequested = false;
            if (main.IsDirty)
            {
                var dialog = new CloseConfirmDlgViewModel(
                    "Unsaved Changes",
                    "The current workflow has unsaved changes. What would you like to do?");
                var win = new CloseConfirmDlgWindow { DataContext = dialog };
                int result = await win.ShowDialog<int>(this);

                if (result == 0) return;             // Cancel: keep the window open.
                saveRequested = result == 2;
            }

            // Stop first: saving while nodes run is rejected by the edit gate, and the window must
            // never close while nodes still own ports/sockets/files.
            await main.StopForShutdownAsync();
            if (saveRequested && !await main.SaveCurrentAsync())
            {
                // Save failed: keep the window, and let the user keep editing / start again.
                main.CancelShutdown();
                return;
            }

            // Stop and save are complete; now let every node perform its final exit cleanup.
            // Shutdown failures are logged by the view-model and must not keep the window open.
            await main.ShutdownForExitAsync();

            main.IsDirty = false;
            main.DisposeBroadcastDemo();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            main.CancelShutdown();
            main.StatusText = $"{main.L["ErrorPrefix"]}: {ex.Message}";
        }
        finally
        {
            _closing = false;
        }
    }
}
