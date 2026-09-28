using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Sidey.App.Controls;
using Sidey.App.Localization;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Platform.Windows;
using Sidey.Platform.Windows.Shell;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;
using Windows.System;
using Windows.UI.ViewManagement;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Sidey.App.Views;

public sealed partial class MainWindow : Window, IMainWindowDialogService
{
    private readonly DispatcherTimer _statusDismissTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4),
    };

#if DEBUG
    private readonly DispatcherTimer _validationMetricsTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
#endif

    private bool _allowClose;
    private bool _trayAvailable;
    private bool _navigatingBack;
    private bool _storeSearchUpdateQueued;
    private bool _storePreviewDialogOpen;
    private Task _storeFilterTransition = Task.CompletedTask;
    private readonly HashSet<Guid> _roomExpansionAnimations = [];
    private bool _hideQueued;
    private bool _isClosed;
    private bool _hotkeyRecordingActive;
    private bool _refreshingThemeSelection;
    private Action? _cancelHotkeyEditor;
    private string? _pageRequestedAfterHotkeyEditor;
    private bool _scrollToUpdatesAfterHotkeyEditor;
    private string _currentNavigationTag = "profile";
    private readonly Stack<string> _navigationHistory = new();
    private readonly WindowsMinimumSizeController _minimumSizeController;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IMainWindowCoordinator _coordinator;
    private readonly WindowsFeedbackWindowMonitor? _feedbackMonitor;
    private StorePreviewStage? _activePreview;

    public MainWindow(
        IMainWindowCoordinator coordinator,
        IUpdateService updateService)
    {
        InitializeComponent();
        RefreshLocalizationLayout();
        _coordinator = coordinator;
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(
                SideyDeploymentPaths.DeploymentRoot(), "Assets", "Icons", "SideyAppIcon-20.png"))),
        };
        ViewModel = new MainWindowViewModel(coordinator, this, updateService);
        CharacterSoundVolumeSlider.AdjustmentCompleted += () =>
        {
            ViewModel.CharacterSoundEffectsVolume = CharacterSoundVolumeSlider.Value;
            ViewModel.CompleteSoundVolumeAdjustment();
        };
        CharacterSoundVolumeSlider.AdjustmentCanceled += ViewModel.StopSoundVolumeFeedback;
        if (coordinator is AppCoordinator appCoordinator)
            appCoordinator.AnimationsChanged += OnAnimationsChanged;
        try
        {
            _feedbackMonitor = new WindowsFeedbackWindowMonitor(WinRT.Interop.WindowNative.GetWindowHandle(this),
                () => coordinator.StopImpactSounds(), async () =>
                {
                    try
                    { await coordinator.RetryConnectionAsync(userInitiated: false); }
                    catch (Exception exception) { StartupDiagnostics.NonFatal("resume-connection", exception); }
                });
        }
        catch (Exception exception) { StartupDiagnostics.NonFatal("feedback-system-notifications", exception); }
        MainRoot.DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyRequestedTheme();
        ViewModel.PrepareGroupsForPresentation();
        Title = "SIDEY";
        SideyWindowIcon.Apply(AppWindow);
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        SideyWindowTheme.FollowTitleBarTheme(this, MainRoot);
        MainRoot.Loaded += OnResponsiveRootLoaded;
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ResponsiveWindowSize minimumWindowSize = ApplyResponsiveSize();
        _minimumSizeController = new WindowsMinimumSizeController(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            minimumWindowSize);
        ApplyBackdrop();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        ViewModel.NoticeRaised += OnNoticeRaised;
        ViewModel.StorePreviewRequested += OnStorePreviewRequested;
        _statusDismissTimer.Tick += OnStatusDismissTimerTick;
#if DEBUG
        _validationMetricsTimer.Tick += OnValidationMetricsTimerTick;
        _validationMetricsTimer.Start();
