using Sidey.Core.Domain;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class OverlayVisibilityStateTests
{
    private static readonly Guid s_room = Guid.Parse("28ad4914-d6a7-45df-929e-e66a86b58db8");
    private static readonly Guid s_user = Guid.Parse("94b00d5f-eb20-42a4-a784-c8e0735d7549");
    private static readonly Guid s_first = Guid.Parse("ca64c42a-74f5-4f0e-925c-8445d23559ca");
    private static readonly Guid s_second = Guid.Parse("7888145f-51c3-4db1-abaa-7ca8b9f720d4");

    [Fact]
    public void EveryLiveConnectionMustHaveServerConfirmedSuppression()
    {
        var state = new OverlayVisibilityState();
        state.Merge([
            new OverlayVisibilityRecord(s_user, s_first, true, 3),
            new OverlayVisibilityRecord(s_user, s_second, false, 4),
        ]);
        state.Update(s_room, new Dictionary<Guid, IReadOnlyList<OverlayPresenceConnection>>
        {
            [s_user] = [new("a", null, s_first, PresenceState.Online),
                new("b", null, s_second, PresenceState.Online)],
        }, new Dictionary<Guid, IReadOnlyList<string>>(), full: true);
        Assert.Equal(PresenceState.Online, state.BasePresence(s_room, s_user));
        Assert.False(state.IsSuppressed(s_room, s_user));

        state.Update(s_room, new Dictionary<Guid, IReadOnlyList<OverlayPresenceConnection>>(),
            new Dictionary<Guid, IReadOnlyList<string>> { [s_user] = ["b"] }, full: false);
        Assert.Equal(PresenceState.Online, state.BasePresence(s_room, s_user));
        Assert.True(state.IsSuppressed(s_room, s_user));

        state.Update(s_room, new Dictionary<Guid, IReadOnlyList<OverlayPresenceConnection>>
        {
            [s_user] = [new("legacy", null, null, PresenceState.Away)],
        }, new Dictionary<Guid, IReadOnlyList<string>>(), full: false);
        Assert.False(state.IsSuppressed(s_room, s_user));
        state.Update(s_room, new Dictionary<Guid, IReadOnlyList<OverlayPresenceConnection>>(),
            new Dictionary<Guid, IReadOnlyList<string>> { [s_user] = ["a", "legacy"] }, full: false);
        Assert.Equal(PresenceState.Offline, state.BasePresence(s_room, s_user));
        Assert.False(state.IsSuppressed(s_room, s_user));
    }

    [Fact]
    public void LateSnapshotCannotOverwriteOrEraseNewerBroadcast()
    {
        var state = new OverlayVisibilityState();
        state.Merge([new OverlayVisibilityRecord(s_user, s_first, false, 5)]);
        long startedAtRevision = state.Revision;
        state.Merge([new OverlayVisibilityRecord(s_user, s_first, true, 7)]);
        state.Merge([new OverlayVisibilityRecord(s_user, s_second, true, 8)]);
        state.Reconcile([new OverlayVisibilityRecord(s_user, s_first, false, 5)], startedAtRevision);
        state.Update(s_room, new Dictionary<Guid, IReadOnlyList<OverlayPresenceConnection>>
        {
            [s_user] = [new("a", null, s_first, PresenceState.Online),
                new("b", null, s_second, PresenceState.Online)],
        }, new Dictionary<Guid, IReadOnlyList<string>>(), full: true);
        Assert.True(state.IsSuppressed(s_room, s_user));
    }

    [Fact]
    public void EarlyPresenceIsRetainedButOnlyMembersProduceDisplayChanges()
    {
        var state = new OverlayVisibilityState();
        state.Merge([new OverlayVisibilityRecord(s_user, s_first, true, 1)]);
        state.Update(s_room, new Dictionary<Guid, IReadOnlyList<OverlayPresenceConnection>>
        {
            [s_user] = [new("a", null, s_first, PresenceState.Online)],
        }, new Dictionary<Guid, IReadOnlyList<string>>(), full: true);
        Assert.Empty(state.Changes());
        state.SetMembers([new Room(s_room, "room", s_user,
            [new RoomMember(s_user, "member", "hamster", PresenceState.Online)], "", true, 1)]);
        Assert.True(Assert.Single(state.Changes()).Suppressed);
    }
}
