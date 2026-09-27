using System.Globalization;
using System.Runtime.InteropServices;
using Sidey.Core.Abstractions;
using Sidey.Core.Localization;
using Sidey.Core.Overlay;

namespace Sidey.Platform.Windows.Monitoring;

public sealed record WindowsMonitorInfo(
    string Identifier,
    string Name,
    NativePixelRect MonitorPixels,
    NativePixelRect WorkAreaPixels,
    uint Dpi,
    bool IsPrimary);

public sealed class WindowsMonitorService : IMonitorService
{
    public IReadOnlyList<MonitorGeometry> GetMonitors() => [.. GetAll()
        .Select(monitor =>
        {
            double scale = monitor.Dpi / 96d;
            return new MonitorGeometry(
                monitor.Identifier,
                monitor.Name,
                new RectD(
                    monitor.WorkAreaPixels.X / scale,
                    monitor.WorkAreaPixels.Y / scale,
                    monitor.WorkAreaPixels.Width / scale,
                    monitor.WorkAreaPixels.Height / scale),
                monitor.Dpi,
                monitor.IsPrimary);
        })];

    public static IReadOnlyList<WindowsMonitorInfo> GetAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Monitor discovery requires Windows.");
        }

        var monitors = new List<WindowsMonitorInfo>();
        IReadOnlyDictionary<string, string> friendlyNames = GetFriendlyNames();
        NativeMethods.EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx
                {
                    Size = Marshal.SizeOf<MonitorInfoEx>(),
                    DeviceName = string.Empty,
                };
                if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    return true;
                }

                uint dpi = NativeMethods.GetDpiForMonitor(
                    monitor,
                    MonitorDpiType.Effective,
                    out uint dpiX,
                    out _) == 0
                    ? dpiX
                    : NativeMethods.GetDpiForSystem();
                NativeRect work = info.Work;
                string identifier = string.IsNullOrWhiteSpace(info.DeviceName)
                    ? $"monitor-{monitors.Count + 1}"
                    : info.DeviceName;
                friendlyNames.TryGetValue(identifier, out string? friendlyName);
                monitors.Add(new WindowsMonitorInfo(
                    identifier,
                    FormatDisplayName(identifier, friendlyName, monitors.Count + 1),
                    new NativePixelRect(
                        info.Monitor.Left,
                        info.Monitor.Top,
                        info.Monitor.Right - info.Monitor.Left,
                        info.Monitor.Bottom - info.Monitor.Top),
                    new NativePixelRect(
                        work.Left,
                        work.Top,
                        work.Right - work.Left,
                        work.Bottom - work.Top),
                    dpi,
                    (info.Flags & 1) != 0));
                return true;
            },
            nint.Zero);
        return monitors;
    }

    internal static string FormatDisplayName(
        string identifier,
        string? friendlyName,
        int fallbackNumber,
        Func<string, object?[], string>? formatter = null)
    {
        formatter ??= I18n.Format;
        const string DisplayPrefix = @"\\.\DISPLAY";
        string suffix = identifier.StartsWith(DisplayPrefix, StringComparison.OrdinalIgnoreCase)
            ? identifier[DisplayPrefix.Length..]
            : string.Empty;
        int number = int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0
            ? parsed
            : fallbackNumber;
        return string.IsNullOrWhiteSpace(friendlyName)
            ? formatter("settings.placement.monitor.display_name", [number])
            : formatter("settings.placement.monitor.display_name_friendly", [number, friendlyName.Trim()]);
    }

    internal static IReadOnlyDictionary<string, string> GetFriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const uint ActivePaths = 0x00000002;
        const int InsufficientBuffer = 122;

        // A display can change between the size query and the path query.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(ActivePaths, out uint pathCount, out uint modeCount) != 0)
            {
                return names;
            }

            var paths = new DisplayConfigPathInfo[checked((int)pathCount)];
            var modes = new DisplayConfigModeInfo[checked((int)modeCount)];
            int result = NativeMethods.QueryDisplayConfig(
                ActivePaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                nint.Zero);
            if (result == InsufficientBuffer)
            {
                continue;
            }

            if (result != 0)
            {
                return names;
            }

            for (int index = 0; index < pathCount; index++)
            {
                DisplayConfigPathInfo path = paths[index];
                var source = new DisplayConfigSourceDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                        Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                        AdapterId = path.Source.AdapterId,
                        Id = path.Source.Id,
                    },
                    ViewGdiDeviceName = string.Empty,
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref source) != 0
                    || string.IsNullOrWhiteSpace(source.ViewGdiDeviceName)
                    || names.ContainsKey(source.ViewGdiDeviceName))
                {
                    continue;
                }

                var target = new DisplayConfigTargetDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = 2, // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                        Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        AdapterId = path.Target.AdapterId,
                        Id = path.Target.Id,
                    },
                    MonitorFriendlyDeviceName = string.Empty,
                    MonitorDevicePath = string.Empty,
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref target) == 0
                    && !string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName))
                {
                    names.Add(source.ViewGdiDeviceName, target.MonitorFriendlyDeviceName.Trim());
                }
            }

            break;
        }

        return names;
    }

    public static WindowsMonitorInfo Select(string? identifier)
    {
        IReadOnlyList<WindowsMonitorInfo> monitors = GetAll();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException(I18n.Get("settings.placement.monitor.unavailable"));
        }

        return identifier is not null
            ? monitors.FirstOrDefault(monitor => monitor.Identifier == identifier)
                ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? monitors[0]
            : monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    private delegate bool MonitorEnumerationCallback(
        nint monitor,
        nint deviceContext,
        nint monitorRectangle,
        nint data);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo Source;
        public DisplayConfigPathTargetInfo Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public NativeLuid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public NativeLuid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    // The native union is 48 bytes; no mode payload is needed to name a target.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public NativeLuid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint pathCount,
            out uint modeCount);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint pathCount,
            [Out] DisplayConfigPathInfo[] paths,
            ref uint modeCount,
            [Out] DisplayConfigModeInfo[] modes,
            nint currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName request);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(
            nint deviceContext,
            nint clipRectangle,
            MonitorEnumerationCallback callback,
            nint data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(
            nint monitor,
            MonitorDpiType dpiType,
            out uint dpiX,
            out uint dpiY);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();
    }
}
