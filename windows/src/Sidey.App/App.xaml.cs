using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Platform.Windows;
using Sidey.Platform.Windows.Overlay;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.App;

public partial class App : Application
{
    private static readonly TimeSpan s_connectionFailureNotificationDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_connectionFailureNotificationCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_displayTopologyRefreshDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_characterClickDelay = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly WindowsUpdateServiceAdapter _updateService;
    private readonly WindowsUpdateCompletionTracker _updateCompletionTracker;
    private readonly WindowsPostInstallLaunchTracker _postInstallLaunchTracker;
    private Window? _window;
    private MainWindow? _mainWindow;
    private OnboardingWindow? _onboardingWindow;
    private HistoryWindow? _historyWindow;
    private ComposerWindow? _composer;
    private ComposerViewModel? _historyComposer;
    private readonly ComposerTypingOwner _typingOwner = new();
    private readonly CharacterClickComposerState _characterClickComposerState = new();
    private DispatcherQueueTimer? _characterClickTimer;
    private DispatcherQueueTimer? _characterClickTopmostTimer;
    private bool _characterClickFeedbackInProgress;
    private Task _pendingComposerPlacementSave = Task.CompletedTask;
    private AppCoordinator? _coordinator;
    private SingleInstanceGuard? _singleInstance;
    private TrayIconService? _tray;
#if DEBUG
    private DevelopmentUpdateService? _developmentUpdate;
#endif
    private bool _startupUpdateCheckStarted;
    private bool _trayUpdateCheckInProgress;
    private bool _monitorConnectionFailures;
    private bool _connectionFailureNotificationArmed = true;
    private DateTimeOffset? _lastConnectionFailureNotificationAt;
    private DispatcherQueueTimer? _connectionFailureNotificationTimer;
    private DispatcherQueueTimer? _displayTopologyRefreshTimer;
    private string? _pendingUpdateNotificationVersion;
    private string? _pendingReadyToInstallNotificationVersion;
    private Timer? _uiResponsivenessTimer;
    private bool _shuttingDown;
    private bool _launchPresentationReady;
    private bool _showAboutAfterOnboarding;

