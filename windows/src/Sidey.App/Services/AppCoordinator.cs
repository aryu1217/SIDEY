using System.Diagnostics;
using System.Globalization;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Core.Overlay;
using Sidey.Core.Realtime;
using Sidey.Infrastructure;
using Sidey.Overlay;
using Sidey.Platform.Windows;
using Sidey.Platform.Windows.Diagnostics;
using Sidey.Platform.Windows.Shell;
using Sidey.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Sidey.App.Services;

/// <summary>
/// Owns application lifetime, server mutations, room switching and the native
/// overlay. Feature windows consume only CoordinatorState and commands.
/// </summary>
public sealed class AppCoordinator : IMainWindowCoordinator, IHistoryCoordinator, IAsyncDisposable
{
    private readonly WindowsAnimationSettings _animations = new();
    private readonly WindowsImpactAudio _audio;
    private readonly SemaphoreSlim _soundSettingGate = new(1, 1);
    private readonly SemaphoreSlim _overlayVisibilityGate = new(1, 1);
    private readonly Guid _overlayAudioScope = Guid.NewGuid();
    private readonly Lock _overlayAudioGate = new();
    private long _overlayAudioNotBefore;
    private Guid? _feedbackRoomId;
    private bool _feedbackConnected;
    public bool AnimationsEnabled => _animations.Enabled;
    public event Action? AnimationsChanged;
    private readonly IPreferencesStore _preferencesStore;
    private readonly ICredentialStore _credentialStore;
    private RoomSessionLifetime _roomSession = new();
    private readonly SemaphoreSlim _accountSessionGate = new(1, 1);
    private readonly IWindowsStartupService _startup;
    private readonly DiagnosticDataExporter _diagnosticDataExporter = new();
    private readonly IActivityMonitor _activityMonitor = new WindowsActivityMonitor();
    private readonly MessageLedger _messages = new();
    private readonly ActiveBubbleLedger _bubbles = new();
    private readonly CharacterPulseCooldown _pulseCooldown = new();
    private readonly CharacterThrowCooldown _throwCooldown = new();
    private readonly TypingActivityController _typingActivity;
    private readonly TypingFeedbackDispatcher _typingFeedback;
    private readonly Lock _localTypingGate = new();
    private Guid? _localTypingRoom;
    private readonly List<CharacterPulseEvent> _pendingPulses = [];
    private readonly List<CharacterThrowEvent> _pendingThrows = [];
    private readonly HashSet<(Guid RoomId, Guid UserId)> _typing = [];
    private readonly Dictionary<(Guid RoomId, Guid UserId), PresenceState> _basePresence = [];
    private readonly Dictionary<Guid, int> _unreadByRoom = [];
    private readonly TreeMovementLedger _treeMovement = new();
    private readonly HashSet<Guid> _treeMovementMigrationAttempted = [];
    private Guid? _treeMovementAccountId;
    private CancellationTokenSource? _treeMovementRequest;
    private readonly List<Task> _treeMovementOperations = [];
    private bool TreeMovementSaving => _treeMovementRequest is not null;
    private IAuthService? _auth;
    private IBackendGateway? _backend;
    private NativePixelWorldSession? _overlay;
    private long _groupOperationGeneration;
    private CoordinatorState _state = CoordinatorState.Initial;
    private readonly bool _validationMode;
    private Guid? _previewRoomId;
    private Guid? _previewUserId;
    private WorldSnapshot? _previewSnapshot;
    private PresenceState _localPresence = PresenceState.Online;
    private bool _cachedStateLoaded;
    private bool _initialSnapshotReceived;
    private bool OverlayInteractionConnected => _state.ActiveRoomConnected || (_backend is null && _previewSnapshot is not null);

    public AppCoordinator(
        IPreferencesStore? preferencesStore = null,
        ICredentialStore? credentialStore = null,
        IWindowsStartupService? startupService = null,
        Action<Action>? dispatchTypingFeedback = null)
    {
        _preferencesStore = preferencesStore ?? new AtomicPreferencesStore();
        _credentialStore = credentialStore ?? new WindowsCredentialStore();
        _startup = startupService ?? new WindowsStartupService();
        _audio = new WindowsImpactAudio(StartupDiagnostics.NonFatal);
        // The UI host supplies its dispatcher; headless coordinators have no overlay owner.
        _typingFeedback = new TypingFeedbackDispatcher(
            dispatchTypingFeedback ?? (_ => { }),
            (roomId, active) =>
            {
                lock (_localTypingGate)
                    _localTypingRoom = active ? roomId : null;
                ApplyWorldSnapshot();
            });
        _typingActivity = new TypingActivityController(
            (roomId, active, keepalive, token) => _backend?.BroadcastTypingAsync(roomId, active, keepalive, token) ?? Task.CompletedTask,
            _typingFeedback.Publish);
        _animations.Changed += OnAnimationsChanged;
#if DEBUG
        _validationMode = string.Equals(
            Environment.GetEnvironmentVariable("SIDEY_WINDOWS_VALIDATION_MODE"),
            "1",
            StringComparison.Ordinal);
#else
        _validationMode = false;
#endif
    }

    private void OnAnimationsChanged() => AnimationsChanged?.Invoke();
    public void PlayImpactSound(string id, Guid scope, long requestedAt) => _audio.Play(id, scope, requestedAt);
    public void ApplyCharacterSoundEffects(bool enabled, int volume)
    {
        _audio.SetVolume(volume);
        _audio.SetEnabled(enabled && volume > 0);
    }
    public async Task SaveCharacterSoundEffectsAsync(bool enabled, int volume, CancellationToken cancellationToken = default)
    {
        await _soundSettingGate.WaitAsync(cancellationToken);
        AppPreferences previous = _state.Preferences;
        try
        {
            _state = _state with
            {
                Preferences = _state.Preferences with
                {
                    CharacterSoundEffectsVolume = Math.Clamp(volume, 0, 100),
                    CharacterSoundEffectsEnabled = enabled && volume > 0,
                }
            };
            await PersistPreferencesAsync(cancellationToken);
            PublishState();
        }
        catch
        {
            _state = _state with
            {
                Preferences = _state.Preferences with
                {
                    CharacterSoundEffectsVolume = previous.CharacterSoundEffectsVolume,
                    CharacterSoundEffectsEnabled = previous.CharacterSoundEffectsEnabled,
                }
            };
            PublishState();
            throw;
        }
        finally { _soundSettingGate.Release(); }
    }
    private void StopOverlayAudio()
    {
        lock (_overlayAudioGate)
        {
            _overlayAudioNotBefore = Stopwatch.GetTimestamp();
            _audio.StopScope(_overlayAudioScope);
        }
    }
    private void PlayOverlayImpact(string id, long requestedAt)
    {
        lock (_overlayAudioGate)
        {
            if (requestedAt >= _overlayAudioNotBefore && _state.Preferences.OverlayVisible
                && OverlayInteractionConnected)
                _audio.Play(id, _overlayAudioScope, requestedAt);
        }
    }
    public void StopImpactSounds(Guid? scope = null)
    {
        if (scope is { } id)
            _audio.StopScope(id);
        else
            _audio.StopAll();
    }

    public CoordinatorState State => _state;

    public bool IsRemoteContentLoading { get; private set; } = true;

    public bool IsValidationMode => _validationMode;

    public string? ValidationMetricsPath => _overlay?.ValidationMetricsPath;

    public ValidationMetricsSnapshot? ValidationMetricsSummary
    {
        get
        {
            ValidationMetricsSummary? summary = _overlay?.ValidationMetricsSummary;
            return summary is null
                ? null
                : new ValidationMetricsSnapshot(
                    summary.ElapsedSeconds,
                    summary.SampleCount,
                    summary.MaximumFrameMilliseconds,
                    summary.CurrentWorkingSetBytes,
                    summary.PeakWorkingSetBytes,
                    summary.MaximumGdiHandles,
                    summary.MaximumUserHandles);
        }
    }

    public int UnreadCount(Guid roomId) => _unreadByRoom.GetValueOrDefault(roomId);

    public int TotalUnreadCount => _unreadByRoom.Values.Sum();

    public Task<MessageHistoryPage> FetchMessagePageAsync(
        Guid roomId,
        MessageHistoryCursor? before,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        RequiredBackend().FetchMessagePageAsync(roomId, before, limit, cancellationToken);

    public event Action<CoordinatorState>? StateChanged;
    public event Action? ComposerRequested;
    public event Action<int>? CharacterClicked;
    public event Action? PulseRequested;
    public event Action<Guid?>? TreeMovementToggleRequested;
    public event Action<Guid>? CharacterThrowRequested;
    public event Action<Exception>? RenderingFailed;
    public event Action? GroupSetupRequested;
    public event Action<string>? LanguageChanged;

    public async Task LoadCachedStateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedStateLoaded)
        {
            return;
        }

