using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Presentation.Services;

namespace Sidey.Presentation.Tests;

internal sealed class FakeSideyCoordinator : IMainWindowCoordinator, IHistoryCoordinator
{
    public int GoogleStartCount { get; private set; }
    public Task BeginGoogleAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        GoogleStartCount++;
        State = State with { GoogleAuthentication = GoogleAuthenticationState.SigningIn };
        return Task.CompletedTask;
    }
    public Task CancelGoogleAuthenticationAsync()
    {
        State = State with { GoogleAuthentication = GoogleAuthenticationState.Required };
        return Task.CompletedTask;
    }
    public int ConnectionRetryCount { get; private set; }
    public List<Uri> OpenedExternalUris { get; } = [];
    public Func<Task<string>>? DiagnosticExportHandler { get; set; }
    public Task RetryConnectionAsync(bool userInitiated = true) { ConnectionRetryCount++; return Task.CompletedTask; }
    public bool IsRemoteContentLoading { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public Func<bool, Task>? SoundSettingHandler { get; set; }
    public List<string> PreviewedSounds { get; } = [];
    public int LiveSoundVolume { get; private set; } = 100;
    public List<int> SavedSoundVolumes { get; } = [];
    public Func<int, Task>? SoundVolumeHandler { get; set; }
    public bool LiveSoundEnabled { get; private set; } = true;
    public void ApplyCharacterSoundEffects(bool enabled, int volume) { LiveSoundEnabled = enabled; LiveSoundVolume = volume; }
    public async Task SaveCharacterSoundEffectsAsync(bool enabled, int volume, CancellationToken cancellationToken = default)
    {
        SavedSoundVolumes.Add(volume);
        if (SoundSettingHandler is not null)
            await SoundSettingHandler(enabled);
        if (SoundVolumeHandler is not null)
            await SoundVolumeHandler(volume);
        State = State with { Preferences = State.Preferences with { CharacterSoundEffectsEnabled = enabled, CharacterSoundEffectsVolume = volume } };
    }
    public void PlayImpactSound(string id, Guid scope, long requestedAt) => PreviewedSounds.Add(id);
    public void StopImpactSounds(Guid? scope = null) { }
    public CoordinatorState State { get; set; } = CoordinatorState.Initial with { GoogleAuthentication = GoogleAuthenticationState.Verified };

    public IReadOnlyList<ChatMessage> MessagePage { get; set; } = [];

    public MessageHistoryCursor? NextMessageCursor { get; set; }

    public Func<Guid, MessageHistoryCursor?, int, CancellationToken, Task<MessageHistoryPage>>?
        MessagePageLoader
    { get; set; }

    public bool IsValidationMode => false;

    public string? ValidationMetricsPath => null;

    public ValidationMetricsSnapshot? ValidationMetricsSummary => null;

    public IReadOnlyList<MonitorOption> Monitors { get; set; } = [];

    public int SaveProfileCallCount { get; private set; }

    public string? LastSavedNickname { get; private set; }

    public string? LastSavedCharacterId { get; private set; }

    public Func<string, string, CancellationToken, Task>? SaveProfileHandler { get; set; }

    public int CompleteOnboardingCallCount { get; private set; }

    public Func<CancellationToken, Task>? CompleteOnboardingHandler { get; set; }

    public int CreateRoomCallCount { get; private set; }

    public Func<string, CancellationToken, Task>? CreateRoomHandler { get; set; }

    public int JoinRoomCallCount { get; private set; }

    public Func<string, CancellationToken, Task>? JoinRoomHandler { get; set; }

    public int SwitchRoomCallCount { get; private set; }

    public int RemoveRoomMemberCallCount { get; private set; }

    public int DeleteRoomCallCount { get; private set; }

    public int LeaveRoomCallCount { get; private set; }

    public int SignOutCallCount { get; private set; }

    public int DeleteAccountCallCount { get; private set; }

    public Guid? LastLeftRoomId { get; private set; }

    public Task<MessageHistoryPage> FetchMessagePageAsync(
        Guid roomId,
        MessageHistoryCursor? before,
        int limit = 50,
        CancellationToken cancellationToken = default) => MessagePageLoader is { } loader
            ? loader(roomId, before, limit, cancellationToken)
            : Task.FromResult(new MessageHistoryPage(MessagePage, NextMessageCursor));

    public Task SaveProfileAsync(
        string nickname,
        string characterId,
        CancellationToken cancellationToken = default)
    {
        SaveProfileCallCount++;
        LastSavedNickname = nickname;
        LastSavedCharacterId = characterId;
        return SaveProfileHandler?.Invoke(nickname, characterId, cancellationToken)
            ?? Task.CompletedTask;
    }

    public Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
    {
        CompleteOnboardingCallCount++;
        return CompleteOnboardingHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task CreateRoomAsync(string name, CancellationToken cancellationToken = default)
    {
        CreateRoomCallCount++;
        return CreateRoomHandler?.Invoke(name, cancellationToken) ?? Task.CompletedTask;
    }

    public Task JoinRoomAsync(string inviteCode, CancellationToken cancellationToken = default)
    {
        JoinRoomCallCount++;
        return JoinRoomHandler?.Invoke(inviteCode, cancellationToken) ?? Task.CompletedTask;
    }

    public Task SwitchRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        SwitchRoomCallCount++;
        return Task.CompletedTask;
    }

    public Task RenameRoomAsync(
        Guid roomId,
        string name,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RotateInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveRoomMemberAsync(
        Guid roomId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        RemoveRoomMemberCallCount++;
        return Task.CompletedTask;
    }

    public Task DeleteRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        DeleteRoomCallCount++;
        return Task.CompletedTask;
    }

    public Task LeaveRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        LeaveRoomCallCount++;
        LastLeftRoomId = roomId;
        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOutCallCount++;
        State = CoordinatorState.Initial with
        {
            Preferences = State.Preferences with { OnboardingCompleted = false },
            GoogleAuthentication = GoogleAuthenticationState.Required,
        };
        return Task.CompletedTask;
    }

    public Task DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        DeleteAccountCallCount++;
        State = CoordinatorState.Initial with
        {
            Preferences = State.Preferences with { OnboardingCompleted = false },
            GoogleAuthentication = GoogleAuthenticationState.Required,
        };
        return Task.CompletedTask;
    }

    public Task<bool> CopyInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetQuietModeAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public int SetGlobalHotkeysCallCount { get; private set; }
    public Func<GlobalHotkeySettings, Task>? GlobalHotkeysHandler { get; set; }
    public async Task SetGlobalHotkeysAsync(
        GlobalHotkeySettings settings,
        CancellationToken cancellationToken = default)
    {
        SetGlobalHotkeysCallCount++;
        if (GlobalHotkeysHandler is not null)
            await GlobalHotkeysHandler(settings);
        State = State with { Preferences = State.Preferences with { GlobalHotkeys = settings } };
    }

    public Task SetShowOfflineMembersAsync(
        bool enabled,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetRequiresRightClickToThrowAsync(
        bool enabled,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetStartAtLoginAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public int SetLanguageCallCount { get; private set; }
    public Task SetLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        SetLanguageCallCount++;
        State = State with { Preferences = State.Preferences with { Language = language } };
        return Task.CompletedTask;
    }

    public int SetThemeCallCount { get; private set; }
    public Task SetThemeAsync(AppThemePreference theme, CancellationToken cancellationToken = default)
    {
        SetThemeCallCount++;
        State = State with { Preferences = State.Preferences with { Theme = theme } };
        return Task.CompletedTask;
    }

    public Task SetRegionAsync(
        OverlayRegionPreference preference,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public int ActivateStoreProductCallCount { get; private set; }

    public Task ActivateStoreProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        _ = productId;
        ActivateStoreProductCallCount++;
        return Task.CompletedTask;
    }

    public int RefreshStoreCallCount { get; private set; }

    public Task RefreshStoreAsync(CancellationToken cancellationToken = default)
    {
        RefreshStoreCallCount++;
        return Task.CompletedTask;
    }

    public CommerceProductKind? LastEquippedCosmeticKind { get; private set; }
    public string? LastEquippedCosmeticId { get; private set; }
    public int SetEquippedCosmeticCallCount { get; private set; }
    public Func<CommerceProductKind, string?, CancellationToken, Task>?
        SetEquippedCosmeticHandler
    { get; set; }

    public Task SetEquippedCosmeticAsync(
        CommerceProductKind kind,
        string? catalogItemId,
        CancellationToken cancellationToken = default)
    {
        SetEquippedCosmeticCallCount++;
        LastEquippedCosmeticKind = kind;
        LastEquippedCosmeticId = catalogItemId;
        return SetEquippedCosmeticHandler?.Invoke(kind, catalogItemId, cancellationToken)
            ?? Task.CompletedTask;
    }

    public Task CompleteGoogleIdentityLinkAsync(
        Uri callbackUri,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> RecoverGoogleIdentityLinkAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public IReadOnlyList<MonitorOption> GetMonitors() => Monitors;

    public void RequestComposer()
    {
    }

    public Task<string?> ExportValidationMetricsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string> ExportDiagnosticDataAsync(CancellationToken cancellationToken = default) =>
        DiagnosticExportHandler?.Invoke() ?? Task.FromResult("C:\\Desktop\\SIDEY-Diagnostics.zip");

    public Task OpenExternalUriAsync(Uri uri)
    {
        OpenedExternalUris.Add(uri);
        return Task.CompletedTask;
    }
}

internal sealed class FakeMainWindowDialogService : IMainWindowDialogService
{
    public bool ConfirmUpdateDownload { get; set; } = true;

    public bool ConfirmMemberRemoval { get; set; } = true;

    public bool ConfirmRoomDeletion { get; set; } = true;

    public bool ConfirmRoomLeave { get; set; } = true;

    public bool ConfirmSignOut { get; set; } = true;

    public bool ConfirmAccountDeletion { get; set; } = true;

    public string? ConfirmedRemovalNickname { get; private set; }

    public string? ConfirmedLeaveRoomName { get; private set; }

    public bool? ConfirmedLeaveRoomIsOwner { get; private set; }

    public string? ConfirmedUpdateVersion { get; private set; }

    public Task<bool> ConfirmInviteCodeRotationAsync() => Task.FromResult(true);

    public Task<string?> PromptForRoomNameAsync(string currentName) =>
        Task.FromResult<string?>(currentName);

    public Task<bool> ConfirmMemberRemovalAsync(string nickname)
    {
        ConfirmedRemovalNickname = nickname;
        return Task.FromResult(ConfirmMemberRemoval);
    }

    public Task<bool> ConfirmRoomDeletionAsync(string roomName) =>
        Task.FromResult(ConfirmRoomDeletion);

    public Task<bool> ConfirmRoomLeaveAsync(string roomName, bool isOwner)
    {
        ConfirmedLeaveRoomName = roomName;
        ConfirmedLeaveRoomIsOwner = isOwner;
        return Task.FromResult(ConfirmRoomLeave);
    }

    public Task<bool> ConfirmSignOutAsync() => Task.FromResult(ConfirmSignOut);

    public Task<bool> ConfirmAccountDeletionAsync() => Task.FromResult(ConfirmAccountDeletion);

    public Task<bool> ConfirmUpdateDownloadAsync(string version)
    {
        ConfirmedUpdateVersion = version;
        return Task.FromResult(ConfirmUpdateDownload);
    }
}

internal sealed class FakeUpdateService : IUpdateService
{
    public UpdatePreferences Preferences { get; private set; } = new(AutoInstall: false);

    public bool IsAutomaticUpdateConnectionAllowed { get; set; } = true;

    public void SavePreferences(UpdatePreferences preferences) => Preferences = preferences;

    public string CurrentVersion { get; set; } = "1.0.6";

    public string CurrentUpdateVersion { get; set; } = "1.0.6";

    public DateTimeOffset? LastCheckedAt { get; set; }

    public Uri CurrentReleaseNotesUri { get; set; } = new(
        "https://github.com/sidey-app/SIDEY/releases/tag/windows-v1.0.6");

    public AvailableUpdate? AvailableUpdate { get; set; }

    public bool InstallerReady { get; set; }

    public Task<bool> IsInstallerReadyAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default) => Task.FromResult(InstallerReady);

    public int InstallerLaunchCount { get; private set; }

    public bool LastInstallerLaunchWasSilent { get; private set; }

    public int DownloadCount { get; private set; }

    public int ReleaseNotesLaunchCount { get; private set; }

    public Func<AvailableUpdate, IProgress<int>?, CancellationToken, Task>? DownloadHandler { get; set; }

    public async Task DownloadInstallerAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        DownloadCount++;
        if (DownloadHandler is not null)
            await DownloadHandler(update, progress, cancellationToken);
        InstallerReady = true;
    }

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        LastCheckedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(AvailableUpdate);
    }

    public Task DownloadAndLaunchInstallerAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null,
        bool silent = false)
    {
        _ = update;
        InstallerLaunchCount++;
        LastInstallerLaunchWasSilent = silent;
        return DownloadInstallerAsync(update, cancellationToken, progress);
    }

    public Task OpenReleaseNotesAsync(Uri releaseNotesUri)
    {
        _ = releaseNotesUri;
        ReleaseNotesLaunchCount++;
        return Task.CompletedTask;
    }
}
