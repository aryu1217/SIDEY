using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Platform.Windows;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.App;

public partial class App : Application
{
    private static readonly TimeSpan s_connectionFailureNotificationDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_connectionFailureNotificationCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_displayTopologyRefreshDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly WindowsUpdateServiceAdapter _updateService;
    private readonly WindowsUpdateCompletionTracker _updateCompletionTracker;
    private Window? _window;
    private MainWindow? _mainWindow;
    private OnboardingWindow? _onboardingWindow;
    private HistoryWindow? _historyWindow;
    private ComposerWindow? _composer;
    private ComposerViewModel? _historyComposer;
    private readonly ComposerTypingOwner _typingOwner = new();
    private readonly CharacterClickComposerState _characterClickComposerState = new();
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
    private Timer? _uiResponsivenessTimer;
    private bool _handlingCharacterClick;
    private bool _shuttingDown;

    public App()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _updateService = new WindowsUpdateServiceAdapter();
        _updateCompletionTracker = new WindowsUpdateCompletionTracker();
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
                    I18n.Get("error.launch"),
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
        StartupDiagnostics.Stage("launch-entered");
        StartupDiagnostics.Stage(
            $"launch-mode background={backgroundLaunch} updateShutdown={updateShutdown}");
        _singleInstance = SingleInstanceGuard.Acquire(
            Environment.GetEnvironmentVariable(WindowsVersionGuard.StartupSmokeEnvironmentVariable) == "1"
                ? Environment.GetEnvironmentVariable("SIDEY_STARTUP_SMOKE_DATA_ROOT") : null);
        if (!_singleInstance.IsPrimary)
        {
            bool delivered = _singleInstance.Signal(processArguments);
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
            _updateService.CurrentVersion);
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
        if (coordinator.State.NeedsOnboarding)
        {
            CreateOnboardingWindow(coordinator);
            _window = _onboardingWindow;
            _onboardingWindow!.Activate();
            StartupDiagnostics.Stage("onboarding-window-activated");
        }
        else
        {
            EnsureMainWindow();
            _window = _mainWindow;
            StartupDiagnostics.Stage("completed-launch-window-hidden");
        }
        if (Environment.GetEnvironmentVariable(WindowsVersionGuard.StartupSmokeEnvironmentVariable) == "1")
        {
            _startupUpdateCheckStarted = true;
        }
        _singleInstance!.StartListening(RequestPrimaryActivation);
        try
        {
            _tray = TrayIconService.Start(coordinator.State.Preferences.GlobalHotkeys);
            _tray.CommandInvoked += OnTrayCommandInvoked;
            _tray.RoomSelected += OnTrayRoomSelected;
            _tray.DisplayTopologyChanged += OnDisplayTopologyChanged;
            _mainWindow?.SetTrayAvailable(true);
            StartupDiagnostics.Stage("tray-started");
            if (completedUpdateVersion is not null)
            {
                _tray.NotifyUpdateInstalled(completedUpdateVersion);
                StartupDiagnostics.Stage("update-completed-notification-posted");
            }
            if (_pendingUpdateNotificationVersion is { } pendingVersion)
            {
                PostUpdateNotification(pendingVersion);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("tray-start", exception);
            var error = new InvalidOperationException(I18n.Get("error.trayStart"), exception);
            if (coordinator.State.NeedsOnboarding)
                _onboardingWindow?.ShowError(error);
            else
                EnsureMainWindow().ShowFatalError(error);
        }
        if (completedUpdateVersion is null || _tray is not null)
        {
            StartupDiagnostics.Stage(
                $"update-completion-state-saved result={_updateCompletionTracker.TryMarkLaunched(_updateService.CurrentVersion).ToString().ToLowerInvariant()}");
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

            if (coordinator.State.NeedsOnboarding)
                _onboardingWindow?.ShowError(exception);
            else
                EnsureMainWindow().ShowFatalError(exception);
        }

        if (_shuttingDown || !ReferenceEquals(_coordinator, coordinator))
        {
            return;
        }

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
            AvailableUpdate? update = await mainWindow.ViewModel.CheckForUpdatesOnStartupAsync();
            StartupDiagnostics.Stage("startup-update-checked");
            if (!_shuttingDown
                && ReferenceEquals(_mainWindow, mainWindow)
                && update is not null)
            {
                PostUpdateNotification(update.Version);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("startup-update-check", exception);
        }
    }