    public App()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _updateService = new WindowsUpdateServiceAdapter();
        _updateCompletionTracker = new WindowsUpdateCompletionTracker();
        _postInstallLaunchTracker = new WindowsPostInstallLaunchTracker();
        StartupDiagnostics.BeginSession();
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            InitializeComponent();
            UnhandledException += OnXamlUnhandledException;
            StartupDiagnostics.Stage("xaml-initialized");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Fatal("app-xaml-initialization", exception, showDialog: true);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await LaunchAsync(args);
        }
        catch (Exception) when (_shuttingDown)
        {
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Fatal("launch", exception, showDialog: _mainWindow is null);
            if (_mainWindow is not null)
            {
                _mainWindow.ShowFatalError(new InvalidOperationException(
                    I18n.Get("app.launch.failed"),
                    exception));
                return;
            }

            await DisposeAfterFailedLaunchAsync();
            Exit();
        }
    }

    private async Task LaunchAsync(LaunchActivatedEventArgs args)
    {
        string? processArguments = WindowsLaunchArguments.Resolve(
            activationArguments: null,
            Environment.GetCommandLineArgs());
        bool backgroundLaunch = WindowsStartupService.IsBackgroundLaunch(args.Arguments)
            || WindowsStartupService.IsBackgroundLaunch(processArguments);
        bool updateShutdown = WindowsStartupService.IsUpdateShutdown(args.Arguments)
            || WindowsStartupService.IsUpdateShutdown(processArguments);
        bool showAboutAfterInstall = WindowsStartupService.IsShowAboutAfterInstall(args.Arguments)
            || WindowsStartupService.IsShowAboutAfterInstall(processArguments)
            || _postInstallLaunchTracker.IsPending();
        StartupDiagnostics.Stage("launch-entered");
        StartupDiagnostics.Stage(
            $"launch-mode background={backgroundLaunch} updateShutdown={updateShutdown} postInstall={showAboutAfterInstall}");
        _singleInstance = SingleInstanceGuard.Acquire(
            Environment.GetEnvironmentVariable(WindowsVersionGuard.StartupSmokeEnvironmentVariable) == "1"
                ? Environment.GetEnvironmentVariable("SIDEY_STARTUP_SMOKE_DATA_ROOT") : null);
        if (!_singleInstance.IsPrimary)
        {
            // A second Windows Run-key launch must not raise an existing instance.
            bool delivered = backgroundLaunch
                ? false
                : _singleInstance.Signal(processArguments);
            StartupDiagnostics.Stage(
                $"secondary-instance-request request=activate delivered={delivered.ToString().ToLowerInvariant()}");
            _singleInstance.Dispose();
            _singleInstance = null;
            StartupDiagnostics.CompleteSession();
            Exit();
            return;
        }
        StartupDiagnostics.Stage("single-instance-acquired");

        if (updateShutdown)
        {
            StartupDiagnostics.Stage("update-shutdown-no-primary");
            _singleInstance.Dispose();
            _singleInstance = null;
            StartupDiagnostics.CompleteSession();
            Exit();
            return;
        }

        if (!WindowsVersionGuard.CanLaunchMainWindow())
        {
            _window = new UnsupportedWindowsWindow();
            _window.Closed += OnWindowClosed;
            _window.Activate();
            StartupDiagnostics.Stage("unsupported-window-activated");
            StartupDiagnostics.MarkRunning();
            StartUiResponsivenessMonitor();
            return;
        }

        var coordinator = new AppCoordinator(dispatchTypingFeedback: action =>
        {
            if (_dispatcherQueue.HasThreadAccess)
                action();
            else
                _dispatcherQueue.TryEnqueue(() => action());
        });
        _coordinator = coordinator;
        await coordinator.LoadCachedStateAsync();
        if (_shuttingDown || !ReferenceEquals(_coordinator, coordinator))
        {
            return;
        }

        StartupDiagnostics.Stage("cached-settings-loaded");
        I18n.SetLanguage(coordinator.State.Preferences.Language);
        StartupDiagnostics.Stage(
            $"language-initialized language={I18n.Language} "
            + $"saved={(coordinator.State.Preferences.Language is not null).ToString().ToLowerInvariant()}");
        string? completedUpdateVersion = _updateCompletionTracker.PendingNotificationVersion(
            _updateService.CurrentUpdateVersion);
        coordinator.ComposerRequested += RequestComposer;
        coordinator.CharacterClicked += RequestCharacterClick;
        coordinator.PulseRequested += RequestPulse;
        coordinator.TreeMovementToggleRequested += RequestTreeMovementToggle;
        coordinator.CharacterThrowRequested += RequestCharacterThrow;
        coordinator.RenderingFailed += OnRenderingFailed;
        coordinator.GroupSetupRequested += OnGroupSetupRequested;
        coordinator.StateChanged += OnCoordinatorStateChanged;
        coordinator.LanguageChanged += OnLanguageChanged;
        coordinator.ShowStartupOverlay();
        if (Environment.GetEnvironmentVariable(WindowsVersionGuard.StartupSmokeEnvironmentVariable) == "1")
        {
            _startupUpdateCheckStarted = true;
        }
        _singleInstance!.StartListening(RequestPrimaryActivation);
        try
        {
            _tray = TrayIconService.Start(coordinator.State.Preferences.GlobalHotkeys);
            _tray.CommandInvoked += OnTrayCommandInvoked;
            _tray.HotkeyInvoked += OnTrayHotkeyInvoked;
            _tray.RoomSelected += OnTrayRoomSelected;
            _tray.DisplayTopologyChanged += OnDisplayTopologyChanged;
            _mainWindow?.SetTrayAvailable(true);
            StartupDiagnostics.Stage("tray-started");
            if (completedUpdateVersion is not null)
            {
                _tray.NotifyUpdateInstalled(_updateService.CurrentVersion);
                StartupDiagnostics.Stage("update-completed-notification-posted");
            }
            if (_pendingUpdateNotificationVersion is { } pendingVersion)
            {
                PostUpdateNotification(pendingVersion);
            }
            if (_pendingReadyToInstallNotificationVersion is { } readyVersion)
            {
                PostUpdateReadyToInstallNotification(readyVersion);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("tray-start", exception);
            var error = new InvalidOperationException(I18n.Get("tray.start.failed"), exception);
            if (!backgroundLaunch)
                EnsureMainWindow().ShowFatalError(error);
        }
        if (completedUpdateVersion is null || _tray is not null)
        {
            bool markedLaunched = _updateCompletionTracker.TryMarkLaunched(
                _updateService.CurrentUpdateVersion);
            StartupDiagnostics.Stage(
                $"update-completion-state-saved result={markedLaunched.ToString().ToLowerInvariant()}");
        }
#if DEBUG
        _developmentUpdate = DevelopmentUpdateService.Start(OnDevelopmentUpdateAccepted);
#endif
        try
        {
            await coordinator.InitializeAsync();
            if (_shuttingDown || !ReferenceEquals(_coordinator, coordinator))
            {
                return;
            }

            StartupDiagnostics.Stage("coordinator-initialized");
#if SIDEY_DEVELOPMENT_COMMERCE
            if (coordinator.State.DevelopmentCommerceEnabled
                && coordinator.AuthCallbackScheme == WindowsAuthCallback.DevelopmentScheme
                && Environment.ProcessPath is { } executablePath)
            {
                WindowsProtocolRegistration.EnsureCurrentUserDevelopmentCallback(executablePath);
            }
#endif
            await TryHandleActivationRequestAsync(processArguments);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("coordinator-initialize", exception);
            if (_shuttingDown || !ReferenceEquals(_coordinator, coordinator))
            {
                return;
            }

            if (!backgroundLaunch)
                EnsureMainWindow().ShowFatalError(exception);
        }

        if (_shuttingDown || !ReferenceEquals(_coordinator, coordinator))
        {
            return;
        }

        _launchPresentationReady = true;
        if (showAboutAfterInstall)
            ShowPostInstallWindow();
        else if (!backgroundLaunch)
            ShowPrimaryWindow();
        else if (coordinator.State.NeedsOnboarding)
            ShowPrimaryWindow();
        else
            EnsureMainWindow();

        _monitorConnectionFailures = true;
        UpdateConnectionFailureNotification(coordinator.State.Connected);

        StartupDiagnostics.MarkRunning();
        StartUiResponsivenessMonitor();
        _ = _updateService.CleanupInstalledUpdatesAsync();
    }

    private void StartUiResponsivenessMonitor()
    {
        _uiResponsivenessTimer ??= new Timer(
            static state => ((App)state!).ProbeUiResponsiveness(),
            this,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    private void ProbeUiResponsiveness()
    {
        if (_shuttingDown)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        if (!_dispatcherQueue.TryEnqueue(() =>
            StartupDiagnostics.Stage(
                $"ui-heartbeat delay-ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}")))
        {
            StartupDiagnostics.Stage("ui-heartbeat dispatch=failed");
        }
    }

    private void StartStartupUpdateCheck()
    {
        if (_shuttingDown || _startupUpdateCheckStarted || _mainWindow is null)
        {
            return;
        }

        _startupUpdateCheckStarted = true;
        _ = CheckForUpdatesOnStartupAsync(_mainWindow);
    }

    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow is not null)
        {
            return _mainWindow;
        }

        if (_coordinator is null)
        {
            throw new InvalidOperationException("The settings window requires an initialized coordinator.");
        }

        _mainWindow = new MainWindow(_coordinator, _updateService);
        _mainWindow.ViewModel.ManualUpdateFound += update =>
        {
            if (_updateService.Preferences.NotifyWhenAvailable)
                PostUpdateNotification(update.Version);
        };
        _mainWindow.ViewModel.ManualUpdateReady += update =>
            PostUpdateReadyToInstallNotification(update.Version);
        StartupDiagnostics.Stage("settings-window-created result=success");
        _mainWindow.Closed += OnMainWindowClosed;
        _mainWindow.HotkeyRecordingChanged += OnHotkeyRecordingChanged;
        _mainWindow.SetTrayAvailable(_tray is not null);
        StartStartupUpdateCheck();
        return _mainWindow;
    }

    private async Task CheckForUpdatesOnStartupAsync(MainWindow mainWindow)
    {
        try
        {
            StartupDiagnostics.Stage("startup-update-check-started");
            await mainWindow.ViewModel.CheckForUpdatesOnStartupAsync(
                onUpdateFound: update =>
                {
                    if (!_shuttingDown
                        && ReferenceEquals(_mainWindow, mainWindow)
                        && _updateService.Preferences.NotifyWhenAvailable)
                    {
                        PostUpdateNotification(update.Version);
                    }
                },
                onUpdateReady: update =>
                {
                    if (!_shuttingDown && ReferenceEquals(_mainWindow, mainWindow))
                    {
                        PostUpdateReadyToInstallNotification(update.Version);
                    }
                });
            StartupDiagnostics.Stage("startup-update-checked");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("startup-update-check", exception);
        }
    }

    private void PostUpdateNotification(string version)
    {
        if (!_updateService.Preferences.NotifyWhenAvailable)
        {
            _pendingUpdateNotificationVersion = null;
            return;
        }

        if (_shuttingDown)
        {
            return;
        }

        if (_tray is null)
        {
            _pendingUpdateNotificationVersion = version;
            return;
        }

        _pendingUpdateNotificationVersion = null;
        _tray.NotifyUpdateAvailable(version);
        StartupDiagnostics.Stage("update-available-notification-posted");
    }

    private void PostUpdateReadyToInstallNotification(string version)
    {
        if (_shuttingDown)
        {
            return;
        }

        if (_tray is null)
        {
            _pendingReadyToInstallNotificationVersion = version;
            return;
        }

        _pendingReadyToInstallNotificationVersion = null;
        _tray.NotifyUpdateReadyToInstall(version);
        StartupDiagnostics.Stage("update-ready-to-install-notification-posted");
    }

    private async Task CheckForUpdatesFromTrayAsync()
    {
        if (_shuttingDown || _trayUpdateCheckInProgress)
            return;

        _trayUpdateCheckInProgress = true;
        try
        {
            MainWindow window = EnsureMainWindow();
            if (window.ViewModel.IsCheckingForUpdates)
                return;
            AvailableUpdate? update = await window.ViewModel.CheckForUpdatesOnStartupAsync(
                onUpdateFound: found =>
                {
                    if (_updateService.Preferences.NotifyWhenAvailable)
                        PostUpdateNotification(found.Version);
                },
                onUpdateReady: ready => PostUpdateReadyToInstallNotification(ready.Version));
            if (update is not null && !_shuttingDown && ReferenceEquals(_mainWindow, window))
                window.ShowUpdates();
            if (update is null && !_shuttingDown)
                _tray?.NotifyLatestVersion();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("tray-update-check", exception);
            if (!_shuttingDown)
                _tray?.NotifyUpdateCheckFailed();
        }
        finally { _trayUpdateCheckInProgress = false; }
    }

    private async Task DisposeAfterFailedLaunchAsync()
    {
#if DEBUG
        _developmentUpdate?.Dispose();
        _developmentUpdate = null;
#endif
        try
        {
            _tray?.Dispose();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("failed-launch-tray-dispose", exception);
        }
        _tray = null;

        if (_coordinator is not null)
        {
            try
            {
                await _coordinator.DisposeAsync();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.NonFatal("failed-launch-coordinator-dispose", exception);
            }
            _coordinator = null;
        }

        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    private static void OnXamlUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        _ = sender;
        StartupDiagnostics.Fatal("xaml-unhandled", args.Exception, showDialog: true);
    }

    private static void OnDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args)
    {
        _ = sender;
        Exception exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException("A non-Exception object reached the unhandled exception boundary.");
        StartupDiagnostics.Fatal("app-domain-unhandled", exception, showDialog: true);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        _ = sender;
        StartupDiagnostics.NonFatal("unobserved-task", args.Exception);
        args.SetObserved();
    }

    private void RequestComposer()
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_shuttingDown)
            {
                CancelPendingCharacterClick();
                ShowComposer();
            }
        });
    }

    private void RequestCharacterClick(int clickCount)
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_shuttingDown)
            {
                return;
            }

            // Show single-click feedback after 100ms, then restore the original
            // visibility if Windows delivers a later native double-click.
            if (clickCount == 2)
            {
                ComposerVisibilityAction restoration = _characterClickComposerState.CompleteDoubleClick();
                CancelPendingCharacterClick();
                ApplyCharacterClickVisibility(restoration);
                return;
            }

            CancelPendingCharacterClick();
            if (clickCount != 1)
            {
                return;
            }

            _characterClickComposerState.BeginSingleClick(_composer?.IsVisible == true);
            DispatcherQueueTimer timer = _dispatcherQueue.CreateTimer();
            timer.Interval = s_characterClickDelay;
            timer.IsRepeating = false;
            timer.Tick += OnCharacterClickWindowElapsed;
            _characterClickTimer = timer;
            timer.Start();
        });
    }

    private void OnCharacterClickWindowElapsed(DispatcherQueueTimer sender, object args)
    {
        _ = args;
        sender.Tick -= OnCharacterClickWindowElapsed;
        sender.Stop();
        if (!ReferenceEquals(_characterClickTimer, sender))
        {
            return;
        }

        _characterClickTimer = null;
        ComposerVisibilityAction action = _characterClickComposerState.CompleteSingleClick();
        if (_shuttingDown)
        {
            return;
        }

        TimeSpan remainingDoubleClickWindow = TimeSpan.FromSeconds(NativeOverlayWindow.DoubleClickIntervalSeconds)
            + TimeSpan.FromMilliseconds(30) - s_characterClickDelay;
        bool keepHotspotAboveComposer = action == ComposerVisibilityAction.Show
            && remainingDoubleClickWindow > TimeSpan.Zero;
        _characterClickFeedbackInProgress = true;
        try
        {
            ApplyCharacterClickVisibility(action, alwaysOnTop: !keepHotspotAboveComposer, animateShow: true);
        }
        finally
        {
            _characterClickFeedbackInProgress = false;
        }

        if (keepHotspotAboveComposer && _composer?.IsVisible == true)
        {
            DispatcherQueueTimer topmostTimer = _dispatcherQueue.CreateTimer();
            topmostTimer.Interval = remainingDoubleClickWindow;
            topmostTimer.IsRepeating = false;
            topmostTimer.Tick += OnCharacterDoubleClickWindowElapsed;
            _characterClickTopmostTimer = topmostTimer;
            topmostTimer.Start();
        }
    }

    private void OnCharacterDoubleClickWindowElapsed(DispatcherQueueTimer sender, object args)
    {
        _ = args;
        sender.Tick -= OnCharacterDoubleClickWindowElapsed;
        sender.Stop();
        if (!ReferenceEquals(_characterClickTopmostTimer, sender))
        {
            return;
        }

        _characterClickTopmostTimer = null;
        _composer?.SetAlwaysOnTop(true);
        _characterClickComposerState.Reset();
    }

    private void ApplyCharacterClickVisibility(
        ComposerVisibilityAction action, bool alwaysOnTop = true, bool animateShow = false)
    {
        if (action == ComposerVisibilityAction.Show && _composer?.IsVisible != true)
        {
            ShowComposer(alwaysOnTop, animateShow);
        }
        else if (action == ComposerVisibilityAction.Hide && _composer?.IsVisible == true)
        {
            _composer.HideComposer();
        }
    }

    private void CancelPendingCharacterClick()
    {
        _characterClickComposerState.Reset();
        if (_characterClickTimer is { } timer)
        {
            _characterClickTimer = null;
            timer.Tick -= OnCharacterClickWindowElapsed;
            timer.Stop();
        }

        if (_characterClickTopmostTimer is { } topmostTimer)
        {
            _characterClickTopmostTimer = null;
            topmostTimer.Tick -= OnCharacterDoubleClickWindowElapsed;
            topmostTimer.Stop();
            _composer?.SetAlwaysOnTop(true);
        }
    }

    private void ShowComposer(bool alwaysOnTop = true, bool animateShow = false)
    {
        if (_shuttingDown || _coordinator is null || _coordinator.State.NeedsOnboarding)
        {
            return;
        }

        if (_composer is null)
        {
            ComposerViewModel viewModel = CreateComposerViewModel(autoCloseAfterSend: true);
            try
            {
                StartupDiagnostics.Stage("composer-window-create-started");
                _composer = new ComposerWindow(viewModel);
                _composer.PlacementChanged += OnComposerPlacementChanged;
                _composer.ComposerVisibilityChanged += OnComposerVisibilityChanged;
                _composer.ApplyTheme(_coordinator.State.Preferences.Theme);
                StartupDiagnostics.Stage("composer-window-created");
            }
            catch (Exception exception)
            {
                viewModel.Dispose();
                StartupDiagnostics.NonFatal("composer-window-create", exception);
                return;
            }
        }

        ApplyComposerState(_composer.ViewModel, _coordinator.State);
        _composer.ShowAndFocus(
            _coordinator.State.Preferences.OverlayRegion.MonitorIdentifier,
            _coordinator.State.Preferences.ComposerPlacement,
            alwaysOnTop,
            animateShow && _coordinator.AnimationsEnabled);
    }

    private ComposerViewModel CreateComposerViewModel(bool autoCloseAfterSend)
    {
        var viewModel = new ComposerViewModel(
            (roomId, body) => _coordinator is { } coordinator && !_shuttingDown
                ? coordinator.SendMessageAsync(roomId, body)
                : Task.FromException(new InvalidOperationException(I18n.Get("connection.server.not_configured"))),
            autoCloseAfterSend);
        viewModel.TypingChanged += active =>
        {
            if (_coordinator is null || _shuttingDown)
                return;
            bool? change = _typingOwner.Update(viewModel, active,
                viewModel.RoomId == _coordinator.State.ActiveRoomId && viewModel.CanCompose);
            if (change is { } typing)
                _ = RunCoordinatorCommandAsync(() => _coordinator.SetTypingAsync(typing));
        };
        if (_coordinator is not null)
            ApplyComposerState(viewModel, _coordinator.State);
        return viewModel;
    }

    private static void ApplyComposerState(ComposerViewModel composer, CoordinatorState state) =>
        composer.ApplyRoom(state.ActiveRoomId,
            state.ActiveRoomId is not null && !state.NeedsOnboarding && state.GroupOperation == GroupOperation.Idle,
            state.RealtimeConnection.IsReady && state.RealtimeConnection.ActiveRoomTransportConnected);

    private void OnComposerPlacementChanged(ComposerPlacement placement)
    {
        if (_coordinator is { } coordinator && !_shuttingDown)
            _pendingComposerPlacementSave = RunCoordinatorCommandAsync(() => coordinator.SetComposerPlacementAsync(placement));
    }

    private void OnComposerVisibilityChanged(bool isVisible)
    {
        _ = isVisible;
        if (!_characterClickFeedbackInProgress)
        {
            CancelPendingCharacterClick();
        }
    }

    private void RequestPulse()
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_shuttingDown || _coordinator is null)
            {
                return;
            }

            try
            {
                await _coordinator.PulseCurrentCharacterAsync();
            }
            catch (Exception exception)
            {
                if (!_shuttingDown)
                {
                    EnsureMainWindow().ShowFatalError(exception);
                }
            }
        });
    }

    private void RequestTreeMovementToggle(Guid? roomId)
    {
        if (_shuttingDown)
        {
            return;
        }
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_shuttingDown || _coordinator is null)
            {
                return;
            }
            try
            {
                await _coordinator.ToggleTreeMovementAsync(roomId);
            }
            catch (Exception exception)
            {
                StartupDiagnostics.NonFatal("tree-movement", exception);
            }
        });
    }

    private void RequestCharacterThrow(Guid targetUserId)
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_shuttingDown || _coordinator is null)
            {
                return;
            }

            try
            {
                await _coordinator.ThrowAtCharacterAsync(targetUserId);
            }
            catch (Exception exception)
            {
                StartupDiagnostics.NonFatal("character-throw", exception);
            }
        });
    }

    private void OnRenderingFailed(Exception exception)
    {
        StartupDiagnostics.NonFatal("overlay-render", exception);
        if (!_shuttingDown)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_shuttingDown)
                {
                    EnsureMainWindow().ShowFatalError(exception);
                }
            });
        }
    }

    private void OnGroupSetupRequested()
    {
        if (!_shuttingDown)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_shuttingDown)
                {
                    EnsureMainWindow().ShowPage("groups");
                }
            });
        }
    }

    private void OnOnboardingCompleted()
    {
        if (_shuttingDown || _onboardingWindow is null || _coordinator?.State.NeedsOnboarding != false)
        {
            return;
        }

        MainWindow mainWindow = EnsureMainWindow();
        OnboardingWindow onboarding = _onboardingWindow;
        _onboardingWindow = null;
        onboarding.Completed -= OnOnboardingCompleted;
        onboarding.Closed -= OnOnboardingClosed;
        _window = mainWindow;
        mainWindow.Activate();
        SideyWindowActivation.BringToForeground(mainWindow);
        onboarding.Close();
        if (_showAboutAfterOnboarding)
        {
            _showAboutAfterOnboarding = false;
            mainWindow.ShowPage("about");
            _postInstallLaunchTracker.TryMarkShown();
        }
        StartupDiagnostics.Stage("onboarding-completed");
    }

    private void CreateOnboardingWindow(AppCoordinator coordinator)
    {
        _onboardingWindow = new OnboardingWindow(coordinator);
        _onboardingWindow.Completed += OnOnboardingCompleted;
        _onboardingWindow.Closed += OnOnboardingClosed;
    }

    private void OnOnboardingClosed(object sender, WindowEventArgs args)
    {
        _ = args;
        if (!ReferenceEquals(sender, _onboardingWindow))
        {
            return;
        }

        _onboardingWindow!.Completed -= OnOnboardingCompleted;
        _onboardingWindow.Closed -= OnOnboardingClosed;
        _onboardingWindow = null;
        BeginShutdown();
    }

    private void RequestPrimaryActivation(string? activationArgument)
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_shuttingDown)
            {
                return;
            }

            if (WindowsStartupService.IsUpdateShutdown(activationArgument))
            {
                StartupDiagnostics.Stage("update-shutdown-requested");
                BeginShutdown();
                return;
            }

            if (await TryHandleActivationRequestAsync(activationArgument))
            {
                return;
            }

            if (WindowsStartupService.IsBackgroundLaunch(activationArgument))
                return;
            if (_shuttingDown)
            {
                return;
            }

            if (WindowsStartupService.IsShowAboutAfterInstall(activationArgument)
                || _postInstallLaunchTracker.IsPending())
            {
                ShowPostInstallWindow();
                return;
            }

            if (_onboardingWindow is null
                && _coordinator is not null
                && _coordinator.State.NeedsOnboarding)
            {
                CreateOnboardingWindow(_coordinator);
                _window = _onboardingWindow;
            }

            if (_onboardingWindow is not null)
            {
                _onboardingWindow.ShowAndActivate();
                return;
            }

            ShowPrimaryWindow();
        });
    }

    private async Task<bool> TryHandleActivationRequestAsync(string? activationArgument)
    {
        if (_shuttingDown || _coordinator is null)
        {
            return false;
        }
        string expectedScheme = _coordinator.AuthCallbackScheme;
        if (WindowsAuthCallback.TryGetError(
            activationArgument,
            expectedScheme,
            out _,
            out string? errorCode))
        {
            // OAuth errors carry no PKCE correlation; an older browser attempt must not cancel the current one.
            StartupDiagnostics.Stage($"oauth-callback-error code={errorCode ?? "unspecified"}");
            ShowPrimaryWindow();
            if (errorCode == "identity_already_exists")
            {
                try
                {
                    if (await _coordinator.RecoverGoogleIdentityLinkAsync())
                    {
                        StartupDiagnostics.Stage("oauth-callback-recovery result=success");
                        ShowGoogleSignInCompleted();
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    StartupDiagnostics.Stage("oauth-callback-recovery result=canceled");
                    return true;
                }
                catch (Exception exception)
                {
                    if (!_shuttingDown)
                    {
                        StartupDiagnostics.Stage($"oauth-callback-recovery result=failed type={exception.GetType().Name}");
                        StartupDiagnostics.NonFatal("oauth-callback-recovery", exception);
                        ShowActivationError(exception);
                    }
                    return true;
                }
            }
            if (_shuttingDown)
            {
                return true;
            }
            string message = errorCode == "identity_already_exists"
                ? I18n.Get("auth.google.already_linked")
                : I18n.Get("auth.google.cancelled");
            if (errorCode is not null && errorCode != "identity_already_exists")
            {
                message = $"{message} [{errorCode}]";
            }
            ShowActivationError(new InvalidOperationException(message));
            return true;
        }
        if (!WindowsAuthCallback.TryGetCode(
            activationArgument,
            expectedScheme,
            out Uri? callbackUri,
            out _))
        {
            return false;
        }

        try
        {
            StartupDiagnostics.Stage("oauth-callback-code received=true");
            // Use the callback process's foreground grant before awaiting network work.
            ShowPrimaryWindow();
            await _coordinator.CompleteGoogleIdentityLinkAsync(callbackUri!);
            StartupDiagnostics.Stage("oauth-callback-complete result=success");
            ShowGoogleSignInCompleted();
        }
        catch (Exception exception)
        {
            if (!_shuttingDown)
            {
                StartupDiagnostics.Stage($"oauth-callback-complete result=failed type={exception.GetType().Name}");
                StartupDiagnostics.NonFatal("oauth-callback-complete", exception);
                ShowActivationError(exception);
            }
        }
        return true;
    }

    private void ShowGoogleSignInCompleted()
    {
        if (_shuttingDown || _coordinator?.State.GoogleVerified != true)
        {
            return;
        }

        if (!_coordinator.State.NeedsOnboarding)
            OnOnboardingCompleted();
        ShowPrimaryWindow();
        if (_onboardingWindow is { } onboarding)
            onboarding.ShowGoogleSignInComplete();
        else
            _mainWindow?.ViewModel.ReportSuccess(I18n.Get("auth.google.sign_in_complete"));
        _tray?.NotifyGoogleSignInComplete();
    }

    private void ShowActivationError(Exception exception)
    {
        if (_onboardingWindow is { } onboarding)
        {
            onboarding.ShowAndActivate();
            onboarding.ShowError(exception);
            return;
        }

        MainWindow mainWindow = EnsureMainWindow();
        mainWindow.ShowFatalError(exception);
        ShowPrimaryWindow();
    }

    private void ShowPrimaryWindow()
    {
        if (_shuttingDown)
        {
            return;
        }

        if (_coordinator is { State.NeedsOnboarding: true } coordinator)
        {
            _showAboutAfterOnboarding |= _postInstallLaunchTracker.IsPending();
            if (_onboardingWindow is null)
                CreateOnboardingWindow(coordinator);
            _onboardingWindow!.ShowAndActivate();
            return;
        }
        MainWindow mainWindow = EnsureMainWindow();
        _window = mainWindow;
        mainWindow.AppWindow.Show();
        mainWindow.Activate();
        SideyWindowActivation.BringToForeground(mainWindow);
    }

    private void ShowPostInstallWindow()
    {
        if (_coordinator is { State.NeedsOnboarding: true })
        {
            _showAboutAfterOnboarding = true;
            ShowPrimaryWindow();
            return;
        }

        _showAboutAfterOnboarding = false;
        MainWindow mainWindow = EnsureMainWindow();
        _window = mainWindow;
        mainWindow.ShowPage("about");
        _postInstallLaunchTracker.TryMarkShown();
    }

    private void OnCoordinatorStateChanged(CoordinatorState state)
    {
        if (_shuttingDown)
        {
            return;
        }

        AppCoordinator? coordinator = _coordinator;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_shuttingDown || !ReferenceEquals(state, coordinator?.State))
            {
                return;
            }

            UpdateConnectionFailureNotification(state.Connected);
            _mainWindow?.ApplyState(state);
            // Authentication starts in Checking. Wait for restoration before choosing a window.
            if (_launchPresentationReady && state.NeedsOnboarding)
            {
                if (_onboardingWindow is null && coordinator is not null)
                    CreateOnboardingWindow(coordinator);
                _onboardingWindow?.ApplyState(state);
                _mainWindow?.AppWindow.Hide();
                if (_composer is not null)
                {
                    _composer.PlacementChanged -= OnComposerPlacementChanged;
                    _composer.ComposerVisibilityChanged -= OnComposerVisibilityChanged;
                    _composer.CloseForExit();
                    _composer = null;
                }
                _historyWindow?.Close();
                _historyWindow = null;
                _historyComposer?.Dispose();
                _historyComposer = null;
                _window = _onboardingWindow;
                _onboardingWindow?.ShowAndActivate();
            }
            else if (_launchPresentationReady && _onboardingWindow is not null)
            {
                _onboardingWindow.ApplyState(state);
                OnOnboardingCompleted();
            }
            _composer?.ApplyTheme(state.Preferences.Theme);
            if (_composer is not null)
                ApplyComposerState(_composer.ViewModel, state);
            if (_historyComposer is not null)
                ApplyComposerState(_historyComposer, state);
            _historyWindow?.ApplyState(state);
            _tray?.SetState(new TrayMenuState(
                state.Preferences.OverlayVisible,
                state.Preferences.QuietMode,
                state.Preferences.StartAtLogin,
                coordinator?.TotalUnreadCount ?? 0,
                [.. state.Rooms.Select(room => new TrayRoomMenuItem(
                    room.Id,
                    room.Name,
                    coordinator?.UnreadCount(room.Id) ?? 0))],
                state.ActiveRoomId)
            {
                Theme = state.Preferences.Theme,
                GlobalHotkeys = state.Preferences.GlobalHotkeys,
            });
        });
    }

    private void OnLanguageChanged(string language)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_shuttingDown || language == I18n.Language)
                return;
            I18n.SetLanguage(language);
            Localization.LocalizedText.RefreshAll();
            _mainWindow?.RefreshLocalizationLayout();
            _mainWindow?.RefreshMonitors();
            _mainWindow?.ViewModel.RefreshLocalizedText();
            _mainWindow?.RefreshLocalizedThemeSelection();
            _composer?.RefreshLocalizationLayout();
            _onboardingWindow?.RefreshLocalizationLayout();
            _composer?.Title = I18n.Get("composer.window.title");
            if (_historyWindow is not null)
            {
                _historyWindow.Title = I18n.Get("history.window.title");
                _historyWindow.RefreshLocalizedText();
            }
            StartupDiagnostics.Stage($"language-applied language={language}");
        });
    }

    private void OnTrayCommandInvoked(TrayCommand command)
    {
        if (!_shuttingDown)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_shuttingDown)
                {
                    HandleTrayCommand(command);
                }
            });
        }
    }

    private void OnTrayHotkeyInvoked(TrayCommand command)
    {
        if (!_shuttingDown)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_shuttingDown)
                {
                    return;
                }

                if (command == TrayCommand.History && _historyWindow?.IsVisible == true)
                {
                    _historyWindow.HideHistory();
                }
                else
                {
                    HandleTrayCommand(command);
                }
            });
        }
    }

    private void OnTrayRoomSelected(Guid roomId)
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_shuttingDown || _coordinator is null)
            {
                return;
            }

            try
            {
                await _coordinator.SwitchRoomAsync(roomId);
            }
            catch (Exception exception)
            {
                if (!_shuttingDown)
                {
                    EnsureMainWindow().ShowFatalError(exception);
                }
            }
        });
    }

    private void OnDisplayTopologyChanged()
    {
        if (_shuttingDown)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(ScheduleDisplayTopologyRefresh);
    }

    private void ScheduleDisplayTopologyRefresh()
    {
        if (_shuttingDown)
        {
            return;
        }

        if (_displayTopologyRefreshTimer is { } pending)
        {
            pending.Stop();
            pending.Start();
            return;
        }

        DispatcherQueueTimer timer = _dispatcherQueue.CreateTimer();
        timer.Interval = s_displayTopologyRefreshDelay;
        timer.IsRepeating = false;
        timer.Tick += OnDisplayTopologyRefreshElapsed;
        _displayTopologyRefreshTimer = timer;
        timer.Start();
    }

    private void OnDisplayTopologyRefreshElapsed(DispatcherQueueTimer sender, object args)
    {
        _ = args;
        sender.Tick -= OnDisplayTopologyRefreshElapsed;
        sender.Stop();
        if (ReferenceEquals(_displayTopologyRefreshTimer, sender))
        {
            _displayTopologyRefreshTimer = null;
        }

        if (_shuttingDown || _coordinator is null)
        {
            return;
        }

        try
        {
            _coordinator.RefreshDisplayTopology();
            _mainWindow?.RefreshMonitors();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("display-topology-refresh", exception);
            _mainWindow?.ShowFatalError(exception);
        }
    }

    private void CancelDisplayTopologyRefresh()
    {
        if (_displayTopologyRefreshTimer is not { } timer)
        {
            return;
        }

        timer.Tick -= OnDisplayTopologyRefreshElapsed;
        timer.Stop();
        _displayTopologyRefreshTimer = null;
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        if (_coordinator is null)
        {
            return;
        }
        if (_onboardingWindow is null
            && _coordinator.State.NeedsOnboarding
            && command != TrayCommand.Exit)
        {
            CreateOnboardingWindow(_coordinator);
            _window = _onboardingWindow;
        }
        if (_onboardingWindow is not null && command != TrayCommand.Exit)
        {
            _onboardingWindow.ShowAndActivate();
            return;
        }
        switch (command)
        {
            case TrayCommand.Open:
                ShowPrimaryWindow();
                break;
            case TrayCommand.ToggleOverlay:
                _ = RunCoordinatorCommandAsync(
                    () => _coordinator.SetOverlayVisibleAsync(
                        !_coordinator.State.Preferences.OverlayVisible));
                break;
            case TrayCommand.Compose:
                if (_composer?.IsVisible == true)
                    _composer.HideComposer();
                else
                    ShowComposer();
                break;
            case TrayCommand.ToggleQuietMode:
                _ = RunCoordinatorCommandAsync(
                    () => _coordinator.SetQuietModeAsync(
                        !_coordinator.State.Preferences.QuietMode));
                break;
            case TrayCommand.History:
                ShowHistory();
                break;
            case TrayCommand.Groups:
                EnsureMainWindow().ShowPage("groups");
                break;
            case TrayCommand.ToggleStartAtLogin:
                _ = RunCoordinatorCommandAsync(
                    () => _coordinator.SetStartAtLoginAsync(
                        !_coordinator.State.Preferences.StartAtLogin));
                break;
            case TrayCommand.CheckUpdates:
                _ = CheckForUpdatesFromTrayAsync();
                break;
            case TrayCommand.OpenUpdates:
                EnsureMainWindow().ShowUpdates();
                break;
            case TrayCommand.Settings:
                EnsureMainWindow().ShowPage("settings");
                break;
            case TrayCommand.Store:
                EnsureMainWindow().ShowPage("store");
                break;
            case TrayCommand.ReleaseNotes:
                _ = OpenCurrentReleaseNotesFromTrayAsync();
                break;
            case TrayCommand.Exit:
                BeginShutdown();
                break;
        }
    }

    private async Task OpenCurrentReleaseNotesFromTrayAsync()
    {
        try
        {
            await _updateService.OpenReleaseNotesAsync(_updateService.CurrentReleaseNotesUri);
        }
        catch (Exception exception)
        {
            MainWindow mainWindow = EnsureMainWindow();
            mainWindow.ShowPage("about");
            mainWindow.ViewModel.ReportError(new InvalidOperationException(
                I18n.Format("update.release_notes.open_failed", exception.Message),
                exception));
        }
    }

    private void UpdateConnectionFailureNotification(bool connected)
    {
        if (connected)
        {
            _connectionFailureNotificationArmed = true;
            CancelConnectionFailureNotification();
            return;
        }

        if (_shuttingDown
            || !_monitorConnectionFailures
            || !_connectionFailureNotificationArmed)
        {
            return;
        }

        ScheduleConnectionFailureNotification();
    }

    private void ScheduleConnectionFailureNotification()
    {
        if (_connectionFailureNotificationTimer is not null)
        {
            return;
        }

        DispatcherQueueTimer timer = _dispatcherQueue.CreateTimer();
        timer.Interval = s_connectionFailureNotificationDelay;
        timer.IsRepeating = false;
        timer.Tick += OnConnectionFailureNotificationElapsed;
        _connectionFailureNotificationTimer = timer;
        timer.Start();
        StartupDiagnostics.Stage(
            $"connection-failure-notification-deferred delay-ms={(long)s_connectionFailureNotificationDelay.TotalMilliseconds}");
    }

    private void OnConnectionFailureNotificationElapsed(
        DispatcherQueueTimer sender,
        object args)
    {
        _ = args;
        sender.Tick -= OnConnectionFailureNotificationElapsed;
        sender.Stop();
        if (ReferenceEquals(_connectionFailureNotificationTimer, sender))
        {
            _connectionFailureNotificationTimer = null;
        }

        if (!_shuttingDown && _coordinator?.State.Connected == false)
        {
            PostConnectionFailureNotification();
        }
    }

    private void CancelConnectionFailureNotification()
    {
        if (_connectionFailureNotificationTimer is not { } timer)
        {
            return;
        }

        timer.Tick -= OnConnectionFailureNotificationElapsed;
        timer.Stop();
        _connectionFailureNotificationTimer = null;
        StartupDiagnostics.Stage("connection-failure-notification-deferred result=cancelled");
    }

    private void PostConnectionFailureNotification()
    {
        if (_tray is null)
        {
            return;
        }

        _connectionFailureNotificationArmed = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_lastConnectionFailureNotificationAt is { } previous
            && now - previous < s_connectionFailureNotificationCooldown)
        {
            return;
        }

        _lastConnectionFailureNotificationAt = now;
        _tray.NotifyConnectionFailure();
        StartupDiagnostics.Stage("connection-failure-notification-posted");
    }

    private void ShowHistory()
    {
        if (_shuttingDown || _coordinator is null || _coordinator.State.NeedsOnboarding)
        {
            return;
        }
        if (_historyWindow is null)
        {
            _historyComposer ??= CreateComposerViewModel(autoCloseAfterSend: false);
            _historyWindow = new HistoryWindow(new HistoryWindowViewModel(_coordinator, _historyComposer));
            _historyWindow.Closed += (_, _) => _historyWindow = null;
        }
        _historyWindow.ShowAndActivate();
    }

    private async Task RunCoordinatorCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (OperationCanceledException) when (_shuttingDown)
        {
        }
        catch (Exception exception)
        {
            if (!_shuttingDown)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!_shuttingDown)
                    {
                        EnsureMainWindow().ShowFatalError(exception);
                    }
                });
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _ = sender;
        _ = args;
        BeginShutdown();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _ = args;
        if (sender is not MainWindow mainWindow || !ReferenceEquals(mainWindow, _mainWindow))
        {
            return;
        }

        bool shouldExit = mainWindow.ShouldExitOnClose;
        _pendingSettingsSave = mainWindow.ViewModel.FlushSettingsAsync();
        mainWindow.HotkeyRecordingChanged -= OnHotkeyRecordingChanged;
        _tray?.SetHotkeysSuspended(false);
        mainWindow.Closed -= OnMainWindowClosed;
        _mainWindow = null;
        if (ReferenceEquals(_window, mainWindow))
        {
            _window = null;
        }

        if (shouldExit)
        {
            BeginShutdown();
        }
    }

    private void OnHotkeyRecordingChanged(bool recording) =>
        _tray?.SetHotkeysSuspended(recording);

    private Task _pendingSettingsSave = Task.CompletedTask;

    private async void BeginShutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        CancelPendingCharacterClick();
        _monitorConnectionFailures = false;
        CancelConnectionFailureNotification();
        CancelDisplayTopologyRefresh();
        _uiResponsivenessTimer?.Dispose();
        _uiResponsivenessTimer = null;
        if (_mainWindow is not null)
        {
            MainWindow mainWindow = _mainWindow;
            _pendingSettingsSave = mainWindow.ViewModel.FlushSettingsAsync();
            _mainWindow = null;
            mainWindow.Closed -= OnMainWindowClosed;
            mainWindow.CloseForExit();
        }