#endif
    }

    public MainWindowViewModel ViewModel { get; }

    public void RefreshLocalizationLayout() => LocalizationLayout.Apply(MainRoot);

    public void RefreshLocalizedThemeSelection()
    {
        // ComboBox keeps a copy of the selected item's text while it is closed.
        // Reselect after the localized item bindings have updated so the copy changes too.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || ThemeComboBox.SelectedIndex < 0)
                return;

            int selectedIndex = ThemeComboBox.SelectedIndex;
            bool wasSelectionEnabled = ViewModel.IsThemeSelectionEnabled;
            _refreshingThemeSelection = true;
            ViewModel.IsThemeSelectionEnabled = false;
            try
            {
                ThemeComboBox.SelectedIndex = -1;
                ThemeComboBox.SelectedIndex = selectedIndex;
            }
            finally
            {
                ViewModel.IsThemeSelectionEnabled = wasSelectionEnabled;
                _refreshingThemeSelection = false;
            }
        });
    }

    public event Action<bool>? HotkeyRecordingChanged;

    private async void OnHotkeyRecorderClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || HotkeyActionFor(button) is not { } action
            || ActiveXamlRoot() is not { } xamlRoot || !ViewModel.IsHotkeySelectionEnabled)
            return;

        ViewModel.BeginHotkeyRecording(action);
        GlobalHotkeyBinding? candidate = ViewModel.HotkeyBindingFor(action);
        double contentWidth = Math.Min(560, Math.Max(240, xamlRoot.Size.Width - 96));
        var keycaps = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var captureHost = new Grid { Width = contentWidth };
        captureHost.Children.Add(keycaps);
        var emptyPrompt = new TextBlock
        {
            Text = I18n.Get("settings.shortcuts.recorder.empty"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        captureHost.Children.Add(emptyPrompt);
        var capture = new Button
        {
            Content = captureHost,
            Width = contentWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinHeight = 110,
            Style = (Style)MainRoot.Resources["HotkeyEditorCaptureStyle"],
        };
        AutomationProperties.SetName(capture, I18n.Get("settings.shortcuts.recorder.help"));
        var warningText = new TextBlock
        {
            MaxWidth = contentWidth - 72,
            TextWrapping = TextWrapping.Wrap,
        };
        var notice = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Warning,
            Content = warningText,
        };
        var resetContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        resetContent.Children.Add(new TextBlock
        {
            Text = "\uE777",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Style = (Style)MainRoot.Resources["HotkeyEditorActionTextStyle"],
        });
        resetContent.Children.Add(new TextBlock
        {
            Text = I18n.Get("settings.shortcuts.recorder.reset"),
            Style = (Style)MainRoot.Resources["HotkeyEditorActionTextStyle"],
        });
        var reset = new Button
        {
            Content = resetContent,
            Style = (Style)MainRoot.Resources["HotkeyEditorActionStyle"],
        };
        var deleteContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        deleteContent.Children.Add(new TextBlock
        {
            Text = "\uE8BB",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Style = (Style)MainRoot.Resources["HotkeyEditorActionTextStyle"],
        });
        deleteContent.Children.Add(new TextBlock
        {
            Text = I18n.Get("common.delete"),
            Style = (Style)MainRoot.Resources["HotkeyEditorActionTextStyle"],
        });
        var delete = new Button
        {
            Content = deleteContent,
            Style = (Style)MainRoot.Resources["HotkeyEditorActionStyle"],
        };
        GlobalHotkeyBinding defaultBinding = GlobalHotkeySettings.Default.BindingFor(action);
        AutomationProperties.SetName(reset,
            $"{I18n.Get("settings.shortcuts.recorder.reset")}: {defaultBinding.ToDisplayText()}");
        AutomationProperties.SetName(delete, I18n.Get("common.delete"));
        ToolTipService.SetToolTip(reset, defaultBinding.ToDisplayText());
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        actions.Children.Add(reset);
        actions.Children.Add(delete);
        var content = new Grid
        {
            Width = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            RowSpacing = 16,
        };
        for (int index = 0; index < 4; index++)
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var help = new TextBlock
        {
            Text = I18n.Get("settings.shortcuts.recorder.help"),
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(help);
        Grid.SetRow(capture, 1);
        content.Children.Add(capture);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        Grid.SetRow(notice, 3);
        content.Children.Add(notice);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = HotkeyActionTitle(action),
            Content = content,
            PrimaryButtonText = I18n.Get("common.save"),
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.None,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            RequestedTheme = MainRoot.ActualTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 660d;
        bool cancelledByNavigation = false;
        void CancelEditor()
        {
            cancelledByNavigation = true;
            dialog.Hide();
        }
        Action cancelEditor = CancelEditor;
        _cancelHotkeyEditor = cancelEditor;
        string? invalidShortcut = null;

        void ShowCandidate()
        {
            string[] labels;
            if (invalidShortcut is { } invalid)
                labels = [invalid];
            else if (candidate is { IsDisabled: false } binding)
                labels = MainWindowViewModel.HotkeyBindingText(binding).Split(" + ");
            else
                labels = [];
            emptyPrompt.Visibility = labels.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetHotkeyEditorKeys(keycaps, labels, invalidShortcut is not null);
            dialog.IsPrimaryButtonEnabled = candidate is { } valid
                && (valid.IsValid() || valid == GlobalHotkeyBinding.Disabled);
            string? warning = invalidShortcut is null ? null : I18n.Get("settings.shortcuts.error.invalid");
            if (candidate is { } selected && !selected.IsDisabled)
            {
                if (ViewModel.ConflictingHotkeyAction(action, selected) is not null)
                {
                    warning = I18n.Get("settings.shortcuts.error.in_app_conflict");
                }
                else if (selected.IsValid())
                {
                    warning = WindowsHotkeyAvailability.Check(selected) switch
                    {
                        WindowsHotkeyAvailabilityResult.SystemShortcut =>
                            I18n.Get("settings.shortcuts.error.system_conflict"),
                        WindowsHotkeyAvailabilityResult.AlreadyRegistered =>
                            I18n.Get("settings.shortcuts.error.already_registered"),
                        WindowsHotkeyAvailabilityResult.Unverified =>
                            I18n.Get("settings.shortcuts.error.availability_unverified"),
                        _ => null,
                    };
                }
            }
            notice.Severity = invalidShortcut is null ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            notice.IsOpen = warning is not null;
            warningText.Text = warning ?? string.Empty;
            AutomationProperties.SetName(capture, invalidShortcut ?? (candidate is { } selectedBinding
                ? MainWindowViewModel.HotkeyBindingText(selectedBinding)
                : I18n.Get("settings.shortcuts.recorder.help")));
        }

        bool RecordKey(uint virtualKey, GlobalHotkeyModifiers modifiers)
        {
            if (WindowsFocusedHotkeyRecorder.ShouldPassThrough(virtualKey, modifiers))
                return false;
            if (virtualKey == (uint)VirtualKey.Escape)
            {
                dialog.Hide();
                return true;
            }
            if (GlobalHotkeyBinding.IsModifierKey(virtualKey))
            {
                candidate = null;
                invalidShortcut = null;
                emptyPrompt.Visibility = Visibility.Collapsed;
                SetHotkeyEditorKeys(keycaps, GlobalHotkeyBinding.ModifierDisplayText(modifiers)
                    .Split(" + ", StringSplitOptions.RemoveEmptyEntries));
                dialog.IsPrimaryButtonEnabled = false;
                notice.IsOpen = false;
                return true;
            }

            var binding = new GlobalHotkeyBinding(modifiers, virtualKey);
            if (!binding.IsValid())
            {
                candidate = null;
                invalidShortcut = binding.ToDisplayText();
                ShowCandidate();
                return true;
            }
            candidate = binding;
            invalidShortcut = null;
            ShowCandidate();
            return true;
        }

        bool dialogOpen = true;
        using var recorder = new WindowsFocusedHotkeyRecorder(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => capture.FocusState != FocusState.Unfocused,
            (virtualKey, modifiers) =>
            {
                if (!DispatcherQueue.TryEnqueue(() =>
                    {
                        if (dialogOpen && capture.FocusState != FocusState.Unfocused)
                            _ = RecordKey(virtualKey, modifiers);
                    }))
                    throw new InvalidOperationException("Shortcut editor is no longer available.");
            });
        void SetRecorderActive(bool active)
        {
            if (!dialogOpen)
                return;
            try
            {
                recorder.SetActive(active);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                StartupDiagnostics.NonFatal("hotkey-recorder-hook", exception);
            }
        }
        void OnWindowActivated(object sender, WindowActivatedEventArgs activationArgs) =>
            SetRecorderActive(activationArgs.WindowActivationState != WindowActivationState.Deactivated
                && capture.FocusState != FocusState.Unfocused);
        Activated += OnWindowActivated;
        capture.GotFocus += (_, _) => SetRecorderActive(true);
        capture.LostFocus += (_, _) => SetRecorderActive(false);
        capture.PreviewKeyDown += (_, keyArgs) =>
        {
            uint virtualKey = (uint)keyArgs.Key;
            GlobalHotkeyModifiers modifiers = CurrentHotkeyModifiers();
            if (recorder.IsActive)
                modifiers |= recorder.PressedModifiers & GlobalHotkeyModifiers.Windows;
            keyArgs.Handled = RecordKey(virtualKey, modifiers);
        };
        capture.Click += (_, _) => capture.Focus(FocusState.Programmatic);
        reset.Click += (_, _) =>
        {
            candidate = defaultBinding;
            invalidShortcut = null;
            ShowCandidate();
            capture.Focus(FocusState.Programmatic);
        };
        delete.Click += (_, _) =>
        {
            candidate = GlobalHotkeyBinding.Disabled;
            invalidShortcut = null;
            ShowCandidate();
            capture.Focus(FocusState.Programmatic);
        };
        dialog.Loaded += (_, _) => capture.Focus(FocusState.Programmatic);
        try
        {
            // Release SIDEY's registered shortcuts before probing the initial binding.
            // Keep them released while the dialog is open, even if its controls lose focus.
            SetHotkeyRecordingActive(true);
            ShowCandidate();
            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && !cancelledByNavigation && candidate is { } selected)
                ViewModel.AssignGlobalHotkey(action, selected);
        }
        catch (Exception) when (_isClosed)
        {
        }
        finally
        {
            dialogOpen = false;
            if (ReferenceEquals(_cancelHotkeyEditor, cancelEditor))
                _cancelHotkeyEditor = null;
            Activated -= OnWindowActivated;
            try
            {
                recorder.SetActive(false);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                StartupDiagnostics.NonFatal("hotkey-recorder-unhook", exception);
            }
            ViewModel.CancelHotkeyRecording(action);
            SetHotkeyRecordingActive(false);
            if (_pageRequestedAfterHotkeyEditor is { } requestedPage)
            {
                _pageRequestedAfterHotkeyEditor = null;
                bool scrollToUpdates = _scrollToUpdatesAfterHotkeyEditor
                    && requestedPage == "about";
                _scrollToUpdatesAfterHotkeyEditor = false;
                ShowPage(requestedPage);
                if (scrollToUpdates)
                    ShowUpdates();
            }
        }
    }

    private void SetHotkeyEditorKeys(StackPanel keycaps, IEnumerable<string> labels, bool invalid = false)
    {
        string[] ordered = [.. labels];
        keycaps.Children.Clear();
        foreach (string label in ordered.Contains("Win")
            ? new[] { "Win" }.Concat(ordered.Where(value => value != "Win"))
            : ordered)
        {
            UIElement symbol;
            if (label == "Win")
            {
                var logo = new Grid { Width = 22, Height = 22, RowSpacing = 2, ColumnSpacing = 2 };
                logo.RowDefinitions.Add(new RowDefinition());
                logo.RowDefinitions.Add(new RowDefinition());
                logo.ColumnDefinitions.Add(new ColumnDefinition());
                logo.ColumnDefinitions.Add(new ColumnDefinition());
                for (int row = 0; row < 2; row++)
                {
                    for (int column = 0; column < 2; column++)
                    {
                        var pane = new Rectangle
                        {
                            Style = (Style)MainRoot.Resources["HotkeyEditorIconRectangleStyle"],
                        };
                        Grid.SetRow(pane, row);
                        Grid.SetColumn(pane, column);
                        logo.Children.Add(pane);
                    }
                }
                AutomationProperties.SetName(logo, "Win");
                symbol = logo;
            }
            else if (label == "Shift")
            {
                var shiftIcon = new Grid { Width = 24, Height = 24 };
                shiftIcon.Children.Add(new Microsoft.UI.Xaml.Shapes.Polygon
                {
                    Style = (Style)MainRoot.Resources["HotkeyEditorIconPolygonStyle"],
                    Points =
                    [
                        new(12, 1), new(23, 12), new(18, 12), new(18, 20),
                        new(6, 20), new(6, 12), new(1, 12),
                    ],
                    Width = 24,
                    Height = 20,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                shiftIcon.Children.Add(new Rectangle
                {
                    Style = (Style)MainRoot.Resources["HotkeyEditorIconRectangleStyle"],
                    Width = 12,
                    Height = 2,
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
                AutomationProperties.SetName(shiftIcon, "Shift");
                symbol = shiftIcon;
            }
            else
            {
                symbol = new TextBlock
                {
                    Text = label,
                    Style = (Style)MainRoot.Resources[invalid
                        ? "HotkeyEditorInvalidLabelStyle" : "HotkeyEditorLabelStyle"],
                };
            }
            var keycap = new Border
            {
                Style = (Style)MainRoot.Resources[invalid
                    ? "HotkeyEditorInvalidKeycapStyle" : "HotkeyEditorKeycapStyle"],
                Child = symbol,
            };
            keycaps.Children.Add(keycap);
        }
    }

    private static string HotkeyActionTitle(GlobalHotkeyAction action) => I18n.Get(action switch
    {
        GlobalHotkeyAction.ToggleOverlay => "settings.shortcuts.action.overlay",
        GlobalHotkeyAction.ToggleQuietMode => "settings.shortcuts.action.quiet_mode",
        GlobalHotkeyAction.Compose => "settings.shortcuts.action.composer",
        GlobalHotkeyAction.History => "settings.shortcuts.action.history",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    });

    private void SetHotkeyRecordingActive(bool active)
    {
        if (_hotkeyRecordingActive == active)
            return;
        _hotkeyRecordingActive = active;
        HotkeyRecordingChanged?.Invoke(active);
    }

    private GlobalHotkeyAction? HotkeyActionFor(Button button)
    {
        if (ReferenceEquals(button, OverlayHotkeyRecorder))
            return GlobalHotkeyAction.ToggleOverlay;
        if (ReferenceEquals(button, QuietModeHotkeyRecorder))
            return GlobalHotkeyAction.ToggleQuietMode;
        if (ReferenceEquals(button, ComposerHotkeyRecorder))
            return GlobalHotkeyAction.Compose;
        if (ReferenceEquals(button, HistoryHotkeyRecorder))
            return GlobalHotkeyAction.History;
        return null;
    }

    private static GlobalHotkeyModifiers CurrentHotkeyModifiers()
    {
        GlobalHotkeyModifiers modifiers = GlobalHotkeyModifiers.None;
        if (IsKeyDown(VirtualKey.Control))
            modifiers |= GlobalHotkeyModifiers.Control;
        if (IsKeyDown(VirtualKey.Menu))
            modifiers |= GlobalHotkeyModifiers.Alt;
        if (IsKeyDown(VirtualKey.Shift))
            modifiers |= GlobalHotkeyModifiers.Shift;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
            modifiers |= GlobalHotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        _ = sender;
        if (args.PropertyName == nameof(MainWindowViewModel.SelectedThemeIndex) && !_refreshingThemeSelection)
            ApplyRequestedTheme();
    }

    private void ApplyRequestedTheme()
    {
        SideyWindowTheme.Apply(
            MainRoot,
            (AppThemePreference)ViewModel.SelectedThemeIndex);
        ApplyStoreFilterToggleSurface(StoreFilterPanel.Visibility == Visibility.Visible);
    }

    private void OnAnimationsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
                return;
            ViewModel.RefreshFeedbackPresentation();
            _activePreview?.SetAnimationsEnabled(_coordinator.AnimationsEnabled);
            UpdateResponsiveAnimations(MainRoot);
        });
    }

    private void OnResponsiveRootLoaded(object sender, RoutedEventArgs args)
    {
        UpdateResponsiveState();
        UpdateResponsiveAnimations(MainRoot);
    }

    private void OnPageViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateResponsiveState();

    private void UpdateResponsiveState()
    {
        if (_isClosed || _coordinator is null || PageViewport.ActualWidth <= 0)
        {
            return;
        }
        VisualStateManager.GoToState(MainRoot,
            PageViewport.ActualWidth < 680 ? "Narrow" : "Standard", _coordinator.AnimationsEnabled);
    }

    private void UpdateResponsiveAnimations(DependencyObject root)
    {
        if (ReferenceEquals(root, MainRoot))
        {
            FrameworkElement[] controls = [GroupCreateAction, GroupJoinAction, StoreFilterToggle,
                StoreSortStack, StoreHideOwnedCheckBox, SoundControlsGrid, EdgeComboBox,
                SpanComboBox, MonitorComboBox, LanguageComboBox, ThemeComboBox];
            foreach (FrameworkElement control in controls)
            {
                control.Transitions = _coordinator.AnimationsEnabled
                    ? [new RepositionThemeTransition { IsStaggeringEnabled = false }] : null;
            }
        }
        if (root is ResponsiveFormPanel form)
        {
            form.AnimationsEnabled = _coordinator.AnimationsEnabled;
        }
        if (root is ResponsiveSelectionPanel selection)
        {
            selection.AnimationsEnabled = _coordinator.AnimationsEnabled;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            UpdateResponsiveAnimations(VisualTreeHelper.GetChild(root, index));
        }
    }

    public bool ShouldExitOnClose => _allowClose || !_trayAvailable;

    public void ApplyState(CoordinatorState state)
    {
        if (!_isClosed)
        {
            ViewModel.ApplyState(state);
        }
    }

    public void RefreshMonitors()
    {
        if (!_isClosed)
        {
            ViewModel.RefreshMonitors();
        }
    }

    public void ShowFatalError(Exception exception)
    {
        if (!_isClosed)
        {
            ViewModel.ReportError(exception);
        }
    }

    public void ShowPage(string tag)
    {
        if (_isClosed)
        {
            return;
        }

        if (_cancelHotkeyEditor is { } cancelEditor)
        {
            _pageRequestedAfterHotkeyEditor = tag;
            cancelEditor();
            return;
        }

        ViewModel.PrepareGroupsForPresentation();
        NavigationViewItem? item = FindNavigationItem(tag);
        if (item is not null)
        {
            RootNavigation.SelectedItem = item;
        }

        AppWindow.Show();
        Activate();
        SideyWindowActivation.BringToForeground(this);
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

    public void SetTrayAvailable(bool available)
    {
        if (!_isClosed)
        {
            _trayAvailable = available;
        }
    }

    public void ShowUpdates()
    {
        if (_isClosed)
        {
            return;
        }

        if (_cancelHotkeyEditor is not null)
        {
            _scrollToUpdatesAfterHotkeyEditor = true;
            ShowPage("about");
            return;
        }

        ShowPage("about");
        SetUpdateSectionExpanded(true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
            {
                return;
            }

            AboutPage.UpdateLayout();
            double offset = UpdateSection.TransformToVisual(AboutPageContent)
                .TransformPoint(new Windows.Foundation.Point()).Y;
            AboutPage.ChangeView(
                null,
                Math.Clamp(offset, 0, AboutPage.ScrollableHeight),
                null,
                disableAnimation: true);
        });
    }

    private void OnUpdateSectionHeaderTapped(object sender, TappedRoutedEventArgs args)
    {
        for (var source = args.OriginalSource as DependencyObject;
            source is not null && !ReferenceEquals(source, sender);
            source = VisualTreeHelper.GetParent(source))
        {
            if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                return;
        }

        SetUpdateSectionExpanded(UpdateSectionToggle.IsChecked != true);
    }

    private void OnUpdateSectionToggleClick(object sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        SetUpdateSectionExpanded(UpdateSectionToggle.IsChecked == true);
    }

    private void SetUpdateSectionExpanded(bool expanded)
    {
        UpdateSectionToggle.IsChecked = expanded;
        UpdateSectionDetails.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SetChevronAngle(UpdateSectionChevron, expanded ? 180 : 0);
    }

    public async Task<bool> ConfirmInviteCodeRotationAsync()
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("groups.invite.rotate.confirmation.title"),
            Content = I18n.Get("groups.invite.rotate.confirmation.message"),
            PrimaryButtonText = I18n.Get("groups.invite.rotate.confirmation.action"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    private async void OnStorePreviewRequested(StoreProductPreviewViewModel product)
    {
        if (_isClosed || _storePreviewDialogOpen)
        {
            return;
        }

        _storePreviewDialogOpen = true;
        StorePreviewStage? previewStage = null;
        try
        {
            previewStage = new StorePreviewStage(
                product.Kind,
                product.CatalogItemId,
                product.CharacterId,
                _lifetime.Token);
            _activePreview = previewStage;
            previewStage.SetAnimationsEnabled(_coordinator.AnimationsEnabled);
            previewStage.CharacterImpact += _coordinator.PlayImpactSound;
            previewStage.StopSounds += scope => _coordinator.StopImpactSounds(scope);
            if (ActiveXamlRoot() is not { } xamlRoot)
            {
                return;
            }

            ContentDialog dialog = CreateStorePreviewDialog(product, previewStage, xamlRoot);
            dialog.Closing += (_, _) => previewStage.EndPresentation();
            previewStage.BeginPresentation();
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("store-preview-dialog", exception);
            if (!_isClosed)
            {
                ViewModel.ReportError(exception);
            }
        }
        finally
        {
            previewStage?.EndPresentation();
            _activePreview = null;
            _storePreviewDialogOpen = false;
        }
    }

    internal static ContentDialog CreateStorePreviewDialog(
        StoreProductPreviewViewModel? product, StorePreviewStage previewStage, XamlRoot xamlRoot)
    {
        StackPanel content = product is null ? new StackPanel() : CreateStorePreviewContent(product, previewStage);
        if (product is null)
        {
            content.Children.Add(new Viewbox { Child = previewStage, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        }
        var scroll = new ScrollViewer
        {
            Content = content,
            Width = 540,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Content = scroll,
            RequestedTheme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            CloseButtonText = I18n.Get("common.close"),
            DefaultButton = ContentDialogButton.Close,
        };
        // WinUI's default dialog content width is narrower than the preview stage.
        dialog.Resources["ContentDialogMaxWidth"] = 620d;
        void UpdateViewport(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            scroll.MaxWidth = Math.Max(1, sender.Size.Width - 80);
            scroll.MaxHeight = Math.Max(1, sender.Size.Height - 160);
        }
        scroll.MaxWidth = Math.Max(1, xamlRoot.Size.Width - 80);
        scroll.MaxHeight = Math.Max(1, xamlRoot.Size.Height - 160);
        dialog.Opened += (_, _) => xamlRoot.Changed += UpdateViewport;
        dialog.Closed += (_, _) => xamlRoot.Changed -= UpdateViewport;
        return dialog;
    }

    internal static StackPanel CreateStorePreviewContent(StoreProductPreviewViewModel product, StorePreviewStage previewStage)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = product.DisplayName,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new Viewbox
        {
            Child = previewStage,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var cards = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.Children.Add(CreateStoreDetailCard(product, isKeepsake: product.IsKeepsake));
        if (product.RelatedKeepsake is { } keepsake)
        {
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            Border keepsakeCard = CreateStoreDetailCard(keepsake, isKeepsake: true);
            Grid.SetColumn(keepsakeCard, 1);
            cards.Children.Add(keepsakeCard);
        }
        content.Children.Add(cards);
        return content;
    }

    private static Border CreateStoreDetailCard(StoreProductPreviewViewModel product, bool isKeepsake)
    {
        var content = new Grid { RowSpacing = 12 };
        foreach (GridLength height in new[] { GridLength.Auto, new GridLength(88), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
        {
            content.RowDefinitions.Add(new RowDefinition { Height = height });
        }
        var header = new TextBlock
        {
            Text = I18n.Get(isKeepsake ? "store.kind.keepsake" : product.Kind switch
            {
                CommerceProductKind.Character => "profile.character.label",
                CommerceProductKind.Bubble => "profile.cosmetics.bubble.title",
                _ => "profile.cosmetics.throwable.title",
            }),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
        };
        content.Children.Add(header);
        var artwork = new StoreProductArtwork
        {
            ProductKind = product.Kind,
            CatalogItemId = product.CatalogItemId,
            CharacterId = product.CharacterId,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var artworkSurface = new Border
        {
            Style = (Style)Application.Current.Resources["SideyStoreArtworkSurfaceStyle"],
            Child = artwork,
        };
        Grid.SetRow(artworkSurface, 1);
        content.Children.Add(artworkSurface);
        foreach ((string property, int row) in new[] { (nameof(product.DisplayName), 2), (nameof(product.Description), 3) })
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = property == nameof(product.DisplayName) ? 14 : 12,
                FontWeight = property == nameof(product.DisplayName)
                    ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            text.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
            {
                Source = product,
                Path = new PropertyPath(property),
                Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
            });
            Grid.SetRow(text, row);
            content.Children.Add(text);
        }
        var footer = new StackPanel { Spacing = 6 };
        var price = new TextBlock { TextAlignment = TextAlignment.Center };
        price.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = product,
            Path = new PropertyPath(nameof(product.FormattedPrice)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
        });
        footer.Children.Add(price);
        if (isKeepsake)
        {
            footer.Children.Add(new TextBlock
            {
                Text = I18n.Get("store.product.sold_separately"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 12,
                Style = (Style)Application.Current.Resources["SideyStoreSecondaryTextStyle"],
            });
        }
        var purchase = new Button
        {
            Command = product.ActionCommand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(purchase, "StorePurchase_" + product.ProductId);
        purchase.SetBinding(ContentControl.ContentProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = product,
            Path = new PropertyPath(nameof(product.DetailStatusText)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
        });
        purchase.SetBinding(Control.IsEnabledProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = product,
            Path = new PropertyPath(nameof(product.IsActionEnabled)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
        });
        footer.Children.Add(purchase);
        Grid.SetRow(footer, 4);
        content.Children.Add(footer);
        return new Border
        {
            Style = (Style)Application.Current.Resources["SideySettingsCardStyle"],
            Padding = new Thickness(16),
            Child = content,
        };
    }

    public async Task<string?> PromptForRoomNameAsync(string currentName)
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
        {
            return null;
        }

        var input = new TextBox
        {
            Text = currentName,
            MaxLength = 20,
            PlaceholderText = I18n.Get("groups.rename.name_placeholder"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("groups.rename.title"),
            Content = input,
            PrimaryButtonText = I18n.Get("common.save"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? input.Text
                : null;
        }
        catch (Exception) when (_isClosed)
        {
            return null;
        }
    }

    public async Task<bool> ConfirmMemberRemovalAsync(string nickname)
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Format("groups.member.remove.confirmation.title", nickname),
            Content = I18n.Format("groups.member.remove.confirmation.message", nickname),
            PrimaryButtonText = I18n.Get("groups.member.remove.action"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    public async Task<bool> ConfirmRoomLeaveAsync(string roomName, bool isOwner)
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Format("groups.leave.confirmation.title", roomName),
            Content = I18n.Get(isOwner
                ? "groups.leave.confirmation.owner_message"
                : "groups.leave.confirmation.member_message"),
            PrimaryButtonText = I18n.Get("groups.leave.confirmation.action"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    public async Task<bool> ConfirmRoomDeletionAsync(string roomName)
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
        {
            return false;
        }

        var impactDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Format("groups.delete.confirmation.title", roomName),
            Content = I18n.Get("groups.delete.confirmation.message"),
            PrimaryButtonText = I18n.Get("common.continue"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult impactResult;
        try
        {
            impactResult = await impactDialog.ShowAsync();
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
        if (impactResult != ContentDialogResult.Primary || _isClosed)
        {
            return false;
        }

        var finalDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("groups.delete.final_confirmation.title"),
            Content = I18n.Format("groups.delete.final_confirmation.message", roomName),
            PrimaryButtonText = I18n.Get("common.delete"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await finalDialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    public async Task<bool> ConfirmSignOutAsync()
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
            return false;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("account.sign_out.confirmation.title"),
            Content = I18n.Get("account.sign_out.confirmation.message"),
            PrimaryButtonText = I18n.Get("account.sign_out.confirmation.action"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    public async Task<bool> ConfirmAccountDeletionAsync()
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
            return false;

        var impactDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("account.delete.confirmation.title"),
            Content = I18n.Get("account.delete.confirmation.message"),
            PrimaryButtonText = I18n.Get("common.continue"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await impactDialog.ShowAsync() != ContentDialogResult.Primary)
                return false;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }

        var finalDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("account.delete.final_confirmation.title"),
            Content = I18n.Get("account.delete.final_confirmation.message"),
            PrimaryButtonText = I18n.Get("account.delete.confirmation.action"),
            CloseButtonText = I18n.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await finalDialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    public async Task<bool> ConfirmUpdateDownloadAsync(string version)
    {
        if (ActiveXamlRoot() is not { } xamlRoot)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = I18n.Get("update.prompt.title"),
            Content = I18n.Format("update.prompt.message", version),
            PrimaryButtonText = I18n.Get("update.prompt.download"),
            CloseButtonText = I18n.Get("update.prompt.later"),
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception) when (_isClosed)
        {
            return false;
        }
    }

    private XamlRoot? ActiveXamlRoot() => _isClosed ? null : Content.XamlRoot;

    private void OnAppWindowClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        _ = sender;
        ViewModel.StopSoundVolumeFeedback();
        if (_allowClose || !_trayAvailable)
        {
            PrepareForClose();
            return;
        }

        args.Cancel = true;
        if (_hideQueued)
        {
            return;
        }

        _hideQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _hideQueued = false;
            if (!_allowClose && !_isClosed)
            {
                AppWindow.Hide();
            }
        });
    }

    private void PrepareForClose()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        ViewModel.StopSoundVolumeFeedback();
        _lifetime.Cancel();
        MainRoot.DataContext = null;
    }

    private void OnRootNavigationPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not NavigationView navigation
            || args.Key != VirtualKey.Tab
            || args.OriginalSource is not DependencyObject source)
        {
            return;
        }

        NavigationViewItem? currentItem = null;
        for (DependencyObject? ancestor = source;
             ancestor is not null && !ReferenceEquals(ancestor, navigation);
             ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is NavigationViewItem item)
            {
                currentItem = item;
                break;
            }
        }

        NavigationViewItem[] items =
        [
            .. navigation.MenuItems.OfType<NavigationViewItem>(),
            .. navigation.FooterMenuItems.OfType<NavigationViewItem>(),
        ];
        int currentIndex = Array.IndexOf(items, currentItem);
        int nextIndex = currentIndex + (IsKeyDown(VirtualKey.Shift) ? -1 : 1);
        if (currentIndex >= 0
            && nextIndex >= 0
            && nextIndex < items.Length
            && items[nextIndex].Focus(FocusState.Keyboard))
        {
            args.Handled = true;
        }
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        _ = sender;
        string tag = (args.SelectedItemContainer?.Tag as string) ?? "profile";
        if (tag != "settings")
            ViewModel.StopSoundVolumeFeedback();
        bool navigationChanged = !StringComparer.Ordinal.Equals(tag, _currentNavigationTag);
        if (navigationChanged)
        {
            if (!_navigatingBack)
            {
                _navigationHistory.Push(_currentNavigationTag);
            }

            _currentNavigationTag = tag;
        }

        AppTitleBar.IsBackButtonEnabled = _navigationHistory.Count > 0;
        if (tag == "groups")
        {
            ViewModel.PrepareGroupsForPresentation();
        }

        HomePage.Visibility = tag == "profile" ? Visibility.Visible : Visibility.Collapsed;
        GroupsPage.Visibility = tag == "groups" ? Visibility.Visible : Visibility.Collapsed;
        StorePage.Visibility = tag == "store" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (navigationChanged)
        {
            ScrollViewer selectedPage = tag switch
            {
                "groups" => GroupsPage,
                "store" => StorePage,
                "settings" => SettingsPage,
                "about" => AboutPage,
                _ => HomePage,
            };
            selectedPage.ChangeView(null, 0, null, disableAnimation: true);
            DispatcherQueue.TryEnqueue(() =>
                selectedPage.ChangeView(null, 0, null, disableAnimation: true));
            AnimatePageRefresh(selectedPage);
        }
    }

    private void OnStoreKindChipChecked(object sender, RoutedEventArgs args)
    {
        _ = args;
        if (sender is not RadioButton { Tag: string tag }
            || !int.TryParse(tag, out int selectedIndex)
            || MainRoot.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        int previousIndex = viewModel.SelectedStoreKindIndex;
        ApplyStoreKindChipStyles(selectedIndex);
        if (selectedIndex < 0 || selectedIndex == previousIndex)
        {
            return;
        }

        viewModel.SelectedStoreKindIndex = selectedIndex;
        AnimateSiblingPage(
            StoreResultsHost,
            selectedIndex > previousIndex ? 24d : -24d);
    }

    private void ApplyStoreKindChipStyles(int selectedIndex)
    {
        if (StoreCharacterKindChip is null
            || StoreBubbleKindChip is null
            || StoreThrowableKindChip is null)
        {
            return;
        }

        var defaultStyle = (Style)Application.Current.Resources["SideyStoreKindChipStyle"];
        var selectedStyle = (Style)Application.Current.Resources["SideyStoreKindChipSelectedStyle"];
        RadioButton[] chips =
        [
            StoreCharacterKindChip,
            StoreBubbleKindChip,
            StoreThrowableKindChip,
        ];
        for (int index = 0; index < chips.Length; index++)
        {
            chips[index].Style = index == selectedIndex ? selectedStyle : defaultStyle;
        }
    }

    private static void AnimatePageRefresh(FrameworkElement element) =>
        AnimateElement(element, horizontalOffset: 0, verticalOffset: 16, durationMilliseconds: 250);

    private static void AnimateSiblingPage(FrameworkElement element, double horizontalOffset) =>
        AnimateElement(element, horizontalOffset, verticalOffset: 0, durationMilliseconds: 167);

    private async void OnRoomHeaderClick(object sender, RoutedEventArgs args)
    {
        _ = args;
        if (sender is not Button header
            || header.DataContext is not RoomCardViewModel room
            || header.Tag is not FrameworkElement body
            || !_roomExpansionAnimations.Add(room.Room.Id))
        {
            return;
        }

        var headerLayout = VisualTreeHelper.GetParent(header) as Grid;
        FontIcon? chevron = headerLayout is null
            ? null
            : FindNamedDescendant<FontIcon>(headerLayout, "RoomExpansionChevron");
        try
        {
            if (!_coordinator.AnimationsEnabled)
            {
                room.ToggleCommand.Execute(null);
                SetChevronAngle(chevron, room.IsExpanded ? 180 : 0);
                return;
            }

            if (room.IsExpanded)
            {
                await Task.WhenAll(
                    AnimateRoomBodyAsync(body, expanding: false),
                    AnimateChevronAsync(chevron, expanding: false));
                room.ToggleCommand.Execute(null);
                body.Height = double.NaN;
                body.Opacity = 1;
            }
            else
            {
                room.ToggleCommand.Execute(null);
                body.Height = double.NaN;
                body.UpdateLayout();
                await Task.WhenAll(
                    AnimateRoomBodyAsync(body, expanding: true),
                    AnimateChevronAsync(chevron, expanding: true));
                body.Height = double.NaN;
                body.Opacity = 1;
            }
        }
        finally
        {
            _roomExpansionAnimations.Remove(room.Room.Id);
        }
    }

    private void OnRoomExpansionChevronLoaded(object sender, RoutedEventArgs args)
    {
        _ = args;
        if (sender is FontIcon chevron && chevron.DataContext is RoomCardViewModel room)
        {
            SetChevronAngle(chevron, room.IsExpanded ? 180 : 0);
        }
    }

    private void OnRoomHeaderPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _ = args;
        SetRoomHeaderHoverOpacity(sender, 1);
    }

    private void OnRoomHeaderPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _ = args;
        SetRoomHeaderHoverOpacity(sender, 0);
    }

    private static void SetRoomHeaderHoverOpacity(object sender, double opacity)
    {
        if (sender is Grid header
            && header.Children
                .OfType<Border>()
                .FirstOrDefault(child => child.Name == "RoomHeaderHoverBackground") is { } hoverBackground)
        {
            hoverBackground.Opacity = opacity;
        }
    }

    private static T? FindNamedDescendant<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T element && StringComparer.Ordinal.Equals(element.Name, name))
            {
                return element;
            }

            if (FindNamedDescendant<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static Task AnimateChevronAsync(FontIcon? chevron, bool expanding)
    {
        if (chevron is null)
        {
            return Task.CompletedTask;
        }

        SetChevronAngle(chevron, expanding ? 0 : 180);
        var angle = new DoubleAnimation
        {
            From = expanding ? 0 : 180,
            To = expanding ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(167),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(angle, chevron);
        Storyboard.SetTargetProperty(
            angle,
            "(UIElement.RenderTransform).(RotateTransform.Angle)");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storyboard = new Storyboard();
        storyboard.Children.Add(angle);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private static void SetChevronAngle(FontIcon? chevron, double angle)
    {
        if (chevron?.RenderTransform is RotateTransform transform)
        {
            transform.Angle = angle;
        }
    }

    private static Task AnimateRoomBodyAsync(FrameworkElement body, bool expanding)
    {
        double expandedHeight = Math.Max(1, body.ActualHeight);
        body.Height = expanding ? 0 : expandedHeight;
        body.Opacity = expanding ? 0 : 1;
        if (body.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            body.RenderTransform = transform;
        }
        transform.Y = expanding ? -8 : 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(167));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation
        {
            From = expanding ? 0 : expandedHeight,
            To = expanding ? expandedHeight : 0,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(height, body);
        Storyboard.SetTargetProperty(height, "Height");

        var opacity = new DoubleAnimation
        {
            From = expanding ? 0 : 1,
            To = expanding ? 1 : 0,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(opacity, body);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var translation = new DoubleAnimation
        {
            From = expanding ? -8 : 0,
            To = expanding ? 0 : -8,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(translation, body);
        Storyboard.SetTargetProperty(
            translation,
            "(UIElement.RenderTransform).(TranslateTransform.Y)");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storyboard = new Storyboard();
        storyboard.Children.Add(height);
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translation);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private static void AnimateElement(
        FrameworkElement element,
        double horizontalOffset,
        double verticalOffset,
        int durationMilliseconds)
    {
        if (!new UISettings().AnimationsEnabled)
        {
            element.Opacity = 1;
            element.RenderTransform = new TranslateTransform();
            return;
        }

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        element.Opacity = 1;

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var translation = new DoubleAnimation
        {
            From = horizontalOffset == 0 ? verticalOffset : horizontalOffset,
            To = 0,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(translation, element);
        Storyboard.SetTargetProperty(
            translation,
            horizontalOffset == 0
                ? "(UIElement.RenderTransform).(TranslateTransform.Y)"
                : "(UIElement.RenderTransform).(TranslateTransform.X)");

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translation);
        storyboard.Begin();
    }

    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        _ = sender;
        _ = args;
        if (_navigationHistory.Count == 0)
        {
            return;
        }

        string tag = _navigationHistory.Pop();
        NavigationViewItem? item = FindNavigationItem(tag);
        if (item is null)
        {
            AppTitleBar.IsBackButtonEnabled = _navigationHistory.Count > 0;
            return;
        }

        _navigatingBack = true;
        try
        {
            RootNavigation.SelectedItem = item;
        }
        finally
        {
            _navigatingBack = false;
        }
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args)
    {
        _ = sender;
        _ = args;
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
    }

    private NavigationViewItem? FindNavigationItem(string tag) =>
        RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Tag as string, tag));

    private void OnStoreFilterToggleClick(object sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        if (!_storeFilterTransition.IsCompleted)
        {
            bool isExpanded = StoreFilterPanel.Visibility == Visibility.Visible;
            StoreFilterToggle.IsChecked = isExpanded;
            ApplyStoreFilterToggleSurface(isExpanded);
            return;
        }

        bool requestedExpanded = StoreFilterToggle.IsChecked == true;
        ApplyStoreFilterToggleSurface(requestedExpanded);
        _storeFilterTransition = TransitionStoreFilterPanelAsync(requestedExpanded);
    }

    private void OnStoreSearchTextChanged(object sender, TextChangedEventArgs args)
    {
        _ = args;
        if (sender is not TextBox)
        {
            return;
        }

        QueueStoreSearchUpdate();
    }

    private void QueueStoreSearchUpdate()
    {
        if (_storeSearchUpdateQueued)
        {
            return;
        }

        _storeSearchUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _storeSearchUpdateQueued = false;
                if (!_isClosed)
                {
                    ViewModel.StoreSearchText = StoreSearchTextBox.Text;
                }
            }))
        {
            _storeSearchUpdateQueued = false;
        }
    }

    private void OnResetStoreFiltersClick(object sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        ViewModel.ResetStoreFiltersCommand.Execute(null);
        StoreSearchTextBox.Text = string.Empty;
    }

    private async Task TransitionStoreFilterPanelAsync(bool isExpanded)
    {
        StoreFilterToggle.IsEnabled = false;
        try
        {
            if (!isExpanded)
            {
                // Let TextBox focus and IME composition finish before hiding its visual tree.
                StoreFilterToggle.Focus(FocusState.Programmatic);
                await WaitForDispatcherTurnAsync();
            }

            if (!_coordinator.AnimationsEnabled)
            {
                SetStoreFilterPanelExpanded(isExpanded);
                return;
            }

            if (isExpanded)
            {
                StoreFilterPanel.Visibility = Visibility.Visible;
                StoreFilterPanel.Height = double.NaN;
                StoreFilterPanel.UpdateLayout();
            }

            await Task.WhenAll(
                AnimateFilterPanelAsync(StoreFilterPanel, isExpanded),
                AnimateChevronAsync(StoreFilterChevron, isExpanded));
            SetStoreFilterPanelExpanded(isExpanded);
        }
        finally
        {
            StoreFilterToggle.IsEnabled = true;
        }
    }

    private static Task AnimateFilterPanelAsync(FrameworkElement panel, bool expanding)
    {
        double expandedHeight = Math.Max(1, panel.ActualHeight);
        panel.Height = expanding ? 0 : expandedHeight;
        panel.Opacity = expanding ? 0 : 1;
        if (panel.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            panel.RenderTransform = transform;
        }
        transform.Y = expanding ? -8 : 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(167));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation
        {
            From = expanding ? 0 : expandedHeight,
            To = expanding ? expandedHeight : 0,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(height, panel);
        Storyboard.SetTargetProperty(height, "Height");

        var opacity = new DoubleAnimation
        {
            From = expanding ? 0 : 1,
            To = expanding ? 1 : 0,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(opacity, panel);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var translation = new DoubleAnimation
        {
            From = expanding ? -8 : 0,
            To = expanding ? 0 : -8,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(translation, panel);
        Storyboard.SetTargetProperty(
            translation,
            "(UIElement.RenderTransform).(TranslateTransform.Y)");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storyboard = new Storyboard();
        storyboard.Children.Add(height);
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translation);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private void SetStoreFilterPanelExpanded(bool isExpanded)
    {
        StoreFilterToggle.IsChecked = isExpanded;
        ApplyStoreFilterToggleSurface(isExpanded);
        StoreFilterPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        StoreFilterPanel.Height = double.NaN;
        StoreFilterPanel.Opacity = 1;
        if (StoreFilterPanel.RenderTransform is TranslateTransform transform)
        {
            transform.Y = 0;
        }
        SetChevronAngle(StoreFilterChevron, isExpanded ? 180 : 0);
    }

    private void ApplyStoreFilterToggleSurface(bool isExpanded)
    {
        string styleKey = isExpanded
            ? "SideyStoreFilterToggleExpandedStyle"
            : "SideyStoreFilterToggleStyle";
        StoreFilterToggle.Style = (Style)Application.Current.Resources[styleKey];
    }

    private async Task WaitForDispatcherTurnAsync()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => completion.TrySetResult()))
        {
            throw new InvalidOperationException("The UI dispatcher is unavailable.");
        }

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private void OnNoticeRaised(NoticeMessage notice)
    {
        if (_isClosed)
        {
            return;
        }

        _statusDismissTimer.Stop();
        StatusInfoBar.Message = notice.Message;
        StatusInfoBar.Severity = notice.Kind switch
        {
            NoticeKind.Success => InfoBarSeverity.Success,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        StatusInfoBar.IsOpen = true;
        if (notice.Kind is NoticeKind.Success or NoticeKind.Informational)
        {
            _statusDismissTimer.Start();
        }
    }

    private void OnStatusDismissTimerTick(object? sender, object args)
    {
        _ = sender;
        _ = args;
        _statusDismissTimer.Stop();
        if (!_isClosed)
        {
            StatusInfoBar.IsOpen = false;
        }
    }

    private void ApplyBackdrop()
    {
        SideyWindowTheme.ApplyBackdrop(this, MainFallbackBackground);
    }

    private ResponsiveWindowSize ApplyResponsiveSize()
    {
        WindowsMonitorInfo monitor = WindowsMonitorService.Select(identifier: null);
        ResponsiveWindowSize size = ResponsiveWindowSizePolicy.Calculate(
            monitor,
            SideyWindowKind.Settings);
        ResponsiveWindowSize minimumWindowSize = ResponsiveWindowSizePolicy.Minimum(
            monitor,
            SideyWindowKind.Settings);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, size.Height));
        AppWindow.Move(new Windows.Graphics.PointInt32(
            monitor.WorkAreaPixels.X + ((monitor.WorkAreaPixels.Width - size.Width) / 2),
            monitor.WorkAreaPixels.Y + ((monitor.WorkAreaPixels.Height - size.Height) / 2)));
        return minimumWindowSize;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _ = sender;
        _ = args;
        SetHotkeyRecordingActive(false);
        PrepareForClose();
        AppWindow.Closing -= OnAppWindowClosing;
        Closed -= OnWindowClosed;
        ViewModel.NoticeRaised -= OnNoticeRaised;
        ViewModel.StorePreviewRequested -= OnStorePreviewRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        MainRoot.Loaded -= OnResponsiveRootLoaded;
        _minimumSizeController.Dispose();
        _feedbackMonitor?.Dispose();
        if (_coordinator is AppCoordinator appCoordinator)
            appCoordinator.AnimationsChanged -= OnAnimationsChanged;
        _activePreview?.EndPresentation();
        _statusDismissTimer.Stop();
        _statusDismissTimer.Tick -= OnStatusDismissTimerTick;
#if DEBUG
        _validationMetricsTimer.Stop();
        _validationMetricsTimer.Tick -= OnValidationMetricsTimerTick;
#endif
        _lifetime.Dispose();
    }

#if DEBUG
    private void OnValidationMetricsTimerTick(object? sender, object args)
    {
        _ = sender;
        _ = args;
        ViewModel.RefreshDiagnostics();
    }
#endif
}