    private void PostUpdateNotification(string version)
    {
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

    private async Task CheckForUpdatesFromTrayAsync()
    {
        if (_shuttingDown || _trayUpdateCheckInProgress)
        {
            return;
        }

        _trayUpdateCheckInProgress = true;
        try
        {
            AvailableUpdate? update = await _updateService.CheckAsync();
            if (_shuttingDown)
            {
                return;
            }

            if (update is null)
            {
                _tray?.NotifyLatestVersion();
                StartupDiagnostics.Stage("tray-update-notification-posted result=latest");
            }
            else
            {
                _tray?.NotifyUpdateAvailable(update.Version);
                StartupDiagnostics.Stage("tray-update-notification-posted result=available");
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("tray-update-check", exception);
            if (!_shuttingDown)
            {
                _tray?.NotifyUpdateCheckFailed();
            }
        }
        finally
        {
            _trayUpdateCheckInProgress = false;
        }
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
                _characterClickComposerState.Reset();
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

            _handlingCharacterClick = true;
            try
            {
                bool isVisible = _composer?.IsVisible == true;
                ComposerVisibilityAction action = _characterClickComposerState.HandleClick(clickCount, isVisible);
                switch (action)
                {
                    case ComposerVisibilityAction.Show:
                        ShowComposer();
                        break;
                    case ComposerVisibilityAction.Hide:
                        _composer?.HideComposer();
                        break;
                }

                if (clickCount == 1)
                {
                    _characterClickComposerState.CompleteSingleClick(_composer?.IsVisible == true);
                }
            }
            finally
            {
                _handlingCharacterClick = false;
            }
        });
    }

    private void ShowComposer()
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
            _coordinator.State.Preferences.ComposerPlacement);
    }

    private ComposerViewModel CreateComposerViewModel(bool autoCloseAfterSend)
    {
        var viewModel = new ComposerViewModel(
            (roomId, body) => _coordinator is { } coordinator && !_shuttingDown
                ? coordinator.SendMessageAsync(roomId, body)
                : Task.FromException(new InvalidOperationException(I18n.Get("error.serverNotConfigured"))),
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
            state.ActiveRoomId is not null && !state.NeedsOnboarding && state.GroupOperation == GroupOperation.Idle);

    private void OnComposerPlacementChanged(ComposerPlacement placement)
    {
        if (_coordinator is { } coordinator && !_shuttingDown)
            _pendingComposerPlacementSave = RunCoordinatorCommandAsync(() => coordinator.SetComposerPlacementAsync(placement));
    }

    private void OnComposerVisibilityChanged(bool isVisible)
    {
        _ = isVisible;
        if (!_handlingCharacterClick)
        {
            _characterClickComposerState.Reset();
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
            if (_shuttingDown)
            {
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
                ? I18n.Get("auth.googleAlreadyLinked")
                : I18n.Get("auth.googleCancelled");
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
            _mainWindow?.ViewModel.ReportSuccess(I18n.Get("auth.googleSignInComplete"));
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

    private void OnCoordinatorStateChanged(CoordinatorState state)
    {
        if (_shuttingDown)
        {
            return;
        }

        AppCoordinator? coordinator = _coordinator;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_shuttingDown)
            {
                return;
            }

            UpdateConnectionFailureNotification(state.Connected);
            _mainWindow?.ApplyState(state);
            if (state.NeedsOnboarding)
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
            else if (_onboardingWindow is not null)
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
            _mainWindow?.ViewModel.RefreshLocalizedText();
            _composer?.Title = I18n.Get("window.composerTitle");
            if (_historyWindow is not null)
            {
                _historyWindow.Title = I18n.Get("window.historyTitle");
                _historyWindow.ViewModel.RefreshLocalizedText();
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
                _coordinator.RequestComposer();
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
                I18n.Format("update.releaseNotesFailed", exception.Message),
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