#if DEBUG
        _developmentUpdate?.Dispose();
        _developmentUpdate = null;
#endif
        if (_tray is not null)
        {
            _tray.CommandInvoked -= OnTrayCommandInvoked;
            _tray.HotkeyInvoked -= OnTrayHotkeyInvoked;
            _tray.RoomSelected -= OnTrayRoomSelected;
            _tray.DisplayTopologyChanged -= OnDisplayTopologyChanged;
            try
            {
                _tray.Dispose();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.NonFatal("shutdown-tray-dispose", exception);
            }
            _tray = null;
        }
        if (_composer is not null)
        {
            _composer.PlacementChanged -= OnComposerPlacementChanged;
            _composer.ComposerVisibilityChanged -= OnComposerVisibilityChanged;
            _composer.CloseForExit();
            _composer = null;
        }

        _historyWindow?.Close();
        _historyWindow = null;
        _historyComposer?.Dispose();
        _historyComposer = null;
        if (_onboardingWindow is not null)
        {
            _onboardingWindow.Completed -= OnOnboardingCompleted;
            _onboardingWindow.Closed -= OnOnboardingClosed;
            _onboardingWindow.Close();
            _onboardingWindow = null;
        }
        if (_coordinator is not null)
        {
            try
            { await _pendingSettingsSave; }
            catch (Exception exception) { StartupDiagnostics.NonFatal("shutdown-settings-save", exception); }
            await _pendingComposerPlacementSave;
            _coordinator.ComposerRequested -= RequestComposer;
            _coordinator.CharacterClicked -= RequestCharacterClick;
            _coordinator.PulseRequested -= RequestPulse;
            _coordinator.TreeMovementToggleRequested -= RequestTreeMovementToggle;
            _coordinator.CharacterThrowRequested -= RequestCharacterThrow;
            _coordinator.RenderingFailed -= OnRenderingFailed;
            _coordinator.GroupSetupRequested -= OnGroupSetupRequested;
            _coordinator.StateChanged -= OnCoordinatorStateChanged;
            try
            {
                await _coordinator.DisposeAsync();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.NonFatal("shutdown-coordinator-dispose", exception);
            }
            _coordinator = null;
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        StartupDiagnostics.CompleteSession();
        Exit();
    }

#if DEBUG
    private void OnDevelopmentUpdateAccepted(DevelopmentUpdateRequest request)
    {
        if (_shuttingDown || _developmentUpdate is null)
        {
            return;
        }
        try
        {
            if (_developmentUpdate.LaunchUpdater(request))
            {
                // This callback runs on the watcher thread so it remains
                // independent from UI and network initialization stalls.
                StartupDiagnostics.Stage("update-handoff-complete");
                StartupDiagnostics.CompleteSession();
                Environment.Exit(0);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("development-update-start", exception);
        }
    }
#endif
}
