using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Platform.Windows;
using Sidey.Presentation.ViewModels;
using Windows.System;

namespace Sidey.App.Views;

public sealed partial class ComposerWindow : Window
{
    private const int ComposerWidth = 400;
    private const int ComposerHeight = 56;
    private const int FocusAttemptCount = 3;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcherQueue;
    private readonly WindowsBorderlessWindowController _borderlessWindow;
    private bool _focusRequested;
    private bool _isHiding;
    private bool _isVisible;
    private bool _isClosed;
    private bool _allowClose;
    private bool _isDragging;
    private uint _dragPointerId;
    private Windows.Graphics.PointInt32 _dragStartPosition;
    private bool _isComposing;
    private ComposerPlacement? _placement;
    private string? _monitorIdentifier;
    private int _focusRequestId;

    public ComposerWindow(ComposerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _uiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        ComposerRoot.DataContext = ViewModel;
        Title = I18n.Get("window.composerTitle");
        SideyWindowIcon.Apply(AppWindow);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

        _borderlessWindow = new WindowsBorderlessWindowController(
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        _borderlessWindow.DisplayConfigurationChanged += OnDisplayConfigurationChanged;

        ViewModel.CloseRequested += OnCloseRequested;
        MessageInput.Loaded += OnMessageInputLoaded;
        MessageInput.TextCompositionStarted += (_, _) => _isComposing = true;
        MessageInput.TextCompositionEnded += (_, _) => _isComposing = false;
        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
    }

    public ComposerViewModel ViewModel { get; }

    public event Action<ComposerPlacement>? PlacementChanged;

    public void ApplyTheme(AppThemePreference theme)
    {
        if (!_isClosed)
        {
            SideyWindowTheme.Apply(ComposerRoot, theme);
        }
    }

    public void ShowAndFocus(string? monitorIdentifier, ComposerPlacement? placement = null)
    {
        if (_isClosed)
        {
            return;
        }

        ViewModel.OnShown();
        _monitorIdentifier = monitorIdentifier;
        _placement = placement?.Normalize();
        RestorePlacement();
        _isVisible = true;
        AppWindow.Show();
        Activate();
        SideyWindowActivation.BringToForeground(this);
        RequestMessageInputFocus();
    }

    public void HideComposer()
    {
        if (_isClosed || !_isVisible || _isHiding)
        {
            return;
        }

        _isHiding = true;
        FinishDrag(restoreFocus: false);
        _borderlessWindow.SetDragGripHovered(false);
        _isVisible = false;
        try
        {
            _focusRequestId++;
            _focusRequested = false;
            ViewModel.OnHidden();
            StartupDiagnostics.Stage("composer-hide-started");
            AppWindow.Hide();
            StartupDiagnostics.Stage("composer-hidden");
        }
        finally
        {
            _isHiding = false;
        }
    }

    public void RestoreDraftAndFocus(string body)
    {
        if (_isClosed)
        {
            return;
        }

        ViewModel.RestoreDraft(body);
        _isVisible = true;
        AppWindow.Show();
        Activate();
        SideyWindowActivation.BringToForeground(this);
        RequestMessageInputFocus();
    }

    public void CloseForExit()
    {
        if (_isClosed)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void OnMessageInputPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        _ = sender;
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            ViewModel.CloseCommand.Execute(null);
            return;
        }

        if (args.Key != VirtualKey.Enter || _isComposing)
        {
            return;
        }

        Windows.UI.Core.CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(
            VirtualKey.Shift);
        if ((shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            if (!ViewModel.CanAddLine)
            {
                args.Handled = true;
            }

            return;
        }

        args.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
            RequestMessageInputFocus();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _ = sender;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_isDragging && !_focusRequested && _isVisible && !_isHiding)
            {
                HideComposer();
            }

            return;
        }

