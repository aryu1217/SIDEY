using Sidey.Core.Domain;
using Sidey.Platform.Windows.Shell;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsHotkeyAvailabilityTests
{
    [Fact]
    public void WindowsScreenSnippingWarnsWithoutTakingTheShortcut()
    {
        var native = new FakeNative();
        var shortcut = new GlobalHotkeyBinding(
            GlobalHotkeyModifiers.Windows | GlobalHotkeyModifiers.Shift, 'S');

        Assert.Equal(
            WindowsHotkeyAvailabilityResult.SystemShortcut,
            WindowsHotkeyAvailability.Check(shortcut, native));
        Assert.Empty(native.Registrations);
    }

    [Fact]
    public void AvailableShortcutIsReleasedImmediatelyAfterProbe()
    {
        var native = new FakeNative();
        var shortcut = new GlobalHotkeyBinding(
            GlobalHotkeyModifiers.Windows | GlobalHotkeyModifiers.Control, 'O');

        Assert.Equal(
            WindowsHotkeyAvailabilityResult.Available,
            WindowsHotkeyAvailability.Check(shortcut, native));
        Assert.Single(native.Registrations);
        Assert.Equal((nint.Zero, native.Registrations[0].Id), Assert.Single(native.Unregistrations));
        Assert.Equal((uint)shortcut.Modifiers | TrayHotkeys.NoRepeat,
            native.Registrations[0].Modifiers);
        Assert.Equal(shortcut.VirtualKey, native.Registrations[0].VirtualKey);
    }

    [Theory]
    [InlineData(1409, WindowsHotkeyAvailabilityResult.AlreadyRegistered)]
    [InlineData(5, WindowsHotkeyAvailabilityResult.Unverified)]
    public void FailedProbeDoesNotUnregisterAnotherOwnersShortcut(
        int error,
        WindowsHotkeyAvailabilityResult expected)
    {
        var native = new FakeNative { Error = error };
        var shortcut = new GlobalHotkeyBinding(GlobalHotkeyModifiers.Control, 'O');

        Assert.Equal(expected, WindowsHotkeyAvailability.Check(shortcut, native));
        Assert.Empty(native.Unregistrations);
    }

    [Fact]
    public void EditorProbeDistinguishesSavedSideyBindingsFromAnotherAppsBinding()
    {
        var native = new TrackingNative();
        GlobalHotkeyBinding[] defaultBindings =
            [.. Enum.GetValues<GlobalHotkeyAction>().Select(GlobalHotkeySettings.Default.BindingFor)];
        using (var defaultTray = new TrayHotkeys(42, GlobalHotkeySettings.Default, native))
        {
            foreach (GlobalHotkeyBinding binding in defaultBindings)
                Assert.Equal(WindowsHotkeyAvailabilityResult.AlreadyRegistered,
                    WindowsHotkeyAvailability.Check(binding, native));
        }

        GlobalHotkeySettings settings = GlobalHotkeySettings.Default.Assign(
            GlobalHotkeyAction.ToggleOverlay,
            new GlobalHotkeyBinding(GlobalHotkeyModifiers.Control, 'X'));
        GlobalHotkeyBinding[] sideyBindings =
            [.. Enum.GetValues<GlobalHotkeyAction>().Select(settings.BindingFor)];
        var tray = new TrayHotkeys(42, settings, native);

        foreach (GlobalHotkeyBinding binding in sideyBindings)
            Assert.Equal(WindowsHotkeyAvailabilityResult.AlreadyRegistered,
                WindowsHotkeyAvailability.Check(binding, native));

        tray.Dispose(); // The shortcut editor suspends SIDEY registrations before its first probe.
        foreach (GlobalHotkeyBinding binding in sideyBindings)
            Assert.Equal(WindowsHotkeyAvailabilityResult.Available,
                WindowsHotkeyAvailability.Check(binding, native));

        var otherAppBinding = new GlobalHotkeyBinding(GlobalHotkeyModifiers.Control, 'O');
        Assert.Equal(0, native.Register(99, 999,
            (uint)otherAppBinding.Modifiers | TrayHotkeys.NoRepeat, otherAppBinding.VirtualKey));
        Assert.Equal(WindowsHotkeyAvailabilityResult.AlreadyRegistered,
            WindowsHotkeyAvailability.Check(otherAppBinding, native));

        using var restoredTray = new TrayHotkeys(42, settings, native);
        foreach (GlobalHotkeyBinding binding in sideyBindings)
            Assert.Equal(WindowsHotkeyAvailabilityResult.AlreadyRegistered,
                WindowsHotkeyAvailability.Check(binding, native));
    }

    private sealed class FakeNative : ITrayHotkeyNative
    {
        public int Error { get; init; }
        public List<(nint Window, int Id, uint Modifiers, uint VirtualKey)> Registrations { get; } = [];
        public List<(nint Window, int Id)> Unregistrations { get; } = [];

        public int Register(nint window, int id, uint modifiers, uint virtualKey)
        {
            Registrations.Add((window, id, modifiers, virtualKey));
            return Error;
        }

        public void Unregister(nint window, int id) => Unregistrations.Add((window, id));
    }

    private sealed class TrackingNative : ITrayHotkeyNative
    {
        private readonly Dictionary<(nint Window, int Id), (uint Modifiers, uint Key)> _registrations = [];

        public int Register(nint window, int id, uint modifiers, uint virtualKey)
        {
            (uint Modifiers, uint Key) binding = (modifiers, virtualKey);
            if (_registrations.Values.Contains(binding))
                return 1409;
            _registrations[(window, id)] = binding;
            return 0;
        }

        public void Unregister(nint window, int id) => _registrations.Remove((window, id));
    }
}
