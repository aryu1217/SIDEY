using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.Presentation.Tests;

[Collection("Language refresh")]
public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task AccountActionsRequireConfirmationAndInvokeOnlyTheSelectedAction()
    {
        var coordinator = new FakeSideyCoordinator();
        var dialogs = new FakeMainWindowDialogService
        {
            ConfirmSignOut = false,
            ConfirmAccountDeletion = false,
        };
        var model = new MainWindowViewModel(coordinator, dialogs, new FakeUpdateService());

        await model.SignOutCommand.ExecuteAsync(null);
        await model.DeleteAccountCommand.ExecuteAsync(null);
        Assert.Equal(0, coordinator.SignOutCallCount);
        Assert.Equal(0, coordinator.DeleteAccountCallCount);

        dialogs.ConfirmSignOut = true;
        await model.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(1, coordinator.SignOutCallCount);
        Assert.Equal(0, coordinator.DeleteAccountCallCount);

        coordinator.State = coordinator.State with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
        };
        model.ApplyState(coordinator.State);
        dialogs.ConfirmAccountDeletion = true;
        await model.DeleteAccountCommand.ExecuteAsync(null);
        Assert.Equal(1, coordinator.DeleteAccountCallCount);
    }

    [Fact]
    public void AccountActionsAreDisabledDuringAnotherGroupMutation()
    {
        var coordinator = new FakeSideyCoordinator();
        var model = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        model.ApplyState(coordinator.State with { GroupOperation = GroupOperation.Mutating });

        Assert.False(model.SignOutCommand.CanExecute(null));
        Assert.False(model.DeleteAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectionStatusRetriesWhileDisconnectedAndDisablesAfterConnection()
    {
        var coordinator = new FakeSideyCoordinator();
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        Assert.True(model.RetryConnectionCommand.CanExecute(null));
        await model.RetryConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, coordinator.ConnectionRetryCount);
        model.IsConnected = true;
        Assert.False(model.RetryConnectionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("https://github.com/sidey-app/SIDEY/issues/new/choose")]
    [InlineData("ms-settings:colors")]
    public async Task InformationLinksOpenTheExactRequestedAddress(string address)
    {
        var coordinator = new FakeSideyCoordinator();
        var model = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        await model.OpenExternalLinkCommand.ExecuteAsync(address);

        Assert.Equal(
            new Uri(address),
            Assert.Single(coordinator.OpenedExternalUris));
    }

    [Fact]
    public async Task DiagnosticExportFailureReportsAStableErrorAndAllowsRetry()
    {
        var coordinator = new FakeSideyCoordinator
        {
            DiagnosticExportHandler = () => Task.FromException<string>(
                new IOException("private path must not be shown")),
        };
        var model = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        NoticeMessage? notice = null;
        model.NoticeRaised += value => notice = value;

        await model.ExportDiagnosticDataCommand.ExecuteAsync(null);

        Assert.Equal(NoticeKind.Error, notice?.Kind);
        Assert.Equal("진단 데이터를 내보내지 못했습니다. 다시 시도해 주세요.", notice?.Message);
        Assert.True(model.ExportDiagnosticDataCommand.CanExecute(null));
    }
    [Fact]
    public async Task MuteButtonPreservesVolumeAndRestoresItAfterZero()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService())
        {
            CharacterSoundEffectsVolume = 37
        };
        model.ToggleCharacterSoundMuteCommand.Execute(null);
        Assert.False(coordinator.LiveSoundEnabled);
        Assert.Equal(37, model.CharacterSoundEffectsVolume);
        model.ToggleCharacterSoundMuteCommand.Execute(null);
        Assert.True(coordinator.LiveSoundEnabled);
        Assert.Equal(37, model.CharacterSoundEffectsVolume);
        model.CharacterSoundEffectsVolume = 0;
        model.ToggleCharacterSoundMuteCommand.Execute(null);
        Assert.True(coordinator.LiveSoundEnabled);
        Assert.Equal(37, model.CharacterSoundEffectsVolume);
        await model.FlushSoundSettingsAsync();
        Assert.Empty(coordinator.PreviewedSounds);
    }

    [Fact]
    public async Task SoundSettingFailureRestoresToggle()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SoundSettingHandler = _ => completion.Task;
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService())
        {
            CharacterSoundEffectsEnabled = false
        };
        Assert.False(coordinator.LiveSoundEnabled);
        Task saving = model.FlushSoundSettingsAsync();
        completion.SetException(new IOException("Cannot save settings"));
        await saving.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.CharacterSoundEffectsEnabled);
        Assert.True(coordinator.LiveSoundEnabled);
    }

    [Fact]
    public async Task SoundVolumeUpdatesLiveAndCoalescesSettingsWrites()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService())
        {
            CharacterSoundEffectsVolume = 75
        };
        model.CharacterSoundEffectsVolume = 40;
        model.CharacterSoundEffectsVolume = 0;
        Assert.Equal(0, coordinator.LiveSoundVolume);
        Assert.Empty(coordinator.SavedSoundVolumes);
        model.ApplyState(state);
        Assert.Equal(0, model.CharacterSoundEffectsVolume);
        await model.FlushSoundSettingsAsync();
        Assert.Equal(0, Assert.Single(coordinator.SavedSoundVolumes));
        Assert.Equal(0, coordinator.State.Preferences.CharacterSoundEffectsVolume);
        Assert.Empty(coordinator.PreviewedSounds);
    }

    [Fact]
    public async Task SoundVolumeSaveFailureRestoresSavedVolumeAndReportsFailure()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with { Preferences = state.Preferences with { CharacterSoundEffectsVolume = 60 } };
        coordinator.SoundVolumeHandler = _ => Task.FromException(new IOException("Cannot save volume"));
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        NoticeMessage? notice = null;
        model.NoticeRaised += value => notice = value;
        model.CharacterSoundEffectsVolume = 20;
        await model.FlushSoundSettingsAsync();
        Assert.Equal(60, model.CharacterSoundEffectsVolume);
        Assert.Equal(60, coordinator.LiveSoundVolume);
        Assert.Equal(NoticeKind.Error, notice?.Kind);
    }

    [Fact]
    public async Task LateVolumeSaveFailureDoesNotOverwriteNewerSliderValue()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SoundVolumeHandler = volume =>
        {
            if (volume != 80)
                return Task.CompletedTask;
            firstStarted.TrySetResult();
            return firstSave.Task;
        };
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        var notices = new List<NoticeMessage>();
        model.NoticeRaised += notices.Add;
        model.CharacterSoundEffectsVolume = 80;
        Task saving = model.FlushSoundSettingsAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        model.CharacterSoundEffectsVolume = 25;
        firstSave.SetException(new IOException("Older write failed"));
        await saving.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(25, model.CharacterSoundEffectsVolume);
        Assert.Equal(25, coordinator.LiveSoundVolume);
        Assert.Equal(25, coordinator.State.Preferences.CharacterSoundEffectsVolume);
        Assert.Equal(new[] { 80, 25 }, coordinator.SavedSoundVolumes);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task SliderLinksMuteAndOnlyUserVolumeChangesPlayDefaultImpact()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        model.ApplyState(state with { Preferences = state.Preferences with { CharacterSoundEffectsVolume = 60 } });
        Assert.Empty(coordinator.PreviewedSounds);
        model.CharacterSoundEffectsVolume = 0;
        Assert.False(model.CharacterSoundEffectsEnabled);
        Assert.False(coordinator.LiveSoundEnabled);
        Assert.Empty(coordinator.PreviewedSounds);
        await model.FlushSoundSettingsAsync();
        Assert.False(coordinator.State.Preferences.CharacterSoundEffectsEnabled);
        model.CharacterSoundEffectsVolume = 25;
        Assert.True(model.CharacterSoundEffectsEnabled);
        Assert.True(coordinator.LiveSoundEnabled);
        Assert.Empty(coordinator.PreviewedSounds);
        await model.FlushSoundSettingsAsync();
        Assert.True(coordinator.State.Preferences.CharacterSoundEffectsEnabled);
        Assert.Empty(coordinator.PreviewedSounds);
        model.CompleteSoundVolumeAdjustment();
        model.CompleteSoundVolumeAdjustment();
        Assert.Equal("patch_soft_ball", Assert.Single(coordinator.PreviewedSounds));
        model.CharacterSoundEffectsVolume = 0;
        model.CompleteSoundVolumeAdjustment();
        Assert.Single(coordinator.PreviewedSounds);
        await model.FlushSoundSettingsAsync();
    }

    [Fact]
    public async Task ManualMutePreservesVolumeAndUnmutingZeroRestoresLastPositiveVolume()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService())
        {
            CharacterSoundEffectsVolume = 45,
            CharacterSoundEffectsEnabled = false
        };
        await model.FlushSoundSettingsAsync();
        Assert.Equal(45, coordinator.State.Preferences.CharacterSoundEffectsVolume);
        Assert.False(coordinator.State.Preferences.CharacterSoundEffectsEnabled);
        model.CharacterSoundEffectsVolume = 0;
        model.CharacterSoundEffectsEnabled = true;
        await model.FlushSoundSettingsAsync();
        Assert.Equal(45, model.CharacterSoundEffectsVolume);
        Assert.True(coordinator.State.Preferences.CharacterSoundEffectsEnabled);
        Assert.Equal(45, coordinator.LiveSoundVolume);
    }

    [Fact]
    public void AnimationChangeUpdatesExistingSelectionItems()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        CharacterSelectionItemViewModel first = model.CharacterSelections[0];
        coordinator.AnimationsEnabled = false;
        model.RefreshFeedbackPresentation();
        Assert.Same(first, model.CharacterSelections[0]);
        Assert.All(model.CharacterSelections, item => Assert.False(item.AnimationsEnabled));
        Assert.All(model.BubbleSelections, item => Assert.False(item.AnimationsEnabled));
    }
    [Fact]
    public async Task PendingCharacterRetainsConfirmedCheckAndFailurePreservesDraft()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SaveProfileHandler = (_, _, _) => completion.Task;
        var model = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.IsSavingCharacter) && !model.IsSavingCharacter)
                finished.TrySetResult();
        };
        model.Nickname = "my-draft";
        model.SelectedCharacterId = "pixel_cat";
        Assert.True(model.CharacterSelections.Single(x => x.Id == "pixel_hamster").IsSelected);
        Assert.True(model.CharacterSelections.Single(x => x.Id == "pixel_cat").IsPending);
        Assert.All(model.CharacterSelections, x => Assert.False(x.IsEnabled));
        model.ApplyState(state);
        Assert.True(model.CharacterSelections.Single(x => x.Id == "pixel_cat").IsPending);
        completion.SetException(new InvalidOperationException("failed"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("my-draft", model.Nickname);
        Assert.All(model.CharacterSelections, x => Assert.False(x.IsPending));
        Assert.True(model.CharacterSelections.Single(x => x.Id == "pixel_hamster").IsSelected);
    }

    [Theory]
    [InlineData("en-US", "3.50", "3,50")]
    [InlineData("ja-JP", "3.50", "3,50")]
    [InlineData("zh-CN", "3.50", "3,50")]
    [InlineData("zh-TW", "3.50", "3,50")]
    [InlineData("uk-UA", "3,50", "3.50")]
    [InlineData("ru-RU", "3,50", "3.50")]
    public void NumericFormattingUsesSelectedLanguageWithoutChangingWindowsCulture(
        string language,
        string expected,
        string unexpected)
    {
        string previousLanguage = Sidey.Core.Localization.I18n.Language;
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Sidey.Core.Localization.I18n.SetLanguage(language);
            Assert.Equal(language, Sidey.Core.Localization.I18n.Culture.Name);
            string text = Sidey.Core.Localization.I18n.Format("renderer_metrics.summary", 1, 2, 3.5, 4.5, 5.5, 6, 7);
            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.DoesNotContain(unexpected, text, StringComparison.Ordinal);
            Assert.Equal("fr-FR", System.Globalization.CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            Sidey.Core.Localization.I18n.SetLanguage(previousLanguage);
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task LanguageChangesPreserveSystemDateTimeFormats(int daysAgo)
    {
        string previousLanguage = Sidey.Core.Localization.I18n.Language;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            // Include custom Windows regional formats, not just a locale's defaults.
            var systemCulture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            systemCulture.DateTimeFormat.ShortDatePattern = "yyyy/MM/dd";
            systemCulture.DateTimeFormat.ShortTimePattern = "HH.mm";
            CultureInfo.CurrentCulture = systemCulture;
            DateTimeOffset timestamp = new(DateTime.Today.AddDays(-daysAgo).AddHours(13).AddMinutes(24));
            DateTimeOffset messageTimestamp = DateTimeOffset.Now.AddMinutes(-1);
            (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
            coordinator.MessagePage =
            [
                new ChatMessage(Guid.NewGuid(), state.Rooms[0].Id, state.Profile!.Id, "test", messageTimestamp.ToUniversalTime()),
            ];
            var updates = new FakeUpdateService { LastCheckedAt = timestamp.ToUniversalTime() };
            var main = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), updates);
            var history = new HistoryWindowViewModel(coordinator);
            await history.ActivateAsync();

            foreach (string language in Sidey.Core.Localization.I18n.SupportedLanguages.Append("ko-KR"))
            {
                Sidey.Core.Localization.I18n.SetLanguage(language);
                main.RefreshLocalizedText();
                history.RefreshLocalizedText();
                string display = daysAgo switch
                {
                    0 => Sidey.Core.Localization.I18n.Format("settings.update.last_checked.today", timestamp.ToString("t", systemCulture)),
                    1 => Sidey.Core.Localization.I18n.Format("settings.update.last_checked.yesterday", timestamp.ToString("t", systemCulture)),
                    _ => timestamp.ToString("g", systemCulture),
                };
                Assert.Equal(Sidey.Core.Localization.I18n.Format("settings.update.last_checked.value", display), main.LastUpdateCheckText);
                Assert.Equal(messageTimestamp.ToString("g", systemCulture), Assert.Single(history.Items).LocalTimeText);
                Assert.Same(systemCulture, CultureInfo.CurrentCulture);
            }
        }
        finally
        {
            Sidey.Core.Localization.I18n.SetLanguage(previousLanguage);
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void LanguageRefreshUpdatesExistingItemsAndPreservesDraftsAndFilters()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state;
        var viewModel = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        CharacterSelectionItemViewModel character = viewModel.CharacterSelections[0];
        CosmeticSelectionItemViewModel bubble = viewModel.BubbleSelections[0];
        StoreProductPreviewViewModel product = viewModel.StoreProducts[0];
        viewModel.Nickname = "draft";
        viewModel.InviteCode = "ABCDEF";
        viewModel.CreateRoomName = "room draft";
        viewModel.SelectedStoreKindIndex = 1;
        viewModel.SelectedStoreSortIndex = 2;
        string previous = Sidey.Core.Localization.I18n.Language;
        try
        {
            foreach (string language in Sidey.Core.Localization.I18n.SupportedLanguages.Skip(1).Append("ko-KR"))
            {
                Sidey.Core.Localization.I18n.SetLanguage(language);
                viewModel.RefreshLocalizedText();
                Assert.Same(character, viewModel.CharacterSelections[0]);
                Assert.Same(bubble, viewModel.BubbleSelections[0]);
                Assert.Same(product, viewModel.StoreProducts[0]);
                Assert.All(viewModel.StoreProducts, item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Description));
                    Assert.DoesNotContain("store.catalog.", item.Description, StringComparison.Ordinal);
                    Assert.DoesNotContain("store.catalog.", item.DisplayName, StringComparison.Ordinal);
                });
                Assert.Equal(PixelCharacterCatalog.Get(character.Id).DisplayName, character.DisplayName);
                Assert.Equal(Sidey.Core.Localization.I18n.Get("profile.cosmetics.bubble.default"), bubble.DisplayName);
                Assert.Equal("draft", viewModel.Nickname);
                Assert.Equal("ABCDEF", viewModel.InviteCode);
                Assert.Equal("room draft", viewModel.CreateRoomName);
                Assert.Equal(1, viewModel.SelectedStoreKindIndex);
                Assert.Equal(2, viewModel.SelectedStoreSortIndex);
            }
        }
        finally { Sidey.Core.Localization.I18n.SetLanguage(previous); }
    }

    public static IEnumerable<object[]> SupportedLanguageSelections =>
        Sidey.Core.Localization.I18n.SupportedLanguages
            .Select((language, index) => new object[] { index, language });

    [Theory]
    [MemberData(nameof(SupportedLanguageSelections))]
    public void LanguageSelectionIsRestoredWithoutSavingAndPersistsUserChoice(int index, string language)
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with { Preferences = state.Preferences with { Language = language } };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        Assert.Equal(index, viewModel.SelectedLanguageIndex);
        Assert.Equal(0, coordinator.SetLanguageCallCount);

        int next = (index + 1) % Sidey.Core.Localization.I18n.SupportedLanguages.Count;
        viewModel.SelectedLanguageIndex = next;
        Assert.Equal(1, coordinator.SetLanguageCallCount);
        Assert.Equal(
            Sidey.Core.Localization.I18n.SupportedLanguages[next],
            coordinator.State.Preferences.Language);
        Assert.True(viewModel.IsLanguageSelectionEnabled);
        Assert.Equal(next, viewModel.SelectedLanguageIndex);
    }

    [Theory]
    [InlineData(AppThemePreference.System, 0)]
    [InlineData(AppThemePreference.Light, 1)]
    [InlineData(AppThemePreference.Dark, 2)]
    public void ThemeSelectionIsRestoredWithoutSavingAndPersistsUserChoice(
        AppThemePreference theme,
        int index)
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with { Preferences = state.Preferences with { Theme = theme } };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        Assert.Equal(index, viewModel.SelectedThemeIndex);
        Assert.Equal(0, coordinator.SetThemeCallCount);

        int next = (index + 1) % 3;
        viewModel.SelectedThemeIndex = next;

        Assert.Equal(1, coordinator.SetThemeCallCount);
        Assert.Equal((AppThemePreference)next, coordinator.State.Preferences.Theme);
        Assert.True(viewModel.IsThemeSelectionEnabled);
        Assert.Equal(next, viewModel.SelectedThemeIndex);
    }

    [Fact]
    public void HotkeyRecordingSwapsAnOccupiedBindingAndPersistsOneCompleteMapping()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with
        {
            Preferences = state.Preferences with { GlobalHotkeys = GlobalHotkeySettings.Default },
        };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        Assert.Equal("Ctrl + Alt + H", viewModel.OverlayHotkeyText);
        Assert.Equal("Ctrl + Alt + I", viewModel.ComposerHotkeyText);
        Assert.Contains("Ctrl + Alt + H", viewModel.OverlayHotkeyAccessibleName, StringComparison.Ordinal);
        Assert.Equal(0, coordinator.SetGlobalHotkeysCallCount);

        viewModel.BeginHotkeyRecording(GlobalHotkeyAction.ToggleOverlay);
        Assert.True(viewModel.IsHotkeyRecording(GlobalHotkeyAction.ToggleOverlay));
        Assert.Contains(I18n.Get("settings.shortcuts.recorder.recording"), viewModel.OverlayHotkeyAccessibleName, StringComparison.Ordinal);
        viewModel.AssignGlobalHotkey(
            GlobalHotkeyAction.ToggleOverlay,
            GlobalHotkeyBinding.FromLegacy(GlobalHotkeyKey.I));

        Assert.Equal(1, coordinator.SetGlobalHotkeysCallCount);
        Assert.Equal(GlobalHotkeyKey.I, coordinator.State.Preferences.GlobalHotkeys.ToggleOverlay);
        Assert.Equal(GlobalHotkeyKey.H, coordinator.State.Preferences.GlobalHotkeys.Compose);
        Assert.Equal("Ctrl + Alt + I", viewModel.OverlayHotkeyText);
        Assert.Equal("Ctrl + Alt + H", viewModel.ComposerHotkeyText);
        Assert.False(viewModel.IsHotkeyRecording(GlobalHotkeyAction.ToggleOverlay));
        Assert.True(viewModel.IsHotkeySelectionEnabled);
    }

    [Fact]
    public void HotkeyEditorPreviewsIndividualKeysAndAnOccupiedShortcutBeforeSaving()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        Assert.Equal(["Ctrl", "Alt", "I"], viewModel.ComposerHotkeyKeys);
        GlobalHotkeyBinding composerBinding = viewModel.HotkeyBindingFor(GlobalHotkeyAction.Compose);
        Assert.Equal(
            GlobalHotkeyAction.Compose,
            viewModel.ConflictingHotkeyAction(GlobalHotkeyAction.ToggleOverlay, composerBinding));
        Assert.Null(viewModel.ConflictingHotkeyAction(GlobalHotkeyAction.Compose, composerBinding));
        Assert.Equal(0, coordinator.SetGlobalHotkeysCallCount);

        viewModel.BeginHotkeyRecording(GlobalHotkeyAction.ToggleOverlay);
        viewModel.CancelHotkeyRecording(GlobalHotkeyAction.ToggleOverlay);

        Assert.Equal(["Ctrl", "Alt", "H"], viewModel.OverlayHotkeyKeys);
        Assert.Equal(0, coordinator.SetGlobalHotkeysCallCount);
    }

    [Fact]
    public void HotkeyEditorCanDisableOneActionAndDisplayItsDisabledState()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        viewModel.BeginHotkeyRecording(GlobalHotkeyAction.Compose);
        viewModel.AssignGlobalHotkey(GlobalHotkeyAction.Compose, GlobalHotkeyBinding.Disabled);

        Assert.Equal(1, coordinator.SetGlobalHotkeysCallCount);
        Assert.Equal(
            GlobalHotkeyBinding.Disabled,
            coordinator.State.Preferences.GlobalHotkeys.BindingFor(GlobalHotkeyAction.Compose));
        Assert.Equal("—", viewModel.ComposerHotkeyText);
        Assert.Equal(["—"], viewModel.ComposerHotkeyKeys);
        Assert.Contains(I18n.Get("common.delete"), viewModel.ComposerHotkeyAccessibleName);
        Assert.Null(viewModel.ConflictingHotkeyAction(
            GlobalHotkeyAction.ToggleOverlay, GlobalHotkeyBinding.Disabled));
    }

    [Fact]
    public async Task SettingsFlushWaitsForPendingHotkeySave()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var saveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.GlobalHotkeysHandler = _ => saveCompletion.Task;
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        viewModel.BeginHotkeyRecording(GlobalHotkeyAction.ToggleOverlay);
        viewModel.AssignGlobalHotkey(
            GlobalHotkeyAction.ToggleOverlay,
            GlobalHotkeyBinding.FromLegacy(GlobalHotkeyKey.O));
        Task flush = viewModel.FlushSettingsAsync();

        Assert.False(viewModel.IsHotkeySelectionEnabled);
        Assert.False(flush.IsCompleted);

        saveCompletion.SetResult();
        await flush;

        Assert.True(viewModel.IsHotkeySelectionEnabled);
        Assert.Equal(GlobalHotkeyKey.O, coordinator.State.Preferences.GlobalHotkeys.ToggleOverlay);
    }

    [Fact]
    public void CharacterPickerKeepsTheFiveFreeWindowsSelections()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal(
            ["pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit", "pixel_penguin"],
            viewModel.CharacterSelections.Select(character => character.Id));
    }

    [Fact]
    public void StorePreviewsAllThirtyThreeProductsWithoutAddingPaidCharactersToThePicker()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal(33, viewModel.StoreProducts.Count);
        Assert.Equal(12, viewModel.StoreProducts.Count(product => product.Kind == CommerceProductKind.Character));
        Assert.Equal(3, viewModel.StoreProducts.Count(product => product.Kind == CommerceProductKind.Bubble));
        Assert.Equal(18, viewModel.StoreProducts.Count(product => product.Kind == CommerceProductKind.Throwable));
        Assert.All(viewModel.StoreProducts, product => Assert.NotEmpty(product.Description));
        Assert.DoesNotContain(
            viewModel.StoreProducts.Where(product => product.Kind == CommerceProductKind.Character).Select(product => product.CharacterId),
            characterId => viewModel.CharacterSelections.Any(character => character.Id == characterId));
    }

    [Fact]
    public void DisabledCommerceKeepsAllStoreActionsLocked()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.All(viewModel.StoreProducts, product =>
        {
            Assert.True(product.IsPreviewOnlyVisible);
            Assert.False(product.IsActionEnabled);
            Assert.Equal("구매 준비 중", product.ActionText);
        });
    }

    [Fact]
    public void EnablingCommerceRemovesPreviewOnlyNoticeAndUpdatesOpenProductDetails()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        StoreProductPreviewViewModel product = viewModel.StoreProducts[0];
        Assert.True(viewModel.IsStorePreviewOnly);
        Assert.Equal("구매 준비 중", product.DetailStatusText);
        var changes = new List<string?>();
        product.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.ApplyState(state with
        {
            DevelopmentCommerceEnabled = true,
            CommerceProducts = [.. WindowsCommerceCatalog.Products.Select(item =>
                new CommerceProductState(item, GoogleConnected: true, CommercePurchaseState.Available))],
        });

        Assert.False(viewModel.IsStorePreviewOnly);
        Assert.False(product.IsPreviewOnlyVisible);
        Assert.True(product.ActionCommand.CanExecute(null));
        Assert.Equal(product.ActionText, product.DetailStatusText);
        Assert.Contains(product.FormattedPrice, product.DetailStatusText, StringComparison.Ordinal);
        Assert.Contains(nameof(product.DetailStatusText), changes);

        viewModel.ApplyState(state);

        Assert.True(viewModel.IsStorePreviewOnly);
        Assert.True(product.IsPreviewOnlyVisible);
        Assert.False(product.ActionCommand.CanExecute(null));
        Assert.Equal("구매 준비 중", product.DetailStatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableStoreActionRefreshesTheCatalogWithoutCreatingAnOrder(bool missingProductState)
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with
        {
            DevelopmentCommerceEnabled = true,
            CommerceProducts = missingProductState ? [] : WindowsCommerceCatalog.LockedStates(),
        };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        StoreProductPreviewViewModel product = viewModel.StoreProducts[0];
        var notices = new List<NoticeMessage>();
        viewModel.NoticeRaised += notices.Add;

        Assert.False(product.IsPreviewOnlyVisible);
        Assert.Equal("다시 시도", product.ActionText);
        Assert.Equal("다시 시도", product.DetailStatusText);
        Assert.True(product.ActionCommand.CanExecute(null));

        await product.ActionCommand.ExecuteAsync(null);

        Assert.Equal(1, coordinator.RefreshStoreCallCount);
        Assert.Equal(0, coordinator.ActivateStoreProductCallCount);
        Assert.Empty(notices);
    }

    [Theory]
    [InlineData(990, "990원", "₩990")]
    [InlineData(1900, "1,900원", "₩1,900")]
    [InlineData(2900, "2,900원", "₩2,900")]
    public void ServerPriceUpdatesExistingStoreCardsAndSurvivesLanguageRefresh(
        int serverPrice, string koreanPrice, string englishPrice)
    {
        string previous = Sidey.Core.Localization.I18n.Language;
        try
        {
            Sidey.Core.Localization.I18n.SetLanguage("ko-KR");
            (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
            var viewModel = new MainWindowViewModel(
                coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
            StoreProductPreviewViewModel product = viewModel.StoreProducts[0];
            var changes = new List<string?>();
            product.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            coordinator.State = state with
            {
                DevelopmentCommerceEnabled = true,
                CommerceProducts = [.. WindowsCommerceCatalog.Products.Select(item =>
                    new CommerceProductState(item with { AmountKrw = serverPrice },
                        GoogleConnected: true, CommercePurchaseState.Available))],
            };

            viewModel.ApplyState(coordinator.State);

            Assert.Same(product, viewModel.StoreProducts[0]);
            Assert.Equal(serverPrice, product.AmountKrw);
            Assert.Equal(koreanPrice, product.FormattedPrice);
            Assert.Equal($"{koreanPrice}에 구매", product.ActionText);
            Assert.Equal(product.ActionText, product.DetailStatusText);
            Assert.Contains(nameof(StoreProductPreviewViewModel.FormattedPrice), changes);
            Assert.Contains(nameof(StoreProductPreviewViewModel.DetailStatusText), changes);

            Sidey.Core.Localization.I18n.SetLanguage("en-US");
            viewModel.RefreshLocalizedText();

            Assert.Same(product, viewModel.StoreProducts[0]);
            Assert.Equal(serverPrice, product.AmountKrw);
            Assert.Equal(englishPrice, product.FormattedPrice);
            Assert.Equal($"Buy for {englishPrice}", product.ActionText);
            Assert.Equal(product.ActionText, product.DetailStatusText);

            Sidey.Core.Localization.I18n.SetLanguage("ko-KR");
            viewModel.RefreshLocalizedText();

            Assert.Equal(serverPrice, product.AmountKrw);
            Assert.Equal(koreanPrice, product.FormattedPrice);
            Assert.Equal($"{koreanPrice}에 구매", product.DetailStatusText);
        }
        finally
        {
            Sidey.Core.Localization.I18n.SetLanguage(previous);
        }
    }

    [Fact]
    public void OwnedEntitlementKeepsUnavailableProductMarkedOwnedAndDisablesItsAction()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        CommerceProduct ownedProduct = WindowsCommerceCatalog.Products[0];
        coordinator.State = state with
        {
            DevelopmentCommerceEnabled = true,
            CommerceProducts = WindowsCommerceCatalog.LockedStates(),
            ActiveEntitlementKeys = new HashSet<string>(StringComparer.Ordinal) { ownedProduct.EntitlementKey },
        };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        StoreProductPreviewViewModel product = viewModel.StoreProducts.Single(item => item.ProductId == ownedProduct.Id);

        Assert.True(product.IsOwned);
        Assert.False(product.IsPreviewOnlyVisible);
        Assert.False(product.IsActionEnabled);
        Assert.False(product.ActionCommand.CanExecute(null));
        Assert.Equal("보유 중", product.ActionText);
        Assert.Equal("보유 중", product.DetailStatusText);
    }

    [Theory]
    [InlineData(CommercePurchaseState.GoogleConnectionRequired, "다시 시도")]
    [InlineData(CommercePurchaseState.OpeningCheckout, "결제창 여는 중…")]
    [InlineData(CommercePurchaseState.Confirming, "결제 확인 중…")]
    [InlineData(CommercePurchaseState.Owned, "보유 중")]
    [InlineData(CommercePurchaseState.Error, "다시 시도")]
    public void EnabledStoreDetailsFollowPurchaseState(CommercePurchaseState purchaseState, string expected)
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        viewModel.ApplyState(state with
        {
            DevelopmentCommerceEnabled = true,
            CommerceProducts = [.. WindowsCommerceCatalog.Products.Select(item =>
                new CommerceProductState(item, GoogleConnected: true, purchaseState))],
        });

        Assert.All(viewModel.StoreProducts, product => Assert.Equal(expected, product.DetailStatusText));
    }
    [Fact]
    public void KeepsakeOwnershipIsIndependentOfCharacterAndDisappearsAfterRevocation()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());
        StoreProductPreviewViewModel otter = viewModel.StoreProducts.Single(product => product.CharacterId == "pixel_otter"
            && product.Kind == CommerceProductKind.Character);
        StoreProductPreviewViewModel clam = Assert.IsType<StoreProductPreviewViewModel>(otter.RelatedKeepsake);
        Assert.True(clam.IsKeepsake);
        Assert.Equal("throwable_clam", clam.CatalogItemId);

        viewModel.ApplyState(state with
        {
            ActiveEntitlementKeys = new HashSet<string>(StringComparer.Ordinal) { "character:pixel_otter" },
        });
        Assert.True(otter.IsOwned);
        Assert.False(clam.IsOwned);
        Assert.Single(viewModel.ThrowableSelections);

        viewModel.ApplyState(state with
        {
            ActiveEntitlementKeys = new HashSet<string>(StringComparer.Ordinal) { "throwable:throwable_clam" },
        });
        Assert.False(otter.IsOwned);
        Assert.True(clam.IsOwned);
        Assert.Equal(2, viewModel.ThrowableSelections.Count);
        Assert.Equal("pixel_hamster", viewModel.SelectedCharacterId);
        Assert.Same(clam, otter.RelatedKeepsake);
        Assert.False(clam.IsActionEnabled);

        viewModel.ApplyState(state);
        Assert.False(clam.IsOwned);
        Assert.Single(viewModel.ThrowableSelections);
    }

    [Fact]
    public async Task StoreRefreshesStaleAccountStateWithoutStartingGoogleLinking()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with
        {
            DevelopmentCommerceEnabled = true,
            CommerceProducts = [.. WindowsCommerceCatalog.Products.Select(product =>
                new CommerceProductState(
                    product,
                    GoogleConnected: false,
                    CommercePurchaseState.GoogleConnectionRequired))],
        };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        NoticeMessage? notice = null;
        viewModel.NoticeRaised += value => notice = value;
        StoreProductPreviewViewModel product = viewModel.StoreProducts[0];

        await product.ActionCommand.ExecuteAsync(null);

        Assert.False(product.IsPreviewOnlyVisible);
        Assert.True(product.IsActionEnabled);
        Assert.Equal("다시 시도", product.ActionText);
        Assert.Equal(0, coordinator.ActivateStoreProductCallCount);
        Assert.Equal(1, coordinator.RefreshStoreCallCount);
        Assert.Null(notice);
    }

    [Fact]
    public void OwnedDevelopmentProductCannotBePurchasedAgain()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        CommerceProduct ownedProduct = WindowsCommerceCatalog.Products[0];
        coordinator.State = state with
        {
            DevelopmentCommerceEnabled = true,
            CommerceProducts = [.. WindowsCommerceCatalog.Products.Select(product =>
                new CommerceProductState(
                    product,
                    GoogleConnected: true,
                    product == ownedProduct
                        ? CommercePurchaseState.Owned
                        : CommercePurchaseState.Available))],
        };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        StoreProductPreviewViewModel product = viewModel.StoreProducts[0];
        Assert.False(product.IsActionEnabled);
        Assert.Equal("보유 중", product.ActionText);
    }

    [Fact]
    public void IssuedCharactersAppearInTheProfilePickerAndTrackSelection()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        state = state with
        {
            Profile = state.Profile! with { CharacterId = "pixel_guinea_pig" },
            ActiveEntitlementKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "character:pixel_guinea_pig",
                "character:pixel_monkey",
                "character:pixel_chinchilla",
            },
        };
        coordinator.State = state;

        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal(
            [
                "pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit", "pixel_penguin",
                "pixel_guinea_pig", "pixel_monkey", "pixel_chinchilla",
            ],
            viewModel.CharacterSelections.Select(character => character.Id));
        Assert.True(viewModel.CharacterSelections.Single(
            character => character.Id == "pixel_guinea_pig").IsSelected);
        Assert.DoesNotContain(
            viewModel.CharacterSelections,
            character => character.Id == "pixel_starlight_upalupa");
    }

    [Fact]
    public void ApplyingEquivalentSnapshotPreservesRoomItemIdentity()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        RoomCardViewModel firstCard = Assert.Single(viewModel.Rooms);

        viewModel.ApplyState(state with { RealtimeConnection = ConnectedStatus() });

        Assert.Same(firstCard, Assert.Single(viewModel.Rooms));
        Assert.True(viewModel.IsConnected);
        Assert.Equal("연결됨", viewModel.ConnectionText);
    }

    [Fact]
    public void ApplyingEquivalentSnapshotPreservesCosmeticItemIdentity()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        CosmeticSelectionItemViewModel bubble = Assert.Single(viewModel.BubbleSelections);
        CosmeticSelectionItemViewModel throwable = Assert.Single(viewModel.ThrowableSelections);

        viewModel.ApplyState(state with { RealtimeConnection = ConnectedStatus() });

        Assert.Same(bubble, Assert.Single(viewModel.BubbleSelections));
        Assert.Same(throwable, Assert.Single(viewModel.ThrowableSelections));
    }

    [Fact]
    public void ApplyingEquivalentSnapshotDoesNotRebuildVisibleStoreCards()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        IReadOnlyList<StoreProductPreviewViewModel> initialProducts = viewModel.VisibleStoreProducts;
        int resultListChanges = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.VisibleStoreProducts))
            {
                resultListChanges++;
            }
        };

        viewModel.ApplyState(state with { RealtimeConnection = ConnectedStatus() });

        Assert.Equal(0, resultListChanges);
        Assert.Same(initialProducts, viewModel.VisibleStoreProducts);
    }

    [Fact]
    public void ProfileCharacterUpdateReusesCosmeticItemsAndRefreshesTheirPreview()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        CosmeticSelectionItemViewModel bubble = Assert.Single(viewModel.BubbleSelections);
        Profile changedProfile = state.Profile! with { CharacterId = "pixel_penguin" };

        viewModel.ApplyState(state with { Profile = changedProfile });

        Assert.Same(bubble, Assert.Single(viewModel.BubbleSelections));
        Assert.Equal("pixel_penguin", bubble.CharacterId);
    }

    [Fact]
    public void DisconnectedStateUsesAnExplicitOfflineLabel()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.False(viewModel.IsConnected);
        Assert.Equal("연결되지 않음", viewModel.ConnectionText);
    }

    [Fact]
    public void MonitorRefreshIncludesHotPluggedDisplays()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        coordinator.Monitors =
        [
            new MonitorOption("display-1", "Laptop", true),
        ];
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        coordinator.Monitors =
        [
            new MonitorOption("display-2", "4K", true),
            new MonitorOption("display-1", "Laptop", false),
        ];
        viewModel.RefreshMonitors();

        Assert.Equal(["display-2", "display-1"], viewModel.Monitors.Select(monitor => monitor.Identifier));
        Assert.Equal("display-2", viewModel.SelectedMonitorIdentifier);
    }

    [Fact]
    public async Task ProfileSaveIsDisabledUntilTheServerRequestCompletes()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SaveProfileHandler = (_, _, _) => completion.Task;
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            Nickname = "새 이름"
        };

        Task pending = viewModel.SaveProfileCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSavingProfile);
        Assert.False(viewModel.SaveProfileCommand.CanExecute(null));
        completion.SetResult();
        await pending;
        Assert.False(viewModel.IsSavingProfile);
        Assert.True(viewModel.SaveProfileCommand.CanExecute(null));
        Assert.Equal(1, coordinator.SaveProfileCallCount);
    }

    [Fact]
    public async Task CharacterSelectionAppliesImmediatelyWithoutUsingTheNicknameDraft()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SaveProfileHandler = (_, _, _) => completion.Task;
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            Nickname = "저장하지 않은 이름",
            SelectedCharacterId = "pixel_cat"
        };

        Assert.True(viewModel.IsSavingCharacter);
        Assert.Equal("aryu", coordinator.LastSavedNickname);
        Assert.Equal("pixel_cat", coordinator.LastSavedCharacterId);
        Assert.False(viewModel.SaveProfileCommand.CanExecute(null));

        completion.SetResult();
        for (int attempt = 0; attempt < 200 && viewModel.IsSavingCharacter; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.False(viewModel.IsSavingCharacter);
    }

    [Fact]
    public void NicknameActionAppearsOnlyForAValidChangedNickname()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.False(viewModel.HasNicknameChanges);
        Assert.False(viewModel.SaveProfileCommand.CanExecute(null));

        viewModel.Nickname = "새 이름";
        Assert.True(viewModel.HasNicknameChanges);
        Assert.True(viewModel.SaveProfileCommand.CanExecute(null));

        viewModel.Nickname = "a";
        Assert.True(viewModel.HasNicknameChanges);
        Assert.False(viewModel.SaveProfileCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("ko-KR", "1,100원", "2,200원", "3,300원")]
    [InlineData("en-US", "₩1,100", "₩2,200", "₩3,300")]
    [InlineData("ja-JP", "1,100ウォン", "2,200ウォン", "3,300ウォン")]
    [InlineData("zh-CN", "1,100 韩元", "2,200 韩元", "3,300 韩元")]
    [InlineData("zh-TW", "1,100 韓元", "2,200 韓元", "3,300 韓元")]
    [InlineData("ru-RU", "1\u00a0100 ₩", "2\u00a0200 ₩", "3\u00a0300 ₩")]
    [InlineData("uk-UA", "1\u00a0100 ₩", "2\u00a0200 ₩", "3\u00a0300 ₩")]
    public void NewCatalogPricesCreateTheStoreAndKeepLocalizedWonFormatting(
        string language, string characterPrice, string bubblePrice, string cannonPrice)
    {
        string previous = Sidey.Core.Localization.I18n.Language;
        try
        {
            Sidey.Core.Localization.I18n.SetLanguage(language);
            var model = new MainWindowViewModel(new FakeSideyCoordinator(), new FakeMainWindowDialogService(), new FakeUpdateService());
            Assert.Equal(characterPrice, model.StoreProducts.Single(product => product.ProductId == "character_tree").FormattedPrice);
            Assert.Equal(bubblePrice, model.StoreProducts.Single(product => product.ProductId == "bubble_bunny_pink").FormattedPrice);
            Assert.Equal(cannonPrice, model.StoreProducts.Single(product => product.ProductId == "throwable_toy_cannon").FormattedPrice);
        }
        finally { Sidey.Core.Localization.I18n.SetLanguage(previous); }
    }

    [Fact]
    public void StoreFiltersByKindSortsByPriceAndCanHideOwnedProducts()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with
        {
            ActiveEntitlementKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "throwable:throwable_bouncy_heart",
            },
        };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal(12, viewModel.VisibleStoreProducts.Count);
        Assert.All(viewModel.VisibleStoreProducts, product =>
            Assert.Equal(CommerceProductKind.Character, product.Kind));

        viewModel.SelectedStoreKindIndex = (int)CommerceProductKind.Throwable;
        viewModel.SelectedStoreSortIndex = 2;
        Assert.Equal(
            new[] { 3_300, 2_200, 2_200 }.Concat(Enumerable.Repeat(1_100, 15)),
            viewModel.VisibleStoreProducts.Select(product => product.AmountKrw));

        viewModel.HidesOwnedStoreProducts = true;
        Assert.DoesNotContain(
            viewModel.VisibleStoreProducts,
            product => product.ProductId == "throwable_bouncy_heart");

        viewModel.StoreSearchText = "오리";
        StoreProductPreviewViewModel throwable = Assert.Single(viewModel.VisibleStoreProducts);
        Assert.Equal("throwable_squeaky_duck", throwable.ProductId);

        viewModel.SelectedStoreKindIndex = (int)CommerceProductKind.Character;
        viewModel.StoreSearchText = "길 잃은 별";
        StoreProductPreviewViewModel character = Assert.Single(viewModel.VisibleStoreProducts);
        Assert.Equal("character_starlight_upalupa", character.ProductId);
    }

    [Fact]
    public void ResetStoreFiltersPreservesProductKindAndRestoresFilters()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state;
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            SelectedStoreKindIndex = (int)CommerceProductKind.Throwable,
            SelectedStoreSortIndex = 2,
            HidesOwnedStoreProducts = true,
            StoreSearchText = "오리",
        };

        viewModel.ResetStoreFiltersCommand.Execute(null);

        Assert.Equal((int)CommerceProductKind.Throwable, viewModel.SelectedStoreKindIndex);
        Assert.Equal(0, viewModel.SelectedStoreSortIndex);
        Assert.False(viewModel.HidesOwnedStoreProducts);
        Assert.Empty(viewModel.StoreSearchText);
        Assert.Equal(18, viewModel.VisibleStoreProducts.Count);
        Assert.All(viewModel.VisibleStoreProducts, product =>
            Assert.Equal(CommerceProductKind.Throwable, product.Kind));
    }

    [Fact]
    public void RemoteContentLoadingTracksTheCoordinatorLifecycle()
    {
        var coordinator = new FakeSideyCoordinator { IsRemoteContentLoading = true };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.True(viewModel.IsRemoteContentLoading);

        coordinator.IsRemoteContentLoading = false;
        viewModel.ApplyState(coordinator.State);

        Assert.False(viewModel.IsRemoteContentLoading);
    }

    [Fact]
    public void SelectionAndRoomSkeletonsFollowTheirSnapshotWhileStoreRemainsPending()
    {
        var coordinator = new FakeSideyCoordinator { IsRemoteContentLoading = true };
        var viewModel = new MainWindowViewModel(coordinator,
            new FakeMainWindowDialogService(), new FakeUpdateService());
        Assert.True(viewModel.IsCharacterSelectionsLoading);
        Assert.True(viewModel.IsBubbleSelectionsLoading);
        Assert.True(viewModel.IsThrowableSelectionsLoading);
        Assert.True(viewModel.IsRoomsLoading);
        Assert.True(viewModel.IsStoreLoading);

        CoordinatorState loaded = coordinator.State with
        {
            ContentLoading = new(RemoteDataLoadState.Ready, RemoteDataLoadState.Initial),
        };
        viewModel.ApplyState(loaded);

        Assert.True(viewModel.IsRemoteContentLoading);
        Assert.False(viewModel.IsCharacterSelectionsLoading);
        Assert.False(viewModel.IsBubbleSelectionsLoading);
        Assert.False(viewModel.IsThrowableSelectionsLoading);
        Assert.False(viewModel.IsRoomsLoading);
        Assert.True(viewModel.IsStoreLoading);

        viewModel.ApplyState(loaded with
        {
            ContentLoading = loaded.ContentLoading with { Store = RemoteDataLoadState.Ready },
        });
        Assert.False(viewModel.IsStoreLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAttemptsStopSkeletonsAndRefreshKeepsPreviouslyLoadedLists(bool hasValue)
    {
        var coordinator = new FakeSideyCoordinator();
        var viewModel = new MainWindowViewModel(coordinator,
            new FakeMainWindowDialogService(), new FakeUpdateService());
        RemoteDataLoadState completed = new RemoteDataLoadState(hasValue, IsLoading: true).EndAttempt();
        CoordinatorState state = coordinator.State with { ContentLoading = new(completed, completed) };
        viewModel.ApplyState(state);
        Assert.False(viewModel.IsCharacterSelectionsLoading);
        Assert.False(viewModel.IsRoomsLoading);
        Assert.False(viewModel.IsStoreLoading);

        viewModel.ApplyState(state with
        {
            ContentLoading = state.ContentLoading with { Store = completed.Begin() },
        });
        Assert.False(viewModel.IsCharacterSelectionsLoading);
        Assert.False(viewModel.IsRoomsLoading);
        Assert.Equal(!hasValue, viewModel.IsStoreLoading);
        viewModel.ApplyState(state with
        {
            ContentLoading = new(completed.Begin(), completed),
        });
        Assert.Equal(!hasValue, viewModel.IsCharacterSelectionsLoading);
        Assert.Equal(!hasValue, viewModel.IsRoomsLoading);
        Assert.False(viewModel.IsStoreLoading);
    }

    [Fact]
    public void RemoteLoadingKeepsLocalCatalogDefaultsAndCachedFreeSelectionAvailable()
    {
        CoordinatorState state = CoordinatorState.Initial with
        {
            Preferences = AppPreferences.Default with { CachedCharacterId = "pixel_cat" },
        };
        var coordinator = new FakeSideyCoordinator
        {
            IsRemoteContentLoading = true,
            State = state,
        };

        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal(
            PixelCharacterCatalog.Selectable.Select(character => character.Id),
            viewModel.CharacterSelections.Select(character => character.Id));
        Assert.True(viewModel.CharacterSelections.Single(character =>
            character.Id == "pixel_cat").IsSelected);
        Assert.True(Assert.Single(viewModel.BubbleSelections).IsSelected);
        Assert.True(Assert.Single(viewModel.ThrowableSelections).IsSelected);
    }

    [Fact]
    public void RemoteLoadingDoesNotGuessPaidSelectionWithoutEntitlements()
    {
        CoordinatorState state = CoordinatorState.Initial with
        {
            Preferences = AppPreferences.Default with
            {
                CachedCharacterId = "pixel_starlight_upalupa",
            },
        };
        var coordinator = new FakeSideyCoordinator
        {
            IsRemoteContentLoading = true,
            State = state,
        };

        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.DoesNotContain(
            viewModel.CharacterSelections,
            character => character.Id == "pixel_starlight_upalupa");
        Assert.DoesNotContain(viewModel.CharacterSelections, character => character.IsSelected);
    }

    [Fact]
    public void StoreSearchReplacesTheVisibleResultListOnce()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state;
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        int resultListChanges = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.VisibleStoreProducts))
            {
                resultListChanges++;
            }
        };

        viewModel.StoreSearchText = "별빛";

        StoreProductPreviewViewModel result = Assert.Single(viewModel.VisibleStoreProducts);
        Assert.Equal("character_starlight_upalupa", result.ProductId);
        Assert.Equal(1, resultListChanges);
    }

    [Fact]
    public async Task CosmeticSelectionAppliesImmediatelyAndBlocksSameKindDuplicates()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with
        {
            ActiveEntitlementKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "bubble:bubble_bunny_pink",
            },
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SetEquippedCosmeticHandler = (_, _, _) => completion.Task;
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal("기본 말풍선", viewModel.BubbleSelections[0].DisplayName);
        Task first = viewModel.BubbleSelections[1].SelectCommand.ExecuteAsync(null);
        Task duplicate = viewModel.BubbleSelections[0].SelectCommand.ExecuteAsync(null);

        Assert.Equal(1, coordinator.SetEquippedCosmeticCallCount);
        Assert.All(viewModel.BubbleSelections, selection => Assert.False(selection.IsEnabled));
        Assert.True(viewModel.BubbleSelections[0].IsSelected);
        Assert.True(viewModel.BubbleSelections[1].IsPending);
        Assert.False(viewModel.BubbleSelections[1].IsSelected);
        viewModel.ApplyState(coordinator.State);
        Assert.True(viewModel.BubbleSelections[1].IsPending);
        Assert.Equal(CommerceProductKind.Bubble, coordinator.LastEquippedCosmeticKind);
        Assert.Equal("bubble_bunny_pink", coordinator.LastEquippedCosmeticId);

        completion.SetResult();
        await Task.WhenAll(first, duplicate);
        Assert.All(viewModel.BubbleSelections, selection => Assert.True(selection.IsEnabled));
        Assert.All(viewModel.BubbleSelections, selection => Assert.False(selection.IsPending));
    }

    [Fact]
    public async Task SuccessfulCreateAndJoinClearTheirSubmittedFields()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            CreateRoomName = "새 그룹",
            InviteCode = "ABCD-EFGH",
        };

        await viewModel.CreateRoomCommand.ExecuteAsync(null);
        await viewModel.JoinRoomCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.CreateRoomName);
        Assert.Equal(string.Empty, viewModel.InviteCode);
        Assert.Equal(1, coordinator.CreateRoomCallCount);
        Assert.Equal(1, coordinator.JoinRoomCallCount);
    }

    [Fact]
    public async Task FailedCreateAndJoinPreserveTheirSubmittedFields()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        coordinator.CreateRoomHandler = (_, _) =>
            Task.FromException(new InvalidOperationException("create failed"));
        coordinator.JoinRoomHandler = (_, _) =>
            Task.FromException(new InvalidOperationException("join failed"));
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            CreateRoomName = "다시 쓸 그룹 이름",
            InviteCode = "KEEP-CODE",
        };

        await viewModel.CreateRoomCommand.ExecuteAsync(null);
        await viewModel.JoinRoomCommand.ExecuteAsync(null);

        Assert.Equal("다시 쓸 그룹 이름", viewModel.CreateRoomName);
        Assert.Equal("KEEP-CODE", viewModel.InviteCode);
    }

    [Fact]
    public async Task LateSuccessDoesNotClearAChangedCreateOrJoinDraft()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var createCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.CreateRoomHandler = (_, _) => createCompletion.Task;
        coordinator.JoinRoomHandler = (_, _) => joinCompletion.Task;
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            CreateRoomName = "제출한 그룹",
            InviteCode = "SUBMITTED-CODE",
        };

        Task createRequest = viewModel.CreateRoomCommand.ExecuteAsync(null);
        viewModel.CreateRoomName = "새 그룹 초안";
        createCompletion.SetResult();
        await createRequest;

        Task joinRequest = viewModel.JoinRoomCommand.ExecuteAsync(null);
        viewModel.InviteCode = "NEW-DRAFT";
        joinCompletion.SetResult();
        await joinRequest;

        Assert.Equal("새 그룹 초안", viewModel.CreateRoomName);
        Assert.Equal("NEW-DRAFT", viewModel.InviteCode);
    }

    [Fact]
    public async Task InviteCopyShowsPerRoomConfirmation()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        RoomCardViewModel room = Assert.Single(viewModel.Rooms);

        await room.InviteCommand.ExecuteAsync(null);

        Assert.True(room.IsInviteCopyConfirmed);
        Assert.Equal("복사됨", room.InviteActionText);
        room.Dispose();
    }

    [Fact]
    public void RoomProjectionMarksCurrentUserAndOwnerWithoutUiTypes()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        RoomMemberCardViewModel member = Assert.Single(Assert.Single(viewModel.Rooms).Members);

        Assert.True(member.IsCurrentUser);
        Assert.True(member.IsOwner);
        Assert.False(member.CanRemove);
        Assert.Equal("pixel_hamster", member.CharacterId);
    }

    [Fact]
    public void CharacterSelectionItemsTrackTheSelectedCharacter()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        CharacterSelectionItemViewModel initial = Assert.Single(viewModel.CharacterSelections, character => character.IsSelected);
        CharacterSelectionItemViewModel next = viewModel.CharacterSelections
            .First(character => !character.IsSelected);

        Assert.Equal("pixel_hamster", initial.Id);

        viewModel.SelectedCharacterId = next.Id;

        Assert.True(next.IsSelected);
        Assert.False(initial.IsSelected);
        Assert.Single(viewModel.CharacterSelections, character => character.IsSelected);
    }

    [Fact]
    public void RealtimeStateUpdatePreservesUnsavedProfileDraft()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService())
        {
            Nickname = "draft-name",
            SelectedCharacterId = "pixel_cat",
        };

        // Character selection has completed independently of the unsaved nickname.
        viewModel.ApplyState(state with { Profile = state.Profile! with { CharacterId = "pixel_cat" }, RealtimeConnection = ConnectedStatus() });

        Assert.Equal("draft-name", viewModel.Nickname);
        Assert.Equal("pixel_cat", viewModel.SelectedCharacterId);
        Assert.True(viewModel.CharacterSelections.Single(item => item.Id == "pixel_cat").IsSelected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown_character")]
    [InlineData("pixel_pig")]
    public void UnavailableCharacterSelectionPreservesConfirmedCharacterWithoutSaving(string? selection)
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with { Profile = state.Profile! with { CharacterId = "pixel_penguin" } };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        viewModel.SelectedCharacterId = selection!;

        Assert.Equal(0, coordinator.SaveProfileCallCount);
        Assert.Equal("pixel_penguin", viewModel.SelectedCharacterId);
        Assert.Equal("pixel_penguin", Assert.Single(viewModel.CharacterSelections, item => item.IsSelected).Id);
    }

    [Fact]
    public void ExplicitHamsterSelectionStillSavesTheChosenCharacter()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        coordinator.State = state with { Profile = state.Profile! with { CharacterId = "pixel_penguin" } };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), new FakeUpdateService());

        viewModel.SelectedCharacterId = "pixel_hamster";

        Assert.Equal(1, coordinator.SaveProfileCallCount);
        Assert.Equal("pixel_hamster", coordinator.LastSavedCharacterId);
        Assert.Equal("pixel_hamster", viewModel.SelectedCharacterId);
        Assert.Equal("pixel_hamster", Assert.Single(viewModel.CharacterSelections, item => item.IsSelected).Id);
    }

    [Fact]
    public void ServerProfileChangeUpdatesAnUneditedProfileDraft()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        Profile changedProfile = state.Profile! with
        {
            Nickname = "server-name",
            CharacterId = "pixel_penguin",
        };

        viewModel.ApplyState(state with { Profile = changedProfile });

        Assert.Equal("server-name", viewModel.Nickname);
        Assert.Equal("pixel_penguin", viewModel.SelectedCharacterId);
    }

    [Fact]
    public void CachedProfileSeedsTheFirstFrameBeforeTheServerProfileArrives()
    {
        AppPreferences preferences = AppPreferences.CreateDefault() with
        {
            CachedNickname = "캐시 이름",
            CachedCharacterId = "pixel_penguin",
        };
        var coordinator = new FakeSideyCoordinator
        {
            State = CoordinatorState.Initial with { Preferences = preferences },
        };

        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        Assert.Equal("캐시 이름", viewModel.Nickname);
        Assert.Equal("pixel_penguin", viewModel.SelectedCharacterId);
        Assert.True(viewModel.CharacterSelections.Single(item => item.Id == "pixel_penguin").IsSelected);
    }

    [Fact]
    public void RealtimeRoomChangesUpdateExistingCardInsteadOfReplacingIt()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        RoomCardViewModel card = Assert.Single(viewModel.Rooms);
        Room changedRoom = state.Rooms[0] with
        {
            Name = "부드러운 그룹",
            Members =
            [
                state.Rooms[0].Members[0] with { Nickname = "새 닉네임" },
                new RoomMember(Guid.NewGuid(), "친구", "pixel_cat", PresenceState.Online),
            ],
        };

        viewModel.ApplyState(state with { Rooms = [changedRoom] });

        Assert.Same(card, Assert.Single(viewModel.Rooms));
        Assert.Equal("부드러운 그룹", card.Name);
        Assert.Equal(2, card.Members.Count);
        Assert.Equal("새 닉네임", card.Members[0].Nickname);
    }

    [Fact]
    public void ChangingActiveRoomKeepsCurrentOrderAndClosesInactiveRooms()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateMultiRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        RoomCardViewModel firstRoom = viewModel.Rooms[0];
        RoomCardViewModel secondRoom = viewModel.Rooms[1];
        secondRoom.ToggleCommand.Execute(null);

        viewModel.ApplyState(state with { ActiveRoomId = secondRoom.Room.Id });

        Assert.Same(firstRoom, viewModel.Rooms[0]);
        Assert.Same(secondRoom, viewModel.Rooms[1]);
        Assert.False(firstRoom.IsExpanded);
        Assert.True(secondRoom.IsExpanded);
        Assert.Equal("\uE70D", firstRoom.ExpansionGlyph);
        Assert.Equal("\uE70E", secondRoom.ExpansionGlyph);
    }

    [Fact]
    public void PreparingGroupsMovesActiveRoomFirstAndRestoresDefaultExpansion()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateMultiRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        RoomCardViewModel firstRoom = viewModel.Rooms[0];
        RoomCardViewModel secondRoom = viewModel.Rooms[1];

        viewModel.ApplyState(state with { ActiveRoomId = secondRoom.Room.Id });
        firstRoom.ToggleCommand.Execute(null);
        viewModel.PrepareGroupsForPresentation();

        Assert.Same(secondRoom, viewModel.Rooms[0]);
        Assert.Same(firstRoom, viewModel.Rooms[1]);
        Assert.True(secondRoom.IsExpanded);
        Assert.False(firstRoom.IsExpanded);
    }

    [Fact]
    public async Task StartupUpdateCheckReturnsAvailableUpdateWithoutAnInAppNotice()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate(
                "2.0.1",
                UpdateVersion: "2.0.1000",
                UpdateTag: "windows-v2.0.1"),
            DownloadHandler = (update, _, _) =>
            {
                Assert.Equal("2.0.1", update.Version);
                Assert.Equal("2.0.1000", update.UpdateVersion);
                Assert.Equal("windows-v2.0.1", update.UpdateTag);
                return Task.CompletedTask;
            },
        };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            updates);
        int noticeCount = 0;
        viewModel.NoticeRaised += _ => noticeCount++;

        AvailableUpdate? update = await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Equal("2.0.1", update?.Version);
        Assert.Equal(0, noticeCount);
        Assert.Equal(0, updates.InstallerLaunchCount);
    }

    [Fact]
    public async Task StartupUpdateCheckIsSilentWhenCurrentVersionIsLatest()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        int noticeCount = 0;
        viewModel.NoticeRaised += _ => noticeCount++;

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Equal(0, noticeCount);
    }

    [Fact]
    public async Task UpdateInformationShowsVersionLastCheckAndOpensReleaseNotes()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService { CurrentVersion = "1.0.6" };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            updates);

        Assert.Equal("v1.0.6", viewModel.CurrentVersionText);
        Assert.NotEmpty(viewModel.LastUpdateCheckText);

        await viewModel.CheckForUpdatesOnStartupAsync();
        await viewModel.OpenReleaseNotesCommand.ExecuteAsync(null);

        Assert.NotNull(updates.LastCheckedAt);
        Assert.Equal(1, updates.ReleaseNotesLaunchCount);
    }

    [Fact]
    public async Task ManualUpdateCheckReportsTheLatestVersion()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        NoticeMessage? notice = null;
        viewModel.NoticeRaised += value => notice = value;

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("최신 버전을 사용하고 있습니다.", notice?.Message);
        Assert.Equal(NoticeKind.Success, notice?.Kind);
    }

    [Fact]
    public async Task ManualUpdateCheckKeepsAvailableVersionUntilDownloadRequested()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var dialogs = new FakeMainWindowDialogService();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("0.3.0-alpha.3"),
        };
        var viewModel = new MainWindowViewModel(coordinator, dialogs, updates);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Null(dialogs.ConfirmedUpdateVersion);
        Assert.True(viewModel.HasAvailableUpdate);
        Assert.Equal(0, updates.DownloadCount);
        Assert.Equal(0, updates.InstallerLaunchCount);

        await viewModel.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.DownloadCount);
        Assert.True(viewModel.IsUpdateReady);
        Assert.Equal(0, updates.InstallerLaunchCount);

        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.InstallerLaunchCount);
    }

    [Fact]
    public async Task UpdateDownloadKeepsAVisibleActivityStateUntilReady()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("0.3.0-alpha.3"),
            DownloadHandler = (_, progress, _) =>
            {
                progress?.Report(42);
                return completion.Task;
            },
        };
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            updates);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        Task pending = viewModel.DownloadUpdateCommand.ExecuteAsync(null);
        await Task.Yield();

        Assert.True(viewModel.IsUpdateTransferInProgress);
        Assert.True(viewModel.HasUpdateActivity);
        Assert.Equal("업데이트를 다운로드하는 중입니다. (42%)", viewModel.UpdateActivityText);
        Assert.False(viewModel.DownloadUpdateCommand.CanExecute(null));
        Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));

        completion.SetResult();
        await pending;

        Assert.False(viewModel.IsUpdateTransferInProgress);
        Assert.False(viewModel.HasUpdateActivity);
        Assert.Equal(string.Empty, viewModel.UpdateActivityText);
        Assert.True(viewModel.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartupAutoDownloadNotifiesAvailabilityBeforeDownloadCompletes()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("0.3.0-alpha.3"),
            DownloadHandler = (_, _, _) => completion.Task,
        };
        updates.SavePreferences(new UpdatePreferences(AutoDownload: true, AutoInstall: false));
        var viewModel = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), updates);
        int found = 0;
        int ready = 0;

        Task pending = viewModel.CheckForUpdatesOnStartupAsync(
            _ => found++, _ => ready++);
        await Task.Yield();

        Assert.Equal(1, found);
        Assert.Equal(0, ready);
        Assert.True(viewModel.HasAvailableUpdate);
        Assert.True(viewModel.IsUpdateTransferInProgress);
        Assert.Equal(0, updates.InstallerLaunchCount);

        completion.SetResult();
        await pending;

        Assert.Equal(1, ready);
        Assert.True(viewModel.IsUpdateReady);
        Assert.Equal(0, updates.InstallerLaunchCount);
    }

    [Fact]
    public async Task ManualCheckAutomaticallyDownloadsWithoutLaunchingInstallerWhenEnabled()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        int found = 0;
        int ready = 0;
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("2.1.0"),
            DownloadHandler = (_, _, _) =>
            {
                Assert.Equal(1, found);
                return Task.CompletedTask;
            },
        };
        updates.SavePreferences(new UpdatePreferences(AutoDownload: true, AutoInstall: false));
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);
        viewModel.ManualUpdateFound += _ => found++;
        viewModel.ManualUpdateReady += _ => ready++;

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.DownloadCount);
        Assert.Equal(1, found);
        Assert.Equal(1, ready);
        Assert.True(viewModel.IsUpdateReady);
        Assert.Equal(0, updates.InstallerLaunchCount);
    }

    [Fact]
    public async Task StartupRecognizesVerifiedCachedInstallerAfterRestart()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("2.1.0"),
            InstallerReady = true,
        };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);
        int found = 0;
        int ready = 0;

        await viewModel.CheckForUpdatesOnStartupAsync(_ => found++, _ => ready++);

        Assert.Equal(0, found);
        Assert.Equal(1, ready);
        Assert.True(viewModel.IsUpdateReady);
        Assert.True(viewModel.InstallUpdateCommand.CanExecute(null));
        Assert.Equal(0, updates.DownloadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoInstallLaunchesVerifiedUpdateWithoutInstallClick(bool cachedInstaller)
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("2.1.0"),
            InstallerReady = cachedInstaller,
        };
        updates.SavePreferences(new UpdatePreferences());
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);
        int readyNotifications = 0;

        await viewModel.CheckForUpdatesOnStartupAsync(
            onUpdateReady: _ => readyNotifications++);

        Assert.True(viewModel.IsUpdateReady);
        Assert.Equal(1, updates.InstallerLaunchCount);
        Assert.True(updates.LastInstallerLaunchWasSilent);
        Assert.Equal(0, readyNotifications);
        Assert.Equal(cachedInstaller ? 1 : 2, updates.DownloadCount);
    }

    [Fact]
    public async Task AutoInstallDownloadFailureDoesNotLaunchInstaller()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("2.1.0"),
            DownloadHandler = (_, _, _) => throw new IOException("download failed"),
        };
        updates.SavePreferences(new UpdatePreferences(AutoInstall: true));
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.False(viewModel.IsUpdateReady);
        Assert.Equal(0, updates.InstallerLaunchCount);
        Assert.True(viewModel.DownloadUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledAutoInstallKeepsManualInstallReadyAndDisablesAutomaticRetry()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("2.1.0"),
            InstallerReady = true,
            DownloadHandler = (_, _, _) => throw new Win32Exception(1223),
        };
        updates.SavePreferences(new UpdatePreferences(AutoInstall: true));
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);
        int readyNotifications = 0;

        await viewModel.CheckForUpdatesOnStartupAsync(
            onUpdateReady: _ => readyNotifications++);

        Assert.False(updates.Preferences.AutoInstall);
        Assert.True(viewModel.IsUpdateReady);
        Assert.Equal(1, readyNotifications);
        Assert.True(viewModel.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task LanguageRefreshUpdatesAvailableVersionWithoutLosingDownloadState()
    {
        string previousLanguage = I18n.Language;
        try
        {
            I18n.SetLanguage("ko-KR");
            (FakeSideyCoordinator coordinator, _) = CreateRoomState();
            var updates = new FakeUpdateService
            {
                AvailableUpdate = new AvailableUpdate("2.1.0"),
                InstallerReady = true,
            };
            var viewModel = new MainWindowViewModel(
                coordinator, new FakeMainWindowDialogService(), updates);
            await viewModel.CheckForUpdatesOnStartupAsync();
            Assert.Equal(I18n.Format("settings.update.available_version", "2.1.0"),
                viewModel.AvailableVersionText);

            I18n.SetLanguage("en-US");
            viewModel.RefreshLocalizedText();

            Assert.Equal(I18n.Format("settings.update.available_version", "2.1.0"),
                viewModel.AvailableVersionText);
            Assert.True(viewModel.IsUpdateReady);
            Assert.True(viewModel.InstallUpdateCommand.CanExecute(null));
        }
        finally { I18n.SetLanguage(previousLanguage); }
    }

    [Fact]
    public void UpdateOptionsAreSavedIndependently()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService();
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);

        viewModel.AutoDownloadUpdates = true;
        viewModel.AutoInstallUpdates = true;
        viewModel.NotifyWhenUpdateAvailable = false;
        viewModel.ShowReleaseNotesAfterUpdate = false;

        Assert.Equal(new UpdatePreferences(true, false, false, true), updates.Preferences);
    }

    [Fact]
    public void ExistingUpdatePreferencesUseEnabledAutoInstallDefault()
    {
        const string ExistingPreferences =
            "{\"AutoDownload\":true,\"NotifyWhenAvailable\":false,\"ShowReleaseNotesAfterUpdate\":true}";

        UpdatePreferences? preferences = JsonSerializer.Deserialize<UpdatePreferences>(
            ExistingPreferences);

        Assert.NotNull(preferences);
        Assert.True(preferences.AutoDownload);
        Assert.True(preferences.AutoInstall);
    }

    [Fact]
    public void ExplicitAutoInstallOptOutRemainsDisabled()
    {
        const string Preferences =
            "{\"AutoDownload\":true,\"AutoInstall\":false,\"NotifyWhenAvailable\":false,\"ShowReleaseNotesAfterUpdate\":true}";

        UpdatePreferences? preferences = JsonSerializer.Deserialize<UpdatePreferences>(
            Preferences);

        Assert.NotNull(preferences);
        Assert.False(preferences.AutoInstall);
    }

    [Fact]
    public async Task UpdateDownloadFailuresShowFriendlyNoticesAndAllowRetry()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        const string InternalDetails = @"An error occurred trying to start process C:\Users\private\Temp\Setup.exe";
        Exception failure = new IOException(InternalDetails);
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("1.0.10"),
            DownloadHandler = (_, _, _) => Task.FromException(failure),
        };
        var viewModel = new MainWindowViewModel(coordinator, new FakeMainWindowDialogService(), updates);
        NoticeMessage? notice = null;
        viewModel.NoticeRaised += value => notice = value;

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await viewModel.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Equal(Sidey.Core.Localization.I18n.Get("update.failed"), notice?.Message);
        Assert.Equal(NoticeKind.Error, notice?.Kind);
        Assert.False(viewModel.HasUpdateActivity);
        Assert.False(viewModel.IsUpdateTransferInProgress);
        Assert.True(viewModel.DownloadUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledInstallerLaunchKeepsTheVerifiedUpdateReady()
    {
        (FakeSideyCoordinator coordinator, _) = CreateRoomState();
        var updates = new FakeUpdateService
        {
            AvailableUpdate = new AvailableUpdate("2.1.0"),
            InstallerReady = true,
            DownloadHandler = (_, _, _) => throw new Win32Exception(1223),
        };
        var viewModel = new MainWindowViewModel(
            coordinator, new FakeMainWindowDialogService(), updates);
        NoticeMessage? notice = null;
        viewModel.NoticeRaised += value => notice = value;

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(I18n.Get("update.install.cancelled"), notice?.Message);
        Assert.Equal(NoticeKind.Informational, notice?.Kind);
        Assert.True(viewModel.IsUpdateReady);
        Assert.True(viewModel.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public void SwitchingStateIdentifiesTheTargetAndDisablesMutations()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateMultiRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());
        Guid targetRoomId = state.Rooms[1].Id;

        viewModel.ApplyState(state with
        {
            GroupOperation = GroupOperation.Switching,
            SwitchingRoomId = targetRoomId,
        });

        RoomCardViewModel target = viewModel.Rooms.Single(room => room.Room.Id == targetRoomId);
        RoomCardViewModel active = viewModel.Rooms.Single(room => room.Room.Id == state.ActiveRoomId);
        Assert.True(target.IsSwitching);
        Assert.Equal("그룹 전환 중…", target.JoinActionText);
        Assert.False(target.IsJoinEnabled);
        Assert.False(active.IsSwitching);
        Assert.False(viewModel.AreGroupMutationsEnabled);
        Assert.All(viewModel.Rooms, room => Assert.False(room.AreRoomActionsEnabled));
        Assert.All(viewModel.Rooms, room => Assert.False(room.AreOwnerActionsEnabled));
        Assert.All(viewModel.Rooms.SelectMany(room => room.Members), member =>
            Assert.False(member.CanRemove));
    }

    [Theory]
    [InlineData(GroupOperation.Creating, "만드는 중…", "초대 코드로 참여")]
    [InlineData(GroupOperation.Joining, "그룹 만들기", "참여 중…")]
    [InlineData(GroupOperation.Mutating, "그룹 만들기", "초대 코드로 참여")]
    public void CreateAndJoinOperationsExposeProgressCopy(
        GroupOperation operation,
        string createText,
        string joinText)
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var viewModel = new MainWindowViewModel(
            coordinator,
            new FakeMainWindowDialogService(),
            new FakeUpdateService());

        viewModel.ApplyState(state with { GroupOperation = operation });

        Assert.Equal(createText, viewModel.CreateRoomActionText);
        Assert.Equal(joinText, viewModel.JoinRoomActionText);
        Assert.False(viewModel.AreGroupMutationsEnabled);
    }

    [Fact]
    public async Task MemberRemovalRequiresNamedConfirmationAndUsesGlobalInAppNotice()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var friendId = Guid.NewGuid();
        Room room = state.Rooms[0] with
        {
            Members =
            [
                .. state.Rooms[0].Members,
                new RoomMember(friendId, "친구별", "pixel_cat", PresenceState.Online),
            ],
        };
        state = state with { Rooms = [room] };
        coordinator.State = state;
        var dialogs = new FakeMainWindowDialogService { ConfirmMemberRemoval = false };
        var viewModel = new MainWindowViewModel(coordinator, dialogs, new FakeUpdateService());
        RoomMemberCardViewModel member = Assert.Single(
            Assert.Single(viewModel.Rooms).Members,
            candidate => candidate.UserId == friendId);

        await member.RemoveCommand.ExecuteAsync(null);
        Assert.Equal("친구별", dialogs.ConfirmedRemovalNickname);
        Assert.Equal(0, coordinator.RemoveRoomMemberCallCount);

        dialogs.ConfirmMemberRemoval = true;
        NoticeMessage? notice = null;
        viewModel.NoticeRaised += value => notice = value;
        await member.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(1, coordinator.RemoveRoomMemberCallCount);
        Assert.Equal(NoticeKind.Success, notice?.Kind);
        Assert.Equal("멤버를 내보냈습니다.", notice?.Message);
    }

    [Fact]
    public async Task LeavingAnyRoomRequiresConfirmationAndUsesTheSelectedRoom()
    {
        (FakeSideyCoordinator coordinator, _) = CreateMultiRoomState();
        var dialogs = new FakeMainWindowDialogService { ConfirmRoomLeave = false };
        var viewModel = new MainWindowViewModel(coordinator, dialogs, new FakeUpdateService());
        RoomCardViewModel room = viewModel.Rooms[1];

        await room.LeaveCommand.ExecuteAsync(null);

        Assert.Equal("Second", dialogs.ConfirmedLeaveRoomName);
        Assert.True(dialogs.ConfirmedLeaveRoomIsOwner);
        Assert.Equal(0, coordinator.LeaveRoomCallCount);

        dialogs.ConfirmRoomLeave = true;
        NoticeMessage? notice = null;
        viewModel.NoticeRaised += value => notice = value;
        await room.LeaveCommand.ExecuteAsync(null);

        Assert.Equal(1, coordinator.LeaveRoomCallCount);
        Assert.Equal(room.Room.Id, coordinator.LastLeftRoomId);
        Assert.Equal(NoticeKind.Success, notice?.Kind);
        Assert.Equal("그룹에서 나왔습니다.", notice?.Message);
    }

    private static (FakeSideyCoordinator Coordinator, CoordinatorState State) CreateRoomState()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var profile = new Profile(userId, "aryu", "pixel_hamster");
        var room = new Room(
            roomId,
            "Test",
            userId,
            [new RoomMember(userId, profile.Nickname, profile.CharacterId, PresenceState.Online)],
            "••••-TEST",
            true,
            1);
        CoordinatorState state = CoordinatorState.Initial with
        {
            Profile = profile,
            Rooms = [room],
            ActiveRoomId = roomId,
            RealtimeConnection = RealtimeConnectionStatus.Disconnected,
        };
        return (new FakeSideyCoordinator { State = state }, state);
    }

    private static RealtimeConnectionStatus ConnectedStatus() => new(true, true, true);

    private static (FakeSideyCoordinator Coordinator, CoordinatorState State) CreateMultiRoomState()
    {
        (FakeSideyCoordinator coordinator, CoordinatorState state) = CreateRoomState();
        var secondRoomId = Guid.NewGuid();
        Room secondRoom = state.Rooms[0] with
        {
            Id = secondRoomId,
            Name = "Second",
            InviteCodeHint = "••••-NEXT",
        };
        CoordinatorState multiRoomState = state with { Rooms = [state.Rooms[0], secondRoom] };
        coordinator.State = multiRoomState;
        return (coordinator, multiRoomState);
    }
}