        if (_isVisible && !_isDragging)
        {
            RequestMessageInputFocus();
        }
    }

    private void OnMessageInputLoaded(object sender, RoutedEventArgs args)
    {
        if (!_isClosed && _isVisible)
        {
            RequestMessageInputFocus();
        }
    }

    private void OnCloseRequested()
    {
        if (_isClosed)
        {
            return;
        }

        if (!_uiDispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed && !_isDragging)
                {
                    HideComposer();
                }
            }))
        {
            StartupDiagnostics.Stage("composer-hide-queue-rejected");
        }
    }

    private void OnAppWindowClosing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        _ = sender;
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        OnCloseRequested();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _ = sender;
        _ = args;
        _isClosed = true;
        FinishDrag(restoreFocus: false);
        _borderlessWindow.DisplayConfigurationChanged -= OnDisplayConfigurationChanged;
        _borderlessWindow.Dispose();
        _focusRequestId++;
        _focusRequested = false;
        _isVisible = false;
        AppWindow.Closing -= OnAppWindowClosing;
        MessageInput.Loaded -= OnMessageInputLoaded;
        ViewModel.CloseRequested -= OnCloseRequested;
        ViewModel.Dispose();
    }

    private void RequestMessageInputFocus()
    {
        _focusRequested = true;
        int requestId = ++_focusRequestId;
        QueueMessageInputFocus(requestId, FocusAttemptCount);
    }

    private void QueueMessageInputFocus(int requestId, int attemptsRemaining)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || requestId != _focusRequestId || !_isVisible || _isDragging)
            {
                return;
            }

            if (MessageInput.Focus(FocusState.Programmatic))
            {
                MessageInput.SelectionStart = MessageInput.Text.Length;
                _focusRequested = false;
                return;
            }

            if (attemptsRemaining > 0)
            {
                QueueMessageInputFocus(requestId, attemptsRemaining - 1);
            }
            else
            {
                _focusRequested = false;
            }
        });
    }

    private void OnDragGripPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        PointerPoint point = args.GetCurrentPoint(ComposerRoot);
        if (_isClosed || _isDragging || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!DragGrip.CapturePointer(args.Pointer))
        {
            return;
        }

        args.Handled = true;
        _dragStartPosition = AppWindow.Position;
        _dragPointerId = args.Pointer.PointerId;
        _focusRequestId++;
        _focusRequested = false;
        ViewModel.OnShown();
        _isDragging = true;
        _borderlessWindow.BeginDrag();
    }

    private void OnDragGripPointerEntered(object sender, PointerRoutedEventArgs args) =>
        _borderlessWindow.SetDragGripHovered(true);

    private void OnDragGripPointerExited(object sender, PointerRoutedEventArgs args) =>
        _borderlessWindow.SetDragGripHovered(false);

    private void OnDragGripPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        _borderlessWindow.RefreshDragGripCursor();
        if (!_isDragging || args.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        args.Handled = true;
        PointerPoint point = args.GetCurrentPoint(ComposerRoot);
        if (!point.IsInContact)
        {
            FinishDrag();
            return;
        }

        _borderlessWindow.DragTo();
    }

    private void OnDragGripPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_isDragging && args.Pointer.PointerId == _dragPointerId)
        {
            args.Handled = true;
            _borderlessWindow.DragTo();
            FinishDrag();
        }
    }

    private void OnDragGripPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_isDragging && args.Pointer.PointerId == _dragPointerId)
        {
            FinishDrag(restoreFocus: false);
        }
    }

    private void FinishDrag(bool restoreFocus = true)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _borderlessWindow.EndDrag();
        DragGrip.ReleasePointerCaptures();
        if (_isClosed)
        {
            return;
        }

        if (AppWindow.Position.X != _dragStartPosition.X || AppWindow.Position.Y != _dragStartPosition.Y)
        {
            SaveDraggedPlacement();
        }
        if (restoreFocus && _isVisible)
        {
            RequestMessageInputFocus();
        }
    }

    private void SaveDraggedPlacement()
    {
        Windows.Graphics.PointInt32 position = AppWindow.Position;
        double centerX = position.X + (AppWindow.Size.Width / 2d);
        double centerY = position.Y + (AppWindow.Size.Height / 2d);
        WindowsMonitorInfo monitor = WindowsMonitorService.GetAll()
            .MinBy(candidate => DistanceToMonitor(candidate, centerX, centerY))
            ?? WindowsMonitorService.Select(_monitorIdentifier);
        double scale = monitor.Dpi / 96d;
        _placement = new ComposerPlacement(
            monitor.Identifier,
            (position.X - monitor.WorkAreaPixels.X) / scale,
            (position.Y - monitor.WorkAreaPixels.Y) / scale);
        RestorePlacement();
        if (_placement is not null)
        {
            PlacementChanged?.Invoke(_placement);
        }
    }

    private static double DistanceToMonitor(WindowsMonitorInfo monitor, double x, double y)
    {
        NativePixelRect area = monitor.MonitorPixels;
        double dx = x - Math.Clamp(x, area.X, area.X + area.Width);
        double dy = y - Math.Clamp(y, area.Y, area.Y + area.Height);
        return (dx * dx) + (dy * dy);
    }

    private void OnDisplayConfigurationChanged()
    {
        _uiDispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed && _isVisible && !_isDragging)
            {
                RestorePlacement();
            }
        });
    }

    private void RestorePlacement()
    {
        WindowsMonitorInfo monitor = WindowsMonitorService.Select(_placement?.MonitorIdentifier ?? _monitorIdentifier);
        double scale = monitor.Dpi / 96d;
        int width = (int)Math.Round(ComposerWidth * scale, MidpointRounding.AwayFromZero);
        int height = (int)Math.Round(ComposerHeight * scale, MidpointRounding.AwayFromZero);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        NativePixelRect workArea = monitor.WorkAreaPixels;
        Windows.Graphics.SizeInt32 windowSize = AppWindow.Size;
        ComposerPlacement resolved = ComposerPlacementPolicy.Resolve(
            _placement,
            monitor.Identifier,
            workArea.Width / scale,
            workArea.Height / scale,
            windowSize.Width / scale,
            windowSize.Height / scale);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + (int)Math.Round(resolved.X * scale),
            workArea.Y + (int)Math.Round(resolved.Y * scale)));
        if (_placement is not null)
        {
            _placement = resolved;
        }
    }
}