        AppPreferences preferences = await _preferencesStore.LoadAsync(cancellationToken);
        _audio.SetEnabled(preferences.CharacterSoundEffectsEnabled);
        _audio.SetVolume(preferences.CharacterSoundEffectsVolume);
        bool startAtLogin = _startup.IsEnabled();
        bool startupMirrorChanged = preferences.StartAtLogin != startAtLogin;
        if (startAtLogin)
        {
            _startup.UpgradeEnabledRegistration();
        }
        preferences = preferences with { StartAtLogin = startAtLogin };
        SetState(_state with { Preferences = preferences });
        _cachedStateLoaded = true;
        if (startupMirrorChanged)
        {
            try
            {
                await _preferencesStore.SaveAsync(preferences, cancellationToken);
            }
            catch (Exception exception)
            {
                StartupDiagnostics.NonFatal("startup-preference-mirror", exception);
            }
        }
    }

    private Task? _initializationTask;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Initialization and manual retry run on the UI thread and share one attempt.
        if (_initializationTask is { IsFaulted: false, IsCanceled: false })
            return _initializationTask;
        return _initializationTask = InitializeCoreAsync(cancellationToken);
    }

    public async Task RetryConnectionAsync(bool userInitiated = true)
    {
        if (_initializationTask is { IsCompleted: false })
            return;
        if (_initializationTask?.IsCompletedSuccessfully != true)
        {
            await InitializeAsync(_roomSession.Token);
            return;
        }
        if (_backend is SupabaseBackendGateway backend)
        {
            if (userInitiated && backend.IsRealtimeRecoveryPaused)
                await RefreshSnapshotAsync(_roomSession.Token);
            backend.RetryRealtimeConnection(userInitiated);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        IsRemoteContentLoading = true;
        SetState(_state with
        {
            ContentLoading = _state.ContentLoading with
            {
                Snapshot = _state.ContentLoading.Snapshot.Begin(),
                Store = _state.ContentLoading.Store.Begin(),
            },
        });
        try
        {
            await LoadRemoteContentAsync(cancellationToken);
        }
        finally
        {
            IsRemoteContentLoading = false;
            SetState(_state with
            {
                ContentLoading = _state.ContentLoading with
                {
                    Snapshot = _state.ContentLoading.Snapshot.EndAttempt(),
                    Store = _state.ContentLoading.Store.EndAttempt(),
                },
            });
        }
    }

    private async Task LoadRemoteContentAsync(CancellationToken cancellationToken)
    {
        await LoadCachedStateAsync(cancellationToken);
        ShowStartupOverlay();
        AppPreferences preferences = _state.Preferences;

        SupabaseRuntimeConfiguration? configuration;
#if DEBUG
        configuration = _validationMode ? null : SupabaseRuntimeConfiguration.FromEnvironment();
#else
        configuration = SupabaseRuntimeConfiguration.FromEnvironment();
#endif
        if (configuration is null)
        {
            StartupDiagnostics.Stage("server-configuration result=missing");
#if DEBUG
            SetState(_state with { GoogleAuthentication = GoogleAuthenticationState.Verified });
            StartPreviewOverlay(preferences);
            SetState(_state with
            {
                ErrorMessage = _validationMode
                    ? I18n.Get("development.metricsPreview")
                    : I18n.Get("development.localPreview"),
            });
#else
            SetState(_state with
            {
                ErrorMessage = I18n.Get("error.serverNotConfigured"),
            });
#endif
            return;
        }

        bool developmentCommerceEnabled = WindowsCommerceConfiguration.IsEnabled(configuration);
        AuthCallbackScheme = WindowsCommerceConfiguration.IsProduction(configuration)
            ? WindowsAuthCallback.ProductionScheme : WindowsAuthCallback.DevelopmentScheme;
        SetState(_state with
        {
            DevelopmentCommerceEnabled = developmentCommerceEnabled,
            CommerceProducts = developmentCommerceEnabled
                ? _state.CommerceProducts
                : WindowsCommerceCatalog.LockedStates(),
        });

        if (_backend is not SupabaseBackendGateway)
        {
            SupabaseAnonymousAuthService auth = _auth as SupabaseAnonymousAuthService
                ?? new SupabaseAnonymousAuthService(configuration, _credentialStore);
            _auth = auth;
            SetState(_state with { GoogleAuthentication = GoogleAuthenticationState.Checking });
            // Never bootstrap a replacement anonymous user. Restoring errors retain credentials.
            AuthSession? restored;
            bool hasGoogleIdentity;
            try
            {
                restored = await auth.RestoreSessionAsync(cancellationToken);
                hasGoogleIdentity = restored is not null
                    && await auth.HasGoogleIdentityAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SetState(_state with
                {
                    GoogleAuthentication = GoogleAuthenticationState.Required,
                    ErrorMessage = exception.Message,
                });
                throw;
            }
            if (restored is null || !hasGoogleIdentity)
            {
                SetState(_state with { GoogleAuthentication = GoogleAuthenticationState.Required });
                return;
            }
            SetState(_state with { GoogleAuthentication = GoogleAuthenticationState.Verified, ErrorMessage = null });
            ShowStartupOverlay();
            _backend = new SupabaseBackendGateway(configuration, auth, _credentialStore);
        }
        var backend = (SupabaseBackendGateway)_backend;

        StartupDiagnostics.Stage("server-snapshot-fetch-started");
        BackendSnapshot snapshot = await backend.FetchSnapshotAsync(cancellationToken);
        StartupDiagnostics.Stage(
            $"server-snapshot-fetch-completed rooms={snapshot.Rooms.Count} profile={(snapshot.Profile is null ? "missing" : "present")}");
        Guid? activeRoomId = SelectActiveRoom(preferences.ActiveRoomId, snapshot.Rooms);
        _state = _state with { ActiveRoomId = activeRoomId };
        ApplySnapshot(snapshot);
        if (snapshot.Profile is not null && !_state.Preferences.OnboardingCompleted)
        {
            SetState(_state with { Preferences = _state.Preferences with { OnboardingCompleted = true } });
            await PersistPreferencesAsync(cancellationToken);
        }
        StartTreeMovementMigration();
        string? commerceStateError = null;
        if (developmentCommerceEnabled)
        {
            try
            {
                await RefreshDevelopmentCommerceStateAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StartupDiagnostics.NonFatal("store-state-load", exception);
                commerceStateError = I18n.Get("store.stateUnavailable");
            }
        }
        _state = _state with
        {
            ActiveRoomId = activeRoomId,
            RealtimeConnection = RealtimeConnectionStatus.Disconnected,
            ErrorMessage = commerceStateError,
        };

        _roomSession.SwitchPipeline ??= new RoomSwitchPipeline(
            PerformRoomSwitchAsync,
            RestoreCommittedRoomAsync,
            CommitRoomSwitch);
        _roomSession.SwitchPipeline.InitializeCommittedRoom(activeRoomId);
        _roomSession.EventPump ??= PumpBackendEventsAsync();
        StartupDiagnostics.Stage(
            $"realtime-subscription-sync-started rooms={snapshot.Rooms.Count}");
        await backend.SynchronizeRealtimeRoomsAsync(
            RoomEpochs(snapshot.Rooms),
            activeRoomId,
            _localPresence,
            cancellationToken);
        StartupDiagnostics.Stage(
            $"realtime-subscription-sync-completed rooms={snapshot.Rooms.Count}");
        _roomSession.ActivityPump ??= PumpActivityAsync();
        if (activeRoomId is { } roomId)
        {
            StartupDiagnostics.Stage("message-history-fetch-started active=true");
            IReadOnlyList<ChatMessage> history = await backend.FetchRecentMessagesAsync(roomId, cancellationToken);
            _messages.ReplaceConfirmed(roomId, history);
            StartupDiagnostics.Stage(
                $"message-history-fetch-completed result=success count={history.Count}");
        }
        await PersistPreferencesAsync(cancellationToken);
        PublishState();
    }

    public async Task SaveProfileAsync(
        string nickname,
        string characterId,
        CancellationToken cancellationToken = default)
    {
        IBackendGateway backend = RequiredBackend();
        Guid? userId = _state.Profile?.Id;
        Profile profile = await backend.SaveProfileAsync(nickname, characterId, cancellationToken);
        if (!ReferenceEquals(backend, _backend) || _state.Profile?.Id != userId || _roomSession.IsCancellationRequested)
            return;
        if (_state.Profile is { } current)
            profile = current with { Nickname = profile.Nickname, CharacterId = profile.CharacterId };
        SetState(_state with
        {
            Profile = profile,
            Rooms = [.. _state.Rooms.Select(room => room with
            {
                Members = [.. room.Members.Select(member => member.UserId == profile.Id
                    ? member with { Nickname = profile.Nickname, CharacterId = profile.CharacterId }
                    : member)],
            })],
            Preferences = _state.Preferences with
            {
                OnboardingCompleted = _state.Preferences.OnboardingCompleted,
                CachedNickname = profile.Nickname,
                CachedCharacterId = PixelCharacterCatalog.NormalizeId(profile.CharacterId),
            },
            ErrorMessage = null,
        });
        await PersistPreferencesAsync(cancellationToken);
        ApplyWorldSnapshot();
    }

    public string AuthCallbackScheme { get; private set; } = WindowsAuthCallback.ProductionScheme;

    public async Task RefreshStoreAsync(CancellationToken cancellationToken = default)
    {
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _roomSession.Token);
        await RefreshDevelopmentCommerceStateAsync(refresh.Token);
    }

    public async Task ActivateStoreProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        if (!_state.DevelopmentCommerceEnabled
            || WindowsCommerceCatalog.Find(productId) is null
            || _backend is not SupabaseBackendGateway backend
            || _auth is not SupabaseAnonymousAuthService auth)
        {
            throw new InvalidOperationException(I18n.Get("store.unavailable"));
        }
        CommerceProductState state = _state.CommerceProducts.Single(product =>
            StringComparer.Ordinal.Equals(product.Product.Id, productId));
        if (state.IsWorking || state.PurchaseState == CommercePurchaseState.Owned)
        {
            return;
        }

        if (!state.GoogleConnected)
        {
            SetCommerceProductState(state with { IsWorking = true, ErrorMessage = null });
            try
            {
                Uri authorizationUri = await auth.BeginGoogleIdentityLinkAsync(
                    new Uri($"{AuthCallbackScheme}://auth/google"),
                    cancellationToken);
                await OpenExternalUriAsync(authorizationUri);
            }
            catch
            {
                SetCommerceProductState(state with
                {
                    PurchaseState = CommercePurchaseState.Error,
                    IsWorking = false,
                    ErrorMessage = I18n.Get("store.googleConnectionFailed"),
                });
                throw;
            }
            SetCommerceProductState(state with { IsWorking = false });
            return;
        }

        if (state.PurchaseState is not (
            CommercePurchaseState.Available
            or CommercePurchaseState.Refunded
            or CommercePurchaseState.Error))
        {
            return;
        }

        SetCommerceProductState(state with
        {
            PurchaseState = CommercePurchaseState.OpeningCheckout,
            IsWorking = true,
            ErrorMessage = null,
        });
        try
        {
            CommerceCheckout checkout = await backend.CreateWindowsCommerceOrderAsync(
                productId,
                cancellationToken);
            await OpenExternalUriAsync(checkout.CheckoutUri);
            SetCommerceProductState(state with
            {
                PurchaseState = CommercePurchaseState.Confirming,
                IsWorking = true,
            });
            for (int attempt = 0; attempt < 90; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                IReadOnlyList<CommerceProductState> refreshedProducts =
                    await RefreshDevelopmentCommerceStateAsync(
                        cancellationToken,
                        workingProductId: productId);
                CommerceProductState refreshed = refreshedProducts.Single(product =>
                    StringComparer.Ordinal.Equals(product.Product.Id, productId));
                if (refreshed.PurchaseState == CommercePurchaseState.Owned)
                {
                    await RefreshSnapshotAsync(cancellationToken);
                    return;
                }
            }
            throw new TimeoutException(I18n.Get("store.paymentTimedOut"));
        }
        catch
        {
            CommerceProductState current = _state.CommerceProducts.Single(product =>
                StringComparer.Ordinal.Equals(product.Product.Id, productId));
            if (current.PurchaseState != CommercePurchaseState.Owned)
            {
                SetCommerceProductState(current with
                {
                    PurchaseState = CommercePurchaseState.Error,
                    IsWorking = false,
                    ErrorMessage = I18n.Get("store.purchaseFailed"),
                });
            }
            throw;
        }
    }

    public async Task SetEquippedCosmeticAsync(
        CommerceProductKind kind,
        string? catalogItemId,
        CancellationToken cancellationToken = default)
    {
        if (kind == CommerceProductKind.Character)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (catalogItemId is not null)
        {
            CommerceProduct product = WindowsCommerceCatalog.Products.SingleOrDefault(candidate =>
                candidate.Kind == kind
                && StringComparer.Ordinal.Equals(candidate.EffectiveCatalogItemId, catalogItemId))
                ?? throw new ArgumentOutOfRangeException(nameof(catalogItemId));
            if (!_state.ActiveEntitlementKeys.Contains(product.EntitlementKey))
            {
                throw new InvalidOperationException(I18n.Get("store.unavailable"));
            }
        }

        IBackendGateway backend = RequiredBackend();
        Guid? userId = _state.Profile?.Id;
        Profile profile = await backend.SetEquippedCosmeticAsync(
            kind,
            catalogItemId,
            cancellationToken);
        if (!ReferenceEquals(backend, _backend) || _state.Profile?.Id != userId || _roomSession.IsCancellationRequested)
            return;
        // Independent equipment requests can complete out of order; apply only this request's field.
        if (_state.Profile is { } current)
            profile = kind == CommerceProductKind.Bubble
                ? current with { EquippedBubbleStyleId = profile.EquippedBubbleStyleId }
                : current with { EquippedThrowableId = profile.EquippedThrowableId };
        IReadOnlyList<Room> rooms = [.. _state.Rooms.Select(room => room with
        {
            Members = [.. room.Members.Select(member => member.UserId == profile.Id
                ? member with { EquippedBubbleStyleId = profile.EquippedBubbleStyleId }
                : member)],
        })];
        SetState(_state with { Profile = profile, Rooms = rooms, ErrorMessage = null });
        ApplyWorldSnapshot();
    }

    private CancellationTokenSource? _googleAuthenticationAttempt;
    private readonly SemaphoreSlim _googleIdentityRecoveryGate = new(1, 1);

    public async Task BeginGoogleAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        if (_googleAuthenticationAttempt is not null || _state.GoogleVerified)
            return;
        bool needsReauthentication = false;
        try
        {
            if (_initializationTask is null || _initializationTask.IsFaulted || _initializationTask.IsCanceled)
                await InitializeAsync(cancellationToken);
            else
                await _initializationTask;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is
            System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
        {
            // Reauthenticate only the preserved user after a permanent auth rejection.
            // Transport failures retain the normal restore/retry path.
            needsReauthentication = true;
        }
        if (_state.GoogleVerified)
            return;
        if (_auth is not SupabaseAnonymousAuthService auth)
            throw new InvalidOperationException(I18n.Get("error.serverNotConfigured"));
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _roomSession.Token);
        _googleAuthenticationAttempt = attempt;
        SetState(_state with { GoogleAuthentication = GoogleAuthenticationState.SigningIn, ErrorMessage = null });
        try
        {
            Uri redirect = new($"{AuthCallbackScheme}://auth/google");
            Uri uri;
            if (needsReauthentication)
                uri = await auth.BeginGoogleReauthenticationAsync(redirect, attempt.Token);
            else
            {
                AuthSession? session = await auth.RestoreSessionAsync(attempt.Token);
                uri = session is null
                    ? await auth.BeginGoogleSignInAsync(redirect, attempt.Token)
                    : await auth.BeginGoogleIdentityLinkAsync(redirect, attempt.Token);
            }
            attempt.Token.ThrowIfCancellationRequested();
            await OpenExternalUriAsync(uri);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            if (ReferenceEquals(_googleAuthenticationAttempt, attempt))
                await CancelGoogleAuthenticationAsync();
        }
        catch
        {
            if (ReferenceEquals(_googleAuthenticationAttempt, attempt))
                await CancelGoogleAuthenticationAsync();
            throw;
        }
    }

    public async Task CancelGoogleAuthenticationAsync()
    {
        CancellationTokenSource? attempt = _googleAuthenticationAttempt;
        attempt?.Cancel();
        if (_auth is SupabaseAnonymousAuthService auth)
            await auth.CancelGoogleAuthenticationAsync();
        if (ReferenceEquals(_googleAuthenticationAttempt, attempt))
            _googleAuthenticationAttempt = null;
        attempt?.Dispose();
        if (!_state.GoogleVerified)
            SetState(_state with { GoogleAuthentication = GoogleAuthenticationState.Required });
    }

    public async Task CompleteGoogleIdentityLinkAsync(Uri callbackUri, CancellationToken cancellationToken = default)
    {
        if (_auth is not SupabaseAnonymousAuthService auth
            || !WindowsAuthCallback.TryGetCode(callbackUri.AbsoluteUri, AuthCallbackScheme, out _, out string? code))
            throw new InvalidOperationException(I18n.Get("auth.identityLinkExpired"));
        CancellationTokenSource? attempt = _googleAuthenticationAttempt;
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _roomSession.Token, attempt?.Token ?? CancellationToken.None);
        await auth.CompleteGoogleIdentityLinkAsync(code!, completion.Token);
        if (attempt is not null && !ReferenceEquals(_googleAuthenticationAttempt, attempt))
            return;
        _googleAuthenticationAttempt = null;
        attempt?.Dispose();
        _initializationTask = null;
        await InitializeAsync(completion.Token);
    }

    public async Task<bool> RecoverGoogleIdentityLinkAsync(CancellationToken cancellationToken = default)
    {
        await _googleIdentityRecoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (_state.GoogleVerified)
            {
                return true;
            }
            if (_auth is not SupabaseAnonymousAuthService auth)
            {
                return false;
            }

            CancellationTokenSource? attempt = _googleAuthenticationAttempt;
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _roomSession.Token, attempt?.Token ?? CancellationToken.None);
            if (!await auth.HasGoogleIdentityAsync(recovery.Token))
            {
                return false;
            }

            _googleAuthenticationAttempt = null;
            _initializationTask = null;
            try
            {
                await InitializeAsync(recovery.Token);
                return _state.GoogleVerified;
            }
            finally
            {
                attempt?.Dispose();
            }
        }
        finally
        {
            _googleIdentityRecoveryGate.Release();
        }
    }

    public async Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
    {
        if (!_state.GoogleVerified)
            throw new InvalidOperationException(I18n.Get("auth.googleRequired"));
        AppPreferences previousPreferences = _state.Preferences;
        SetState(_state with
        {
            Preferences = previousPreferences with { OnboardingCompleted = true },
            ErrorMessage = null,
        });
        try
        {
            await PersistPreferencesAsync(cancellationToken);
        }
        catch
        {
            SetState(_state with { Preferences = previousPreferences });
            throw;
        }
    }

    public async Task CreateRoomAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureMutationsAvailable();
        SetState(_state with
        {
            GroupOperation = GroupOperation.Creating,
            SwitchingRoomId = null,
            ErrorMessage = null,
        });
        try
        {
            CreateRoomResult result = await RequiredBackend().CreateRoomAsync(name, cancellationToken);
            await RefreshSnapshotAndSelectAsync(result.Room.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            SetState(_state with { ErrorMessage = exception.Message });
            throw;
        }
        finally
        {
            SetState(_state with { GroupOperation = GroupOperation.Idle, SwitchingRoomId = null });
        }
    }

    public async Task JoinRoomAsync(string inviteCode, CancellationToken cancellationToken = default)
    {
        EnsureMutationsAvailable();
        SetState(_state with
        {
            GroupOperation = GroupOperation.Joining,
            SwitchingRoomId = null,
            ErrorMessage = null,
        });
        try
        {
            Room room = await RequiredBackend().JoinRoomAsync(inviteCode, cancellationToken);
            await RefreshSnapshotAndSelectAsync(room.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            SetState(_state with { ErrorMessage = exception.Message });
            throw;
        }
        finally
        {
            SetState(_state with { GroupOperation = GroupOperation.Idle, SwitchingRoomId = null });
        }
    }

    public async Task SwitchRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        if (_state.GroupOperation is not (GroupOperation.Idle or GroupOperation.Switching))
        {
            throw new InvalidOperationException(I18n.Get("groups.operationBusy"));
        }

        if (_roomSession.SwitchPipeline is null
            || _state.ActiveRoomId == roomId
            || _state.Rooms.All(room => room.Id != roomId))
        {
            return;
        }

        _typingActivity.Stop();
        long operationGeneration = Interlocked.Increment(ref _groupOperationGeneration);
        SetState(_state with
        {
            GroupOperation = GroupOperation.Switching,
            SwitchingRoomId = roomId,
            ErrorMessage = null,
        });
        try
        {
            await _roomSession.SwitchPipeline.RequestAsync(roomId, cancellationToken);
        }
        catch (Exception exception)
        {
            SetState(_state with { ErrorMessage = exception.Message });
            throw;
        }
        finally
        {
            if (operationGeneration == Volatile.Read(ref _groupOperationGeneration))
            {
                SetState(_state with
                {
                    GroupOperation = GroupOperation.Idle,
                    SwitchingRoomId = null,
                });
            }
        }
    }

    public Task RenameRoomAsync(
        Guid roomId,
        string name,
        CancellationToken cancellationToken = default) =>
        RunRoomMutationAsync(
            token => RequiredBackend().RenameRoomAsync(roomId, name, token),
            cancellationToken);

    public Task RotateInviteCodeAsync(
        Guid roomId,
        CancellationToken cancellationToken = default) =>
        RunRoomMutationAsync(
            token => RequiredBackend().RotateInviteCodeAsync(roomId, token),
            cancellationToken);

    public Task RemoveRoomMemberAsync(
        Guid roomId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        RunRoomMutationAsync(
            token => RequiredBackend().RemoveRoomMemberAsync(roomId, userId, token),
            cancellationToken);

    public Task DeleteRoomAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        RunRoomMutationAsync(
            token => RequiredBackend().DeleteRoomAsync(roomId, token),
            cancellationToken);

    public Task LeaveRoomAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        RunRoomMutationAsync(
            token => RequiredBackend().LeaveRoomAsync(roomId, token),
            cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        EndAccountSessionAsync(deleteAccount: false, cancellationToken);

    public Task DeleteAccountAsync(CancellationToken cancellationToken = default) =>
        EndAccountSessionAsync(deleteAccount: true, cancellationToken);

    private async Task EndAccountSessionAsync(
        bool deleteAccount,
        CancellationToken cancellationToken)
    {
        await _accountSessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_auth is not SupabaseAnonymousAuthService auth || !_state.GoogleVerified)
            {
                throw new InvalidOperationException(I18n.Get("auth.sessionMissing"));
            }
            if (_state.GroupOperation != GroupOperation.Idle)
            {
                throw new InvalidOperationException(I18n.Get("groups.operationBusy"));
            }
            cancellationToken.ThrowIfCancellationRequested();

            Guid[] roomIds = [.. _state.Rooms.Select(room => room.Id).Distinct()];
            _typingActivity.Stop();
            CancelTreeMovementRequest();
            if (deleteAccount)
            {
                await RequiredBackend().DeleteOwnAccountAsync(cancellationToken);
            }

            StopOverlayAudio();
            CancellationToken cleanupToken = CancellationToken.None;

            RoomSessionLifetime previousRoomSession = _roomSession;
            await previousRoomSession.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(_treeMovementOperations).ConfigureAwait(false);
            _treeMovementOperations.Clear();

            if (_backend is SupabaseBackendGateway backend)
            {
                await backend.DisposeAsync().ConfigureAwait(false);
            }
            _backend = null;

            await auth.SignOutAsync(cleanupToken).ConfigureAwait(false);
            foreach (Guid roomId in roomIds)
            {
                try
                {
                    await _credentialStore.DeleteInviteCodeAsync(roomId, cleanupToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    StartupDiagnostics.NonFatal("account-session-invite-cleanup", exception);
                }
            }

            await _overlayVisibilityGate.WaitAsync(cleanupToken).ConfigureAwait(false);
            try
            {
                _overlay?.Dispose();
                _overlay = null;
            }
            finally
            {
                _overlayVisibilityGate.Release();
            }

            _roomSession = new RoomSessionLifetime();
            _initializationTask = null;
            _initialSnapshotReceived = false;
            _previewRoomId = null;
            _previewUserId = null;
            _previewSnapshot = null;
            _localPresence = PresenceState.Online;
            _messages.Clear();
            _bubbles.Clear();
            _typing.Clear();
            _basePresence.Clear();
            _unreadByRoom.Clear();
            _treeMovement.Clear();
            _treeMovementAccountId = null;
            _pendingPulses.Clear();
            _pendingThrows.Clear();

            AppPreferences preferences = _state.Preferences with
            {
                OnboardingCompleted = false,
                CachedNickname = null,
                CachedCharacterId = null,
                ActiveRoomId = null,
                TreeMovementPaused = false,
            };
            await _preferencesStore.SaveAsync(preferences, cleanupToken).ConfigureAwait(false);
            IsRemoteContentLoading = false;
            SetState(CoordinatorState.Initial with
            {
                Preferences = preferences,
                GoogleAuthentication = GoogleAuthenticationState.Required,
                ContentLoading = new(RemoteDataLoadState.Ready, RemoteDataLoadState.Ready),
            });
        }
        finally
        {
            _accountSessionGate.Release();
        }
    }

    private async Task RunRoomMutationAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        EnsureMutationsAvailable();
        _typingActivity.Stop();
        SetState(_state with
        {
            GroupOperation = GroupOperation.Mutating,
            SwitchingRoomId = null,
            ErrorMessage = null,
        });
        try
        {
            await mutation(cancellationToken);
            await RefreshSnapshotAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            SetState(_state with { ErrorMessage = exception.Message });
            throw;
        }
        finally
        {
            SetState(_state with { GroupOperation = GroupOperation.Idle, SwitchingRoomId = null });
        }
    }

    public ValueTask<string?> GetInviteCodeAsync(
        Guid roomId,
        CancellationToken cancellationToken = default) =>
        _credentialStore.ReadInviteCodeAsync(roomId, cancellationToken);

    public async Task<bool> CopyInviteCodeAsync(
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        Room room = _state.Rooms.FirstOrDefault(room => room.Id == roomId) ?? throw new InvalidOperationException(I18n.Get("groups.notFound"));
        if (!room.InviteCodeReady)
        {
            throw new InvalidOperationException(
                I18n.Get("groups.inviteRevoked"));
        }

        string? code = await GetInviteCodeAsync(roomId, cancellationToken);
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }
        string normalizedCode = code.Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
        string hintSuffix = room.InviteCodeHint[(room.InviteCodeHint.LastIndexOf('-') + 1)..]
            .ToUpperInvariant();
        if (hintSuffix.Length != 4
            || normalizedCode.Length < hintSuffix.Length
            || !normalizedCode.EndsWith(hintSuffix, StringComparison.Ordinal))
        {
            await _credentialStore.DeleteInviteCodeAsync(roomId, cancellationToken);
            throw new InvalidOperationException(
                I18n.Get("groups.inviteReplaced"));
        }

        var data = new DataPackage();
        data.SetText(code);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        return true;
    }

    public async Task SendMessageAsync(Guid roomId, string body, CancellationToken cancellationToken = default)
    {
        if (_state.ActiveRoomId != roomId || _state.Profile is not { } profile
            || _state.NeedsOnboarding || _state.GroupOperation != GroupOperation.Idle)
        {
            throw new InvalidOperationException(I18n.Get("composer.activeRoomRequired"));
        }

        string normalized = MessageValidator.Normalize(body);
        if (!MessageValidator.IsValid(normalized))
        {
            throw new ArgumentException(I18n.Get("validation.messageLength"), nameof(body));
        }

        _typingActivity.Stop();
        var id = Guid.NewGuid();
        _messages.Stage(id, roomId, profile.Id, normalized, bubbleStyleId: profile.EquippedBubbleStyleId);
        _bubbles.Show(profile.Id, id, normalized, bubbleStyleId: profile.EquippedBubbleStyleId);
        PublishState();
        ApplyWorldSnapshot();
        try
        {
            ChatMessage confirmed = await RequiredBackend().SendMessageAsync(
                id,
                roomId,
                normalized,
                cancellationToken);
            _messages.Confirm(confirmed);
            PublishState();
        }
        catch
        {
            // Realtime may have confirmed this UUID before the HTTP response was lost.
            if (_messages.Fail(id) is null)
                return;
            _bubbles.Remove(id);
            PublishState();
            ApplyWorldSnapshot();
            throw;
        }
    }

    public async Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        SetState(_state with
        {
            Preferences = _state.Preferences with { OverlayVisible = visible },
        });

        await _overlayVisibilityGate.WaitAsync(cancellationToken);
        try
        {
            if (!visible)
            {
                StopOverlayAudio();
                NativePixelWorldSession? overlay = _overlay;
                if (overlay is not null)
                {
                    try
                    {
                        await overlay.FadeOutAsync(cancellationToken);
                    }
                    finally
                    {
                        if (ReferenceEquals(_overlay, overlay))
                        {
                            overlay.Dispose();
                            _overlay = null;
                        }
                    }
                }
            }
            else if (_overlay is null)
            {
                if (_backend is null && _previewSnapshot is not null)
                {
                    StartPreviewOverlay(_state.Preferences);
                }
                else if (_state.ActiveRoomId is not null)
                {
                    StartOverlay(CurrentWorldSnapshot());
                }
                else
                {
                    ShowStartupOverlay();
                }
            }

            await PersistPreferencesAsync(cancellationToken);
        }
        finally
        {
            _overlayVisibilityGate.Release();
        }
    }

    public async Task SetQuietModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        SetState(_state with
        {
            Preferences = _state.Preferences with { QuietMode = enabled },
        });
        ApplyWorldSnapshot();
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task SetGlobalHotkeysAsync(
        GlobalHotkeySettings settings,
        CancellationToken cancellationToken = default)
    {
        GlobalHotkeySettings previous = _state.Preferences.GlobalHotkeys;
        SetState(_state with
        {
            Preferences = _state.Preferences with { GlobalHotkeys = settings.Normalize() },
        });
        try
        {
            await PersistPreferencesAsync(cancellationToken);
        }
        catch
        {
            SetState(_state with
            {
                Preferences = _state.Preferences with { GlobalHotkeys = previous },
            });
            throw;
        }
    }

    public async Task SetComposerPlacementAsync(ComposerPlacement placement, CancellationToken cancellationToken = default)
    {
        SetState(_state with { Preferences = _state.Preferences with { ComposerPlacement = placement } });
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task SetShowOfflineMembersAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        SetState(_state with
        {
            Preferences = _state.Preferences with { ShowOfflineMembers = enabled },
        });
        await PersistPreferencesAsync(cancellationToken);
        ApplyWorldSnapshot();
    }

    public async Task ToggleTreeMovementAsync(Guid? expectedRoomId, CancellationToken cancellationToken = default)
    {
        WorldSnapshot world = CurrentWorldSnapshot();
        if (TreeMovementSaving || world.RoomId != expectedRoomId
            || world.Members.FirstOrDefault(member => member.IsCurrentUser)?.CharacterId != "pixel_tree")
            return;
        if (_backend is null && _previewSnapshot is not null)
        {
            SetState(_state with { Preferences = _state.Preferences with { TreeMovementPaused = !_state.Preferences.TreeMovementPaused } });
            ApplyWorldSnapshot();
            return;
        }
        if (_state.Profile is not { } profile)
            return;
        if (profile.TreeMovementRevision is null)
        {
            SetState(_state with { Preferences = _state.Preferences with { TreeMovementPaused = !_state.Preferences.TreeMovementPaused } });
            ApplyWorldSnapshot();
            await PersistPreferencesAsync(cancellationToken);
            return;
        }
        PixelWorldMember current = world.Members.Single(member => member.IsCurrentUser);
        bool paused = PixelMovementPolicy.IsTreePaused(current, world.TreeMovementPaused);
        await SaveTreeMovementAsync(!paused, profile, cancellationToken);
    }

    private void StartTreeMovementMigration()
    {
        _treeMovementOperations.RemoveAll(task => task.IsCompleted);
        _treeMovementOperations.Add(MigrateTreeMovementAsync(_roomSession.Token));
    }

    private void CancelTreeMovementRequest()
    {
        CancellationTokenSource? previous = _treeMovementRequest;
        _treeMovementRequest = null;
        previous?.Cancel();
    }

    private async Task MigrateTreeMovementAsync(CancellationToken cancellationToken)
    {
        if (TreeMovementSaving || _backend is null
            || _state.Profile is not { TreeMovementRevision: 0 } profile
            || !_treeMovementMigrationAttempted.Add(profile.Id))
            return;
        try
        {
            await SaveTreeMovementAsync(_state.Preferences.TreeMovementPaused, profile, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // A failed migration never changes confirmed state or blocks login.
            StartupDiagnostics.NonFatal("tree-movement-migration", exception);
        }
    }

    private Task SaveTreeMovementAsync(bool paused, Profile profile, CancellationToken cancellationToken)
    {
        Task operation = SaveTreeMovementCoreAsync(paused, profile, cancellationToken);
        _treeMovementOperations.RemoveAll(task => task.IsCompleted);
        _treeMovementOperations.Add(DrainTreeMovementOperationAsync(operation));
        return operation;
    }

    private static async Task DrainTreeMovementOperationAsync(Task operation)
    {
        // The caller reports failures; this observer owns shutdown draining only.
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private async Task SaveTreeMovementCoreAsync(bool paused, Profile profile, CancellationToken cancellationToken)
    {
        IBackendGateway backend = RequiredBackend();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _roomSession.Token);
        _treeMovementRequest = request;
        try
        {
            Profile saved = await backend.SetTreeMovementPausedAsync(
                paused, profile.TreeMovementRevision!.Value, request.Token);
            if (!ReferenceEquals(request, _treeMovementRequest) || !ReferenceEquals(backend, _backend) || _state.Profile is not { } current
                || current.Id != profile.Id || saved.Id != profile.Id || _roomSession.IsCancellationRequested)
                return;
            Profile confirmed = MergeTreeMovement(saved);
            SetState(_state with
            {
                Profile = current with
                {
                    TreeMovementPaused = confirmed.TreeMovementPaused,
                    TreeMovementRevision = confirmed.TreeMovementRevision,
                },
                Rooms = [.. _state.Rooms.Select(room => room with
                {
                    Members = [.. room.Members.Select(member => member.UserId == profile.Id
                        ? member with
                        {
                            TreeMovementPaused = confirmed.TreeMovementPaused,
                            TreeMovementRevision = confirmed.TreeMovementRevision,
                        } : member)],
                })],
            });
            ApplyWorldSnapshot();
        }
        finally
        {
            if (ReferenceEquals(request, _treeMovementRequest))
                _treeMovementRequest = null;
        }
    }

    private Profile MergeTreeMovement(Profile profile)
    {
        (bool Paused, long? Revision) state = _treeMovement.Merge(profile.Id, profile.TreeMovementPaused, profile.TreeMovementRevision);
        return profile with { TreeMovementPaused = state.Paused, TreeMovementRevision = state.Revision };
    }

    private RoomMember MergeTreeMovement(RoomMember member)
    {
        (bool Paused, long? Revision) state = _treeMovement.Merge(member.UserId, member.TreeMovementPaused, member.TreeMovementRevision);
        return member with { TreeMovementPaused = state.Paused, TreeMovementRevision = state.Revision };
    }

    public async Task SetRequiresRightClickToThrowAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        SetState(_state with
        {
            Preferences = _state.Preferences with { RequiresRightClickToThrow = enabled },
        });
        await PersistPreferencesAsync(cancellationToken);
        _overlay?.ConfigureThrowInteraction(enabled, OverlayInteractionConnected);
    }

    public async Task SetStartAtLoginAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        _startup.SetEnabled(enabled);
        SetState(_state with
        {
            Preferences = _state.Preferences with { StartAtLogin = enabled },
        });
        await PersistPreferencesAsync(cancellationToken);
    }

    public IReadOnlyList<MonitorOption> GetMonitors() => [.. WindowsMonitorService.GetAll()
        .Select(monitor => new MonitorOption(
            monitor.Identifier,
            monitor.Name,
            monitor.IsPrimary))];

    public async Task SetLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        if (!I18n.IsSupportedLanguage(language))
            throw new ArgumentOutOfRangeException(nameof(language));

        string? previousLanguage = _state.Preferences.Language;
        SetState(_state with { Preferences = _state.Preferences with { Language = language } });
        try
        {
            await PersistPreferencesAsync(cancellationToken);
        }
        catch
        {
            SetState(_state with { Preferences = _state.Preferences with { Language = previousLanguage } });
            throw;
        }
        LanguageChanged?.Invoke(language);
    }

    public async Task SetThemeAsync(
        AppThemePreference theme,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme));

        AppThemePreference previousTheme = _state.Preferences.Theme;
        SetState(_state with { Preferences = _state.Preferences with { Theme = theme } });
        try
        {
            await PersistPreferencesAsync(cancellationToken);
        }
        catch
        {
            SetState(_state with { Preferences = _state.Preferences with { Theme = previousTheme } });
            throw;
        }
    }

    public void RefreshDisplayTopology()
    {
        IReadOnlyList<MonitorOption> monitors = GetMonitors();
        StartupDiagnostics.Stage(
            $"display-topology-refreshed monitors={monitors.Count} "
            + $"primary={monitors.FirstOrDefault(monitor => monitor.IsPrimary)?.Identifier ?? "none"}");
        if (_overlay is not null)
        {
            if (_backend is null && _previewSnapshot is not null)
            {
                StartPreviewOverlay(_state.Preferences);
            }
            else
            {
                RestartOverlayForRegionChange();
            }
        }
    }

    public Task SetTypingAsync(bool active, CancellationToken cancellationToken = default)
    {
        if (active && _state.GoogleVerified && !cancellationToken.IsCancellationRequested && _backend is not null
            && _state.GroupOperation == GroupOperation.Idle && _state.ActiveRoomId is { } roomId)
            _typingActivity.Edit(roomId);
        else
            _typingActivity.Stop();
        return Task.CompletedTask;
    }

    public async Task SetRegionAsync(
        OverlayRegionPreference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (!Enum.IsDefined(preference.Edge) || !Enum.IsDefined(preference.Span))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }
        if (preference == _state.Preferences.OverlayRegion)
        {
            return;
        }

        SetState(_state with
        {
            Preferences = _state.Preferences with { OverlayRegion = preference },
        });
        await PersistPreferencesAsync(cancellationToken);
        if (_overlay is null)
        {
            return;
        }

        if (_backend is null && _previewSnapshot is not null)
        {
            StartPreviewOverlay(_state.Preferences);
            return;
        }

        RestartOverlayForRegionChange();
    }

    public void RequestComposer() => ComposerRequested?.Invoke();

    private void RequestCharacterClick(int clickCount) => CharacterClicked?.Invoke(clickCount);

    public void RequestCharacterPulse() => PulseRequested?.Invoke();

    public void RequestCharacterThrow(Guid targetUserId) =>
        CharacterThrowRequested?.Invoke(targetUserId);

    public async Task PulseCurrentCharacterAsync(CancellationToken cancellationToken = default)
    {
        if (!OverlayInteractionConnected)
            return;
        if (_overlay?.IsSelfStunned == true)
            return;
        Guid? roomId = _state.ActiveRoomId ?? _previewRoomId;
        Guid? userId = _state.Profile?.Id ?? _previewUserId;
        if (roomId is null || userId is null)
        {
            return;
        }

        var uptime = TimeSpan.FromSeconds(
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        if (!_pulseCooldown.Accept(roomId.Value, userId.Value, uptime))
        {
            return;
        }

        var pulse = new CharacterPulseEvent(Guid.NewGuid(), roomId.Value, userId.Value);
        QueuePulseForWorld(pulse);

        if (_backend is not null)
        {
            await _backend.BroadcastCharacterPulseAsync(
                roomId.Value,
                pulse.Id,
                cancellationToken);
        }
    }

    public async Task ThrowAtCharacterAsync(
        Guid targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (_overlay?.IsSelfStunned == true)
            return;
        Guid? roomId = _state.ActiveRoomId ?? _previewRoomId;
        Profile? actor = _state.Profile;
        Guid? actorUserId = actor?.Id ?? _previewUserId;
        string? sourceCharacterId = actor?.CharacterId
            ?? _previewSnapshot?.Members.FirstOrDefault(member => member.IsCurrentUser)?.CharacterId;
        if (roomId is null || actorUserId is null || sourceCharacterId is null
            || actorUserId == targetUserId
            || (_backend is not null && !_state.ActiveRoomConnected))
        {
            return;
        }

        WorldSnapshot world = CurrentWorldSnapshot();
        PixelWorldMember? target = world.Members.FirstOrDefault(member => member.Id == targetUserId);
        if (world.RoomId != roomId || target is null
            || !CharacterThrowTargetPolicy.CanTarget(target))
        {
            return;
        }

        var uptime = TimeSpan.FromSeconds(
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        if (!_throwCooldown.Accept(roomId.Value, actorUserId.Value, uptime))
        {
            return;
        }

        var characterThrow = new CharacterThrowEvent(
            Guid.NewGuid(),
            roomId.Value,
            actorUserId.Value,
            targetUserId,
            sourceCharacterId,
            actor?.EquippedThrowableId);
        QueueThrowForWorld(characterThrow);
        if (_backend is not null)
        {
            try
            {
                await _backend.BroadcastCharacterThrowAsync(
                    roomId.Value,
                    characterThrow.Id,
                    targetUserId,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Trace.TraceError("SIDEY character throw broadcast failed: {0}", exception);
            }
        }
    }

    public Task<string?> ExportValidationMetricsAsync(
        CancellationToken cancellationToken = default) =>
        _overlay?.ExportValidationMetricsAsync(cancellationToken)
        ?? Task.FromResult<string?>(null);

    public Task<string> ExportDiagnosticDataAsync(
        CancellationToken cancellationToken = default) =>
        _diagnosticDataExporter.ExportAsync(cancellationToken);

    public Task OpenExternalUriAsync(Uri uri)
    {
        WindowsExternalUriLauncher.Open(uri);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _typingFeedback.Dispose();
        _animations.Changed -= OnAnimationsChanged;
        _animations.Dispose();
        CancelTreeMovementRequest();
        await _typingActivity.DisposeAsync().ConfigureAwait(false);
        _audio.Dispose();
        await _roomSession.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_treeMovementOperations).ConfigureAwait(false);
        if (_backend is SupabaseBackendGateway supabase)
        {
            await supabase.DisposeAsync().ConfigureAwait(false);
        }
        await CancelGoogleAuthenticationAsync();
        if (_auth is IDisposable disposableAuth)
        {
            disposableAuth.Dispose();
        }
        await _overlayVisibilityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _overlay?.Dispose();
            _overlay = null;
        }
        finally
        {
            _overlayVisibilityGate.Release();
        }
        await _activityMonitor.DisposeAsync().ConfigureAwait(false);
        _accountSessionGate.Dispose();
    }

    private async Task<IReadOnlyList<ChatMessage>> PerformRoomSwitchAsync(
        Guid roomId,
        CancellationToken cancellationToken)
    {
        IBackendGateway backend = RequiredBackend();
        await backend.SynchronizeRealtimeRoomsAsync(
            RoomEpochs(_state.Rooms),
            roomId,
            _localPresence,
            cancellationToken);
        return await backend.FetchRecentMessagesAsync(roomId, cancellationToken);
    }

    private Task RestoreCommittedRoomAsync(Guid? roomId, CancellationToken cancellationToken) =>
        RequiredBackend().SynchronizeRealtimeRoomsAsync(
            RoomEpochs(_state.Rooms),
            roomId,
            _localPresence,
            cancellationToken);

    private void CommitRoomSwitch(Guid roomId, IReadOnlyList<ChatMessage> history)
    {
        _messages.ReplaceConfirmed(roomId, history);
        _bubbles.Clear();
        _unreadByRoom[roomId] = 0;
        _state = _state with
        {
            ActiveRoomId = roomId,
            Preferences = _state.Preferences with { ActiveRoomId = roomId },
        };
        _ = PersistCommittedRoomAsync();
        PublishState();
        if (_overlay is null && _state.Preferences.OverlayVisible)
        {
            StartOverlay(CurrentWorldSnapshot());
        }
        else
        {
            ApplyWorldSnapshot();
        }
    }

    private async Task PumpBackendEventsAsync()
    {
        try
        {
            await foreach (BackendEvent backendEvent in RequiredBackend().SubscribeAsync(_roomSession.Token))
            {
                switch (backendEvent)
                {
                    case BackendEvent.SnapshotReceived snapshot:
                        await ReconcileSnapshotAsync(snapshot.Snapshot, _roomSession.Token);
                        break;
                    case BackendEvent.MessageReceived message:
                        bool isActiveRoom = message.Message.RoomId == _state.ActiveRoomId;
                        StartupDiagnostics.Stage(
                            $"realtime-message-received active={isActiveRoom.ToString().ToLowerInvariant()}");
                        _messages.Confirm(message.Message);
                        StartupDiagnostics.Stage("message-ledger-confirmed");
                        _bubbles.Show(
                            message.Message.SenderId,
                            message.Message.Id,
                            message.Message.Body,
                            bubbleStyleId: message.Message.BubbleStyleId);
                        StartupDiagnostics.Stage(
                            $"message-bubble-enqueued active={isActiveRoom.ToString().ToLowerInvariant()} quiet={_state.Preferences.QuietMode.ToString().ToLowerInvariant()}");
                        if (message.Message.SenderId != _state.Profile?.Id
                            && (!isActiveRoom || _state.Preferences.QuietMode))
                        {
                            _unreadByRoom[message.Message.RoomId] = Math.Min(
                                99,
                                _unreadByRoom.GetValueOrDefault(message.Message.RoomId) + 1);
                        }
                        PublishState();
                        ApplyWorldSnapshot("message");
                        break;
                    case BackendEvent.MessageDeleted deleted:
                        _messages.Remove(deleted.RoomId, deleted.MessageId);
                        _bubbles.Remove(deleted.MessageId);
                        PublishState();
                        ApplyWorldSnapshot();
                        break;
                    case BackendEvent.MessagesReplaced replaced:
                        StartupDiagnostics.Stage(
                            $"realtime-messages-reconciled active={(replaced.RoomId == _state.ActiveRoomId).ToString().ToLowerInvariant()}");
                        _messages.ReplaceConfirmed(replaced.RoomId, replaced.Messages);
                        if (replaced.RoomId == _state.ActiveRoomId)
                        {
                            _bubbles.Clear();
                        }
                        PublishState();
                        ApplyWorldSnapshot();
                        break;
                    case BackendEvent.PresenceChanged presence:
                        StartupDiagnostics.Stage(
                            $"realtime-presence state={presence.State.ToString().ToLowerInvariant()}");
                        UpdatePresence(
                            presence.RoomId,
                            presence.UserId,
                            presence.State);
                        break;
                    case BackendEvent.TypingChanged typing:
                        // Local activity owns self feedback; delayed echoes/TTL cannot revive it.
                        if (typing.UserId == _state.Profile?.Id)
                            break;
                        if (typing.Active)
                        {
                            _typing.Add((typing.RoomId, typing.UserId));
                        }
                        else
                        {
                            _typing.Remove((typing.RoomId, typing.UserId));
                        }
                        ApplyWorldSnapshot();
                        break;
                    case BackendEvent.CharacterPulsed pulsed:
                        if (_pulseCooldown.Accept(
                            pulsed.Pulse.RoomId,
                            pulsed.Pulse.UserId,
                            TimeSpan.FromSeconds(
                                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)))
                        {
                            QueuePulseForWorld(pulsed.Pulse);
                        }
                        break;
                    case BackendEvent.CharacterThrown thrown:
                        CharacterThrowEvent characterThrow = thrown.Throw;
                        Room? activeRoom = _state.ActiveRoomId is { } activeRoomId
                            ? _state.Rooms.FirstOrDefault(room => room.Id == activeRoomId)
                            : null;
                        if (activeRoom?.Id == characterThrow.RoomId
                            && characterThrow.ActorUserId != characterThrow.TargetUserId
                            && activeRoom.Members.Any(member => member.UserId == characterThrow.ActorUserId)
                            && activeRoom.Members.Any(member => member.UserId == characterThrow.TargetUserId)
                            && _throwCooldown.Accept(
                                characterThrow.RoomId,
                                characterThrow.ActorUserId,
                                TimeSpan.FromSeconds(
                                    Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)))
                        {
                            QueueThrowForWorld(characterThrow);
                        }
                        break;
                    case BackendEvent.ConnectionChanged connection:
                        StartupDiagnostics.Stage(
                            $"realtime-connection transport={connection.Status.TransportConnected.ToString().ToLowerInvariant()} "
                            + $"active-room={connection.Status.ActiveRoomTransportConnected.ToString().ToLowerInvariant()} "
                            + $"reconciled={connection.Status.RecoveryReconciled.ToString().ToLowerInvariant()}");
                        SetRealtimeConnection(connection.Status);
                        break;
                    case BackendEvent.Diagnostic diagnostic:
                        StartupDiagnostics.Stage(diagnostic.Stage);
                        break;
                    case BackendEvent.TechnicalError error:
                        StartupDiagnostics.Stage("realtime-technical-error");
                        SetState(_state with { ErrorMessage = error.Message });
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_roomSession.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("backend-event-pump", exception);
            SetState(_state with
            {
                RealtimeConnection = RealtimeConnectionStatus.Disconnected,
                ErrorMessage = exception.Message,
            });
        }
    }

    private async Task PumpActivityAsync()
    {
        await foreach (PresenceState presence in _activityMonitor.ObserveAsync(_roomSession.Token))
        {
            _localPresence = presence;
            if (_state.Profile is { } profile && _state.ActiveRoomId is { } activeRoomId)
            {
                UpdateMember(activeRoomId, profile.Id, member => member with
                {
                    Presence = _localPresence,
                });
            }

            if (_backend is null || _state.ActiveRoomId is not { } roomId)
            {
                continue;
            }
            try
            {
                await _backend.PublishPresenceAsync(roomId, presence, _roomSession.Token);
            }
            catch when (!_roomSession.IsCancellationRequested)
            {
                SetRealtimeConnection(RealtimeConnectionStatus.Disconnected);
            }
        }
    }

    private void UpdateMember(
        Guid roomId,
        Guid userId,
        Func<RoomMember, RoomMember> update)
    {
        _state = _state with
        {
            Rooms = [.. _state.Rooms.Select(room => room.Id == roomId
                ? room with
                {
                    Members = [.. room.Members.Select(member => member.UserId == userId
                        ? update(member)
                        : member)],
                }
                : room)],
        };
        PublishState();
        ApplyWorldSnapshot();
    }

    private void UpdatePresence(Guid roomId, Guid userId, PresenceState presence)
    {
        (Guid roomId, Guid userId) key = (roomId, userId);
        _basePresence[key] = presence;
        if (presence == PresenceState.Offline)
        {
            _typing.Remove(key);
        }

        UpdateMember(roomId, userId, member => member with
        {
            Presence = LocalPresenceProjection.ForMember(
                userId,
                _state.Profile?.Id ?? Guid.Empty,
                presence,
                _localPresence),
        });
    }

    private void SetRealtimeConnection(RealtimeConnectionStatus status)
    {
        bool activeRoomConnectionChanged =
            status.ActiveRoomTransportConnected != _state.ActiveRoomConnected;
        if (activeRoomConnectionChanged && !status.ActiveRoomTransportConnected)
        {
            _typing.Clear();
        }

        Guid? currentUserId = _state.Profile?.Id;
        IReadOnlyList<Room> rooms = activeRoomConnectionChanged
            ? [.. _state.Rooms.Select(room => room with
            {
                Members = [.. room.Members.Select(member =>
                {
                    (Guid Id, Guid UserId) key = (room.Id, member.UserId);
                    if (status.ActiveRoomTransportConnected)
                    {
                        return member with
                        {
                            Presence = member.UserId == currentUserId
                                ? _localPresence
                                : _basePresence.GetValueOrDefault(key, PresenceState.Offline),
                        };
                    }

                    if (member.Presence == PresenceState.Offline)
                    {
                        return member;
                    }

                    if (member.UserId != currentUserId)
                    {
                        _basePresence[key] = PresenceState.Offline;
                    }
                    return member with { Presence = PresenceState.Reconnecting };
                })],
            })]
            : _state.Rooms;

        SetState(_state with { Rooms = rooms, RealtimeConnection = status });
        _overlay?.ConfigureThrowInteraction(
            _state.Preferences.RequiresRightClickToThrow,
            status.ActiveRoomTransportConnected);
        if (_overlay is null
            && _state.ActiveRoomId is not null
            && _state.Preferences.OverlayVisible)
        {
            StartOverlay(CurrentWorldSnapshot());
        }
        else
        {
            ApplyWorldSnapshot();
        }
    }

    private void ApplySnapshot(BackendSnapshot snapshot)
    {
        if (_treeMovementAccountId != snapshot.CurrentUserId)
        {
            CancelTreeMovementRequest();
            _treeMovement.Clear();
            _treeMovementAccountId = snapshot.CurrentUserId;
        }
        // Both queries may observe different revisions of our own profile.
        foreach (RoomMember member in snapshot.Rooms.SelectMany(room => room.Members))
            MergeTreeMovement(member);
        if (snapshot.Profile is not null)
            snapshot = snapshot with { Profile = MergeTreeMovement(snapshot.Profile) };
        Guid? activeRoomId = SelectActiveRoom(_state.ActiveRoomId, snapshot.Rooms);
        Profile? profile = snapshot.Profile is null
            ? null
            : snapshot.Profile with
            {
                CharacterId = PixelCharacterCatalog.SelectableId(
                    snapshot.Profile.CharacterId,
                    snapshot.ActiveEntitlementKeys),
                EquippedBubbleStyleId = OwnedCosmeticOrNull(
                    snapshot.Profile.EquippedBubbleStyleId,
                    CommerceProductKind.Bubble,
                    snapshot.ActiveEntitlementKeys),
                EquippedThrowableId = OwnedCosmeticOrNull(
                    snapshot.Profile.EquippedThrowableId,
                    CommerceProductKind.Throwable,
                    snapshot.ActiveEntitlementKeys),
            };
        PresenceState? KnownPresence(Guid roomId, Guid userId) =>
            _basePresence.TryGetValue((roomId, userId), out PresenceState presence)
                ? presence
                : null;
        Room[] projectedRooms = [.. snapshot.Rooms.Select(room => room with
        {
            Members = [.. room.Members.Select(member => MergeTreeMovement(member) with
            {
                CharacterId = member.UserId == snapshot.CurrentUserId && profile is not null
                    ? profile.CharacterId
                    : member.CharacterId,
                EquippedBubbleStyleId = member.UserId == snapshot.CurrentUserId && profile is not null
                    ? profile.EquippedBubbleStyleId
                    : CosmeticCatalog.NormalizeBubbleStyleId(member.EquippedBubbleStyleId),
                Presence = LocalPresenceProjection.ForSnapshotMember(
                    member.UserId,
                    snapshot.CurrentUserId,
                    member.Presence,
                    KnownPresence(room.Id, member.UserId),
                    _localPresence),
            })],
        })];
        var validPresenceKeys = projectedRooms
            .SelectMany(room => room.Members.Select(member => (room.Id, member.UserId)))
            .ToHashSet();
        foreach ((Guid RoomId, Guid UserId) key in _basePresence.Keys.Where(key => !validPresenceKeys.Contains(key)).ToArray())
        {
            _basePresence.Remove(key);
        }
        foreach (Room room in snapshot.Rooms)
        {
            foreach (RoomMember member in room.Members)
            {
                _basePresence.TryAdd((room.Id, member.UserId), member.Presence);
            }
        }
        var roomIds = snapshot.Rooms.Select(room => room.Id).ToHashSet();
        foreach (Guid removedRoomId in _unreadByRoom.Keys.Where(id => !roomIds.Contains(id)).ToArray())
        {
            _unreadByRoom.Remove(removedRoomId);
        }
        _state = _state with
        {
            Profile = profile,
            Rooms = projectedRooms,
            ActiveEntitlementKeys = snapshot.ActiveEntitlementKeys,
            ContentLoading = _state.ContentLoading with
            {
                Snapshot = RemoteDataLoadState.Ready,
                Store = _state.DevelopmentCommerceEnabled
                    ? _state.ContentLoading.Store
                    : RemoteDataLoadState.Ready,
            },
            ActiveRoomId = activeRoomId,
            Preferences = _state.Preferences with
            {
                OnboardingCompleted = _state.Preferences.OnboardingCompleted,
                ActiveRoomId = activeRoomId,
                CachedNickname = profile?.Nickname ?? _state.Preferences.CachedNickname,
                CachedCharacterId = profile is null
                    ? _state.Preferences.CachedCharacterId
                    : profile.CharacterId,
            },
        };
        _initialSnapshotReceived = true;
        PublishState();
        if (_overlay is null && activeRoomId is not null && _state.Preferences.OverlayVisible)
            StartOverlay(CurrentWorldSnapshot());
        else
            ApplyWorldSnapshot("server-snapshot");
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        SetState(_state with
        {
            ContentLoading = _state.ContentLoading with
            {
                Snapshot = _state.ContentLoading.Snapshot.Begin(),
                Store = _state.DevelopmentCommerceEnabled
                    ? _state.ContentLoading.Store
                    : _state.ContentLoading.Store.Begin(),
            },
        });
        try
        {
            BackendSnapshot snapshot = await RequiredBackend().FetchSnapshotAsync(cancellationToken);
            await ReconcileSnapshotAsync(snapshot, cancellationToken);
        }
        finally
        {
            SetState(_state with
            {
                ContentLoading = _state.ContentLoading with
                {
                    Snapshot = _state.ContentLoading.Snapshot.EndAttempt(),
                    Store = _state.DevelopmentCommerceEnabled
                        ? _state.ContentLoading.Store
                        : _state.ContentLoading.Store.EndAttempt(),
                },
            });
        }
    }

    private async Task ReconcileSnapshotAsync(
        BackendSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Guid? previousActiveRoomId = _state.ActiveRoomId;
        ApplySnapshot(snapshot);
        StartTreeMovementMigration();
        if (_state.ActiveRoomId != previousActiveRoomId)
        {
            _typingActivity.Stop();
            _typing.Clear();
            _bubbles.Clear();
            _roomSession.SwitchPipeline?.InitializeCommittedRoom(_state.ActiveRoomId);
            if (_state.ActiveRoomId is { } activeRoomId)
            {
                IReadOnlyList<ChatMessage> history = await RequiredBackend().FetchRecentMessagesAsync(
                    activeRoomId,
                    cancellationToken);
                _messages.ReplaceConfirmed(activeRoomId, history);
                _unreadByRoom[activeRoomId] = 0;
            }
            else
            {
                _overlay?.Dispose();
                _overlay = null;
                if (previousActiveRoomId is not null)
                {
                    GroupSetupRequested?.Invoke();
                }
            }
        }
        await RequiredBackend().SynchronizeRealtimeRoomsAsync(
            RoomEpochs(snapshot.Rooms),
            _state.ActiveRoomId,
            _localPresence,
            cancellationToken);
        if (_overlay is null
            && _state.ActiveRoomConnected
            && _state.ActiveRoomId is not null
            && _state.Preferences.OverlayVisible)
        {
            StartOverlay(CurrentWorldSnapshot());
        }
        await PersistPreferencesAsync(cancellationToken);
        PublishState();
        ApplyWorldSnapshot();
    }

    private async Task RefreshSnapshotAndSelectAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await RefreshSnapshotAsync(cancellationToken);
        // Selection completes the creation/join already in progress. Keep its
        // mutation guard held; the public switch action correctly rejects it.
        if (_roomSession.SwitchPipeline is not null
            && _state.ActiveRoomId != roomId
            && _state.Rooms.Any(room => room.Id == roomId))
        {
            await _roomSession.SwitchPipeline.RequestAsync(roomId, cancellationToken);
        }
    }

    private void StartPreviewOverlay(AppPreferences preferences)
    {
        string[] ids = _validationMode
            ? [PixelCharacterCatalog.FallbackId]
            : [.. PixelCharacterCatalog.All.Select(character => character.Id)];
        WorldSnapshot snapshot = PixelWorldPreview.Create(
            ids,
            preferences.InstallationSeed,
            preferences.OverlayRegion.Edge);
        _previewRoomId = snapshot.RoomId;
        _previewUserId = snapshot.Members.FirstOrDefault(member => member.IsCurrentUser)?.Id;
        _previewSnapshot = snapshot;
        if (preferences.OverlayVisible)
        {
            StartOverlay(snapshot, _validationMode ? ids.ToHashSet(StringComparer.Ordinal) : null);
        }
    }

    public void ShowStartupOverlay()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIDEY_WINDOWS_VALIDATION_MODE") == "1")
            return; // The dedicated validation scene selects its own character set.
#endif
        if (!_state.GoogleVerified || _overlay is not null || _initialSnapshotReceived || _previewSnapshot is not null)
            return;
        if (CachedStartupWorld.Create(_state.Preferences) is { } snapshot)
        {
            StartOverlay(snapshot);
            StartupDiagnostics.Stage("overlay-cached-startup state=reconnecting indicator=gray");
        }
    }

    private void StartOverlay(WorldSnapshot snapshot, IReadOnlySet<string>? validationIds = null)
    {
        StopOverlayAudio();
        if (!_state.GoogleVerified || !_state.Preferences.OverlayVisible)
        {
            return;
        }

        _overlay?.Dispose();
        StopOverlayAudio();
        try
        {
            _overlay = NativePixelWorldSession.Start(
                _state.Preferences.OverlayRegion,
                snapshot,
                RequestCharacterClick,
                RequestCharacterPulse,
                RequestCharacterThrow,
                _state.Preferences.RequiresRightClickToThrow,
                OverlayInteractionConnected,
                exception => RenderingFailed?.Invoke(exception),
                new NativePixelWorldSessionOptions(
                    AnimationsEnabled: () => _animations.Enabled,
                    CharacterImpact: PlayOverlayImpact,
                    TreeMovementToggleRequested: roomId => TreeMovementToggleRequested?.Invoke(roomId),
                    ValidationCharacterIds: validationIds,
                    CollectValidationMetrics: validationIds is not null,
                    MessageBubblesPresented: count => StartupDiagnostics.Stage(
                        $"overlay-message-presented count={count}"),
                    Diagnostic: StartupDiagnostics.Stage,
                    DiagnosticFailure: StartupDiagnostics.NonFatal,
                    RendererPerformanceSampled: (average, maximum, frames, skipped) =>
                        StartupDiagnostics.Stage(
                            $"renderer-health frames={frames} "
                            + $"average-ms={average.ToString("F2", CultureInfo.InvariantCulture)} "
                            + $"maximum-ms={maximum.ToString("F2", CultureInfo.InvariantCulture)} "
                            + $"skipped={skipped}")));
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("overlay-window-create", exception);
            throw;
        }
        StartupDiagnostics.Stage("overlay-started");
    }

    private void RestartOverlayForRegionChange()
    {
        WorldSnapshot snapshot = CurrentWorldSnapshot();
        _overlay?.Dispose();
        _overlay = null;
        StartOverlay(snapshot);
    }

    private void ApplyWorldSnapshot(string? diagnosticContext = null)
    {
        bool connected = OverlayInteractionConnected;
        if (_feedbackRoomId != _state.ActiveRoomId || _feedbackConnected != connected)
        {
            _feedbackRoomId = _state.ActiveRoomId;
            _feedbackConnected = connected;
            StopOverlayAudio();
        }
        if (_overlay is null)
        {
            if (diagnosticContext is not null)
            {
                StartupDiagnostics.Stage(
                    $"overlay-snapshot-skipped context={diagnosticContext} reason=not-started");
            }
            return;
        }
        _bubbles.Prune();
        WorldSnapshot snapshot = CurrentWorldSnapshot();
        _overlay.ConfigureThrowInteraction(_state.Preferences.RequiresRightClickToThrow, connected);
        if (diagnosticContext is not null)
        {
            StartupDiagnostics.Stage(
                $"overlay-snapshot-dispatched context={diagnosticContext} visible={_overlay.IsVisible.ToString().ToLowerInvariant()} members={snapshot.Members.Count} bubbles={snapshot.Bubbles.Count}");
        }
        _overlay.Apply(snapshot);
        if (diagnosticContext is not null)
        {
            StartupDiagnostics.Stage($"overlay-snapshot-accepted context={diagnosticContext}");
        }
        _pendingPulses.Clear();
        _pendingThrows.Clear();
    }

    private void QueuePulseForWorld(CharacterPulseEvent pulse)
    {
        if (_overlay is null)
        {
            return;
        }

        _pendingPulses.Add(pulse);
        ApplyWorldSnapshot();
    }

    private void QueueThrowForWorld(CharacterThrowEvent characterThrow)
    {
        if (_overlay is null)
        {
            return;
        }

        _pendingThrows.Add(characterThrow);
        ApplyWorldSnapshot();
    }

    private WorldSnapshot CurrentWorldSnapshot()
    {
        if (_backend is null && _previewSnapshot is { } preview)
        {
            return QuietModeProjection.Apply(preview with
            {
                Pulses = [.. _pendingPulses],
                Throws = [.. _pendingThrows],
                Edge = _state.Preferences.OverlayRegion.Edge,
                InstallationSeed = _state.Preferences.InstallationSeed,
                TreeMovementPaused = _state.Preferences.TreeMovementPaused,
            }, _state.Preferences.QuietMode);
        }

        if (!_initialSnapshotReceived && CachedStartupWorld.Create(_state.Preferences) is { } cached)
            return cached with { TreeMovementPaused = _state.Preferences.TreeMovementPaused };

        Room? room = _state.ActiveRoomId is { } roomId
            ? _state.Rooms.FirstOrDefault(candidate => candidate.Id == roomId)
            : null;
        PixelWorldMember[] members = room?.Members
            .Where(member => member.UserId == _state.Profile?.Id || _state.Preferences.ShowOfflineMembers
                || member.Presence != PresenceState.Offline)
            .Select(member => new PixelWorldMember(
                member.UserId,
                member.Nickname,
                PixelCharacterCatalog.NormalizeId(member.CharacterId),
                LocalPresenceProjection.ForOverlay(member.Presence, _state.ActiveRoomConnected),
                IsTyping: _state.ActiveRoomConnected && room is not null
                    && (member.UserId == _state.Profile?.Id ? IsLocallyTyping(room.Id) : _typing.Contains((room.Id, member.UserId))),
                IsCurrentUser: member.UserId == _state.Profile?.Id,
                EquippedBubbleStyleId: member.EquippedBubbleStyleId,
                TreeMovementPaused: member.TreeMovementPaused,
                TreeMovementRevision: member.TreeMovementRevision))
            .ToArray() ?? [];
        return QuietModeProjection.Apply(new WorldSnapshot(
            room?.Id,
            members,
            _state.Preferences.QuietMode ? [] : _bubbles.Bubbles.ToArray(),
            [.. _pendingPulses],
            [.. _pendingThrows],
            _state.Preferences.OverlayRegion.Edge,
            _state.Preferences.InstallationSeed,
            _state.Preferences.TreeMovementPaused), _state.Preferences.QuietMode);
    }

    private static string? OwnedCosmeticOrNull(
        string? catalogItemId,
        CommerceProductKind kind,
        IReadOnlySet<string> activeEntitlementKeys)
    {
        string? normalized = kind == CommerceProductKind.Bubble
            ? CosmeticCatalog.NormalizeBubbleStyleId(catalogItemId)
            : CosmeticCatalog.NormalizeThrowableId(catalogItemId);
        CommerceProduct? product = normalized is null
            ? null
            : WindowsCommerceCatalog.Products.FirstOrDefault(candidate =>
                candidate.Kind == kind
                && StringComparer.Ordinal.Equals(candidate.EffectiveCatalogItemId, normalized));
        return product is not null && activeEntitlementKeys.Contains(product.EntitlementKey)
            ? normalized
            : null;
    }

    private async Task PersistPreferencesAsync(CancellationToken cancellationToken) =>
        await _preferencesStore.SaveAsync(_state.Preferences, cancellationToken).ConfigureAwait(false);

    private IBackendGateway RequiredBackend() =>
        !_state.GoogleVerified ? throw new InvalidOperationException(I18n.Get("auth.googleRequired")) :
        _backend ?? throw new InvalidOperationException(I18n.Get("error.serverConnectionNotConfigured"));

    private async Task<IReadOnlyList<CommerceProductState>> RefreshDevelopmentCommerceStateAsync(
        CancellationToken cancellationToken,
        string? workingProductId = null)
    {
        if (!_state.DevelopmentCommerceEnabled
            || _backend is not SupabaseBackendGateway backend)
        {
            return _state.CommerceProducts;
        }
        SetState(_state with
        {
            ContentLoading = _state.ContentLoading with { Store = _state.ContentLoading.Store.Begin() },
        });
        IReadOnlyList<CommerceProductState> products;
        try
        {
            products = await backend.GetWindowsCommerceStateAsync(cancellationToken);
        }
        catch
        {
            SetState(_state with
            {
                ContentLoading = _state.ContentLoading with { Store = _state.ContentLoading.Store.EndAttempt() },
            });
            throw;
        }
        IReadOnlyList<CommerceProductState> presentedProducts = workingProductId is null
            ? products
            : [.. products.Select(product =>
                StringComparer.Ordinal.Equals(product.Product.Id, workingProductId)
                    && product.PurchaseState != CommercePurchaseState.Owned
                    ? product with
                    {
                        PurchaseState = CommercePurchaseState.Confirming,
                        IsWorking = true,
                    }
                    : product)];
        SetState(_state with
        {
            CommerceProducts = presentedProducts,
            ContentLoading = _state.ContentLoading with { Store = RemoteDataLoadState.Ready },
            ErrorMessage = null,
        });
        return products;
    }

    private void SetCommerceProductState(CommerceProductState productState)
    {
        SetState(_state with
        {
            CommerceProducts = [.. _state.CommerceProducts.Select(item =>
                StringComparer.Ordinal.Equals(item.Product.Id, productState.Product.Id)
                    ? productState
                    : item)],
        });
    }

    private void EnsureMutationsAvailable()
    {
        if (!_state.GoogleVerified)
            throw new InvalidOperationException(I18n.Get("auth.googleRequired"));
        if (_state.GroupOperation != GroupOperation.Idle)
        {
            throw new InvalidOperationException(I18n.Get("groups.operationBusy"));
        }
    }

    private void SetState(CoordinatorState state)
    {
        _state = state with { Messages = [.. _messages.Entries] };
        StateChanged?.Invoke(_state);
    }

    private void PublishState() => SetState(_state);

    private async Task PersistCommittedRoomAsync()
    {
        try
        {
            await PersistPreferencesAsync(_roomSession.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_roomSession.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetState(_state with { ErrorMessage = I18n.Format("error.preferencesSaveFailed", exception.Message) });
        }
    }

    private bool IsLocallyTyping(Guid roomId)
    {
        lock (_localTypingGate)
            return _localTypingRoom == roomId;
    }

    private static Guid? SelectActiveRoom(Guid? requested, IReadOnlyList<Room> rooms) =>
        requested is { } roomId && rooms.Any(room => room.Id == roomId)
            ? roomId
            : rooms.FirstOrDefault()?.Id;

    private static IReadOnlyDictionary<Guid, long> RoomEpochs(IReadOnlyList<Room> rooms) =>
        rooms.ToDictionary(room => room.Id, room => room.RealtimeEpoch);
}
