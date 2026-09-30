using Sidey.Core.Abstractions;
using Sidey.Core.Localization;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.Presentation.Tests;

public sealed class ComposerSessionTests
{
    [Fact]
    public async Task DisconnectedComposerPreservesDraftUntilConnectionRecovers()
    {
        int sends = 0;
        Guid room;
        room = Guid.NewGuid();
        using ComposerViewModel composer = new((_, _) =>
        {
            sends++;
            return Task.CompletedTask;
        }, autoCloseAfterSend: false);
        composer.ApplyRoom(room, canCompose: true, isConnectionReady: false);
        composer.Draft = "Saved draft";

        Assert.True(composer.CanCompose);
        Assert.True(composer.IsConnectionRecovering);
        Assert.False(composer.SendCommand.CanExecute(null));
        // The command also checks readiness when called without a UI CanExecute check.
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal("Saved draft", composer.Draft);
        Assert.Equal(0, sends);

        composer.ApplyRoom(room, canCompose: true, isConnectionReady: true);
        Assert.False(composer.IsConnectionRecovering);
        Assert.True(composer.SendCommand.CanExecute(null));
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(1, sends);
        Assert.Equal(string.Empty, composer.Draft);
    }

    [Fact]
    public async Task TerminalNotReadyRaceRestoresDraftAndShowsSafeConnectionMessage()
    {
        using ComposerViewModel composer = new((_, _) =>
            Task.FromException(new ChatNotReadyException()), autoCloseAfterSend: false);
        Guid room;
        room = Guid.NewGuid();
        composer.ApplyRoom(room, canCompose: true, isConnectionReady: true);
        composer.Draft = "Keep this message";

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal("Keep this message", composer.Draft);
        Assert.Equal(I18n.Get("connection.notice.reconnecting"), composer.ErrorMessage);

        composer.ApplyRoom(room, canCompose: true, isConnectionReady: false);
        Assert.True(composer.HasError);
        composer.ApplyRoom(room, canCompose: true, isConnectionReady: true);
        Assert.False(composer.HasError);
        Assert.Equal("Keep this message", composer.Draft);
    }

    [Fact]
    public async Task HistorySendsToCapturedRoomAndPreservesOtherRoomDraftOnFailure()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? sentRoom = null;
        string? sentBody = null;
        using var composer = new ComposerViewModel((room, body) =>
        {
            sentRoom = room;
            sentBody = body;
            return completion.Task;
        }, autoCloseAfterSend: false);
        composer.ApplyRoom(first, true);
        composer.Draft = " first message ";
        Task sending = composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(first, sentRoom);
        Assert.Equal("first message", sentBody);
        Assert.Equal(string.Empty, composer.Draft);
        composer.ApplyRoom(second, true);
        composer.Draft = "second room draft";
        completion.SetException(new IOException("offline"));
        await sending;
        Assert.Equal("second room draft", composer.Draft);
        Assert.False(composer.HasError);
        composer.ApplyRoom(first, true);
        Assert.Equal("first message", composer.Draft);
        Assert.True(composer.HasError);
    }

    [Fact]
    public async Task FailureDoesNotOverwriteNewDraftOrAnotherInput()
    {
        var room = Guid.NewGuid();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var composer = new ComposerViewModel((_, _) => completion.Task);
        using var other = new ComposerViewModel();
        composer.ApplyRoom(room, true);
        other.ApplyRoom(room, true);
        other.Draft = "other input";
        composer.Draft = "first";
        Task sending = composer.SendCommand.ExecuteAsync(null);
        Assert.False(composer.SendCommand.CanExecute(null));
        composer.Draft = "new draft";
        completion.SetException(new IOException("offline"));
        await sending;
        Assert.Equal("new draft", composer.Draft);
        Assert.Equal("other input", other.Draft);
        Assert.True(composer.HasError);
    }

    [Fact]
    public async Task RestoreAfterFailureDoesNotRestartTyping()
    {
        using var composer = new ComposerViewModel((_, _) => Task.FromException(new IOException("offline")));
        composer.ApplyRoom(Guid.NewGuid(), true);
        var typing = new List<bool>();
        composer.TypingChanged += typing.Add;
        composer.Draft = "message";
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal([true, false], typing);
        Assert.Equal("message", composer.Draft);
    }

    [Fact]
    public async Task SuccessfulSendLeavesHistoryOpenAndReadyForNextMessage()
    {
        using var composer = new ComposerViewModel((_, _) => Task.CompletedTask, autoCloseAfterSend: false);
        composer.ApplyRoom(Guid.NewGuid(), true);
        bool closed = false;
        composer.CloseRequested += () => closed = true;
        composer.Draft = "message";
        await composer.SendCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.False(composer.HasError);
        Assert.Equal(string.Empty, composer.Draft);
        composer.Draft = "next";
        Assert.True(composer.SendCommand.CanExecute(null));
    }

    [Fact]
    public void RoomSwitchRestoresDraftWithoutTypingAndDisablesIneligibleInput()
    {
        using var composer = new ComposerViewModel((_, _) => Task.CompletedTask);
        var first = Guid.NewGuid();
        composer.ApplyRoom(first, true);
        composer.Draft = "saved";
        composer.ApplyRoom(Guid.NewGuid(), true);
        var typing = new List<bool>();
        composer.TypingChanged += typing.Add;
        composer.ApplyRoom(first, true);
        Assert.Equal("saved", composer.Draft);
        Assert.Equal([false], typing);
        composer.ApplyRoom(first, false);
        Assert.False(composer.CanCompose);
        Assert.False(composer.SendCommand.CanExecute(null));
        composer.ApplyRoom(null, false);
        Assert.Equal(string.Empty, composer.Draft);
    }

    [Fact]
    public void StaleStopFromPreviousInputDoesNotStopCurrentTyping()
    {
        var owner = new ComposerTypingOwner();
        object floating = new();
        object history = new();
        Assert.True(owner.Update(floating, true, true));
        Assert.True(owner.Update(history, true, true));
        Assert.Null(owner.Update(floating, false, true));
        Assert.False(owner.Update(history, false, true));
        Assert.Null(owner.Update(history, true, false));
    }
}
