using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Sidey.Platform.Windows.Overlay;

public enum NativeOverlayWindowRole
{
    World,
    Hotspot,
}

public readonly record struct NativePixelRect(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public sealed unsafe class NativeOverlayWindow : IDisposable
{
    private const string WindowClassName = "SIDEY.NativeOverlayWindow";
    private const byte AlmostTransparent = 1;
    private static readonly Lock s_registrationLock = new();
    private static readonly ConcurrentDictionary<nint, NativeOverlayWindowRole> s_roles = new();
    private static readonly ConcurrentDictionary<nint, Action> s_activations = new();
    private static readonly ConcurrentDictionary<nint, Action> s_doubleClickActivations = new();
    private static readonly ConcurrentDictionary<nint, LeftClickState> s_leftClicks = new();
    private static readonly ConcurrentDictionary<nint, Action<bool>> s_rightClickActivations = new();
    private static readonly ConcurrentDictionary<nint, uint> s_ownerThreads = new();
    private static readonly ConcurrentDictionary<uint, int> s_threadWindowCounts = new();
    private static readonly WNDPROC s_windowProcedureCallback = WindowProcedure;
    private static readonly HWND s_topmostWindow = new((void*)(-1));
    private static readonly HWND s_notTopmostWindow = new((void*)(-2));
    private static bool s_classRegistered;

    private HWND _handle;
    private readonly uint _ownerThreadId;
    private nint _yieldBehindWindow;
    private bool _isTopmost = true;
    private bool _disposed;

    private NativeOverlayWindow(
        HWND handle,
        NativeOverlayWindowRole role,
        Action? activated,
        Action? doubleClicked,
        Action<bool>? rightClicked)
    {
        _handle = handle;
        Role = role;
        nint handleValue = (nint)handle.Value;
        uint ownerThread = PInvoke.GetCurrentThreadId();
        _ownerThreadId = ownerThread;
        s_roles[handleValue] = role;
        if (activated is not null)
        {
            s_activations[handleValue] = activated;
        }
        if (doubleClicked is not null)
        {
            s_doubleClickActivations[handleValue] = doubleClicked;
            s_leftClicks[handleValue] = new LeftClickState();
        }
        if (rightClicked is not null)
        {
            s_rightClickActivations[handleValue] = rightClicked;
        }
        s_ownerThreads[handleValue] = ownerThread;
        s_threadWindowCounts.AddOrUpdate(ownerThread, 1, static (_, count) => count + 1);
    }

    public static double DoubleClickIntervalSeconds => NativeMethods.GetDoubleClickTime() / 1000d;

    public NativeOverlayWindowRole Role { get; }
    public nint Handle => (nint)_handle.Value;
    public bool IsCreated => _handle != HWND.Null;

    public static uint ExtendedStyleBits(NativeOverlayWindowRole role) =>
        (uint)WindowStyles.ExtendedStyle(role);

    public static unsafe NativeOverlayWindow Create(
        NativeOverlayWindowRole role,
        NativePixelRect initialBounds,
        Action? activated = null,
        Action? doubleClicked = null,
        Action<bool>? rightClicked = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NativeOverlayWindow requires Windows.");
        }

        if (!initialBounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBounds));
        }

        EnsureWindowClass();
        FreeLibrarySafeHandle module = PInvoke.GetModuleHandle((string?)null);
        WINDOW_EX_STYLE extendedStyle = WindowStyles.ExtendedStyle(role);
        HWND handle = PInvoke.CreateWindowEx(
            extendedStyle,
            WindowClassName,
            "SIDEY Overlay",
            WINDOW_STYLE.WS_POPUP,
            initialBounds.X,
            initialBounds.Y,
            initialBounds.Width,
            initialBounds.Height,
            HWND.Null,
            new FreeLibrarySafeHandle(),
            module,
            null);

        if (handle == HWND.Null)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowExW failed.");
        }

        if (role == NativeOverlayWindowRole.Hotspot
            && !PInvoke.SetLayeredWindowAttributes(
                handle,
                default,
                AlmostTransparent,
                LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
        {
            PInvoke.DestroyWindow(handle);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetLayeredWindowAttributes failed.");
        }

        var window = new NativeOverlayWindow(handle, role, activated, doubleClicked, rightClicked);
        window.SetBounds(initialBounds, visible: true);
        return window;
    }

    public void SetBounds(NativePixelRect bounds, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        flags |= visible ? SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW : SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW;
        if (!PInvoke.SetWindowPos(
                _handle,
                ZOrderAnchor(),
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowPos failed.");
        }
    }

    public void SetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!visible)
        {
            PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_HIDE);
            return;
        }

        ApplyZOrder(SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    }

    public void EnsureTopmost()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isTopmost = true;
        _yieldBehindWindow = nint.Zero;
        ApplyZOrder(default);
    }

    public void YieldBehind(nint window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isTopmost = false;
        _yieldBehindWindow = window;
        ApplyZOrder(default);
    }

    private void ApplyZOrder(SET_WINDOW_POS_FLAGS additionalFlags)
    {
        SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER
            | additionalFlags;
        if (!PInvoke.SetWindowPos(_handle, ZOrderAnchor(), 0, 0, 0, 0, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowPos failed while updating overlay Z-order.");
        }
    }

    private HWND ZOrderAnchor()
    {
        if (_isTopmost)
        {
            return s_topmostWindow;
        }

        var yieldWindow = new HWND((void*)_yieldBehindWindow);
        return yieldWindow != HWND.Null && NativeMethods.IsWindow(_yieldBehindWindow)
            ? yieldWindow
            : s_notTopmostWindow;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HWND handle = _handle;
        _handle = HWND.Null;
        if (handle != HWND.Null)
        {
            s_roles.TryRemove((nint)handle.Value, out _);
            s_activations.TryRemove((nint)handle.Value, out _);
            s_doubleClickActivations.TryRemove((nint)handle.Value, out _);
            s_rightClickActivations.TryRemove((nint)handle.Value, out _);
            if (PInvoke.GetCurrentThreadId() == _ownerThreadId)
            {
                PInvoke.DestroyWindow(handle);
            }
            else
            {
                PInvoke.PostMessage(handle, PInvoke.WM_CLOSE, default, default);
            }
        }
    }

    private static unsafe void EnsureWindowClass()
    {
        lock (s_registrationLock)
        {
            if (s_classRegistered)
            {
                return;
            }

            FreeLibrarySafeHandle module = PInvoke.GetModuleHandle((string?)null);
            fixed (char* className = WindowClassName)
            {
                var windowClass = new WNDCLASSW
                {
                    style = (WNDCLASS_STYLES)0x0008, // CS_DBLCLKS
                    lpfnWndProc = s_windowProcedureCallback,
                    hInstance = new HINSTANCE(module.DangerousGetHandle()),
                    hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                    lpszClassName = new PCWSTR(className),
                };

                if (PInvoke.RegisterClass(windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassW failed.");
                }
            }

            s_classRegistered = true;
        }
    }

    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_NCHITTEST
            && s_roles.TryGetValue((nint)window.Value, out NativeOverlayWindowRole role)
            && role == NativeOverlayWindowRole.World)
        {
            return new LRESULT(-1); // HTTRANSPARENT is defense-in-depth; WS_EX_LAYERED | WS_EX_TRANSPARENT owns cross-process pass-through.
        }

        if (message == PInvoke.WM_MOUSEACTIVATE)
        {
            return new LRESULT(3); // MA_NOACTIVATE
        }

        nint handleValue = (nint)window.Value;
        if (s_leftClicks.TryGetValue(handleValue, out LeftClickState? leftClick))
        {
            if (message == 0x0018 && wParam.Value == 0) // WM_SHOWWINDOW: hidden
            {
                leftClick._ignoreRelease = false;
            }
            else if (message == 0x0201) // WM_LBUTTONDOWN: a new single-click sequence
            {
                leftClick._ignoreRelease = false;
            }
            else if (message == 0x0203) // WM_LBUTTONDBLCLK
            {
                leftClick._ignoreRelease = true;
            }
            else if (message == PInvoke.WM_LBUTTONUP)
            {
                if (leftClick._ignoreRelease)
                {
                    leftClick._ignoreRelease = false;
                    return default;
                }
                InvokeActivation(handleValue);
                return default;
            }
        }

        if (message == PInvoke.WM_LBUTTONUP)
        {
            InvokeActivation(handleValue);
            return default;
        }

        if (message == 0x0203 // WM_LBUTTONDBLCLK
            && s_doubleClickActivations.TryGetValue((nint)window.Value, out Action? doubleClicked))
        {
            try
            {
                doubleClicked();
            }
            catch (Exception exception)
            {
                Trace.TraceError("SIDEY hotspot double-click callback failed: {0}", exception);
            }

            return default;
        }

        if (message is 0x0204 or 0x0206 // WM_RBUTTONDOWN / WM_RBUTTONDBLCLK
            && s_rightClickActivations.TryGetValue((nint)window.Value, out Action<bool>? rightClicked))
        {
            try
            {
                rightClicked(message == 0x0206);
            }
            catch (Exception exception)
            {
                Trace.TraceError("SIDEY hotspot right-click callback failed: {0}", exception);
            }

            return default;
        }

        if (message == PInvoke.WM_CLOSE)
        {
            PInvoke.DestroyWindow(window);
            return default;
        }

        if (message == PInvoke.WM_DESTROY)
        {
            nint handle = (nint)window.Value;
            s_roles.TryRemove(handle, out _);
            s_activations.TryRemove(handle, out _);
            s_doubleClickActivations.TryRemove(handle, out _);
            if (s_leftClicks.TryRemove(handle, out LeftClickState? pendingClick))
            {
                pendingClick._ignoreRelease = false;
            }
            s_rightClickActivations.TryRemove(handle, out _);
            if (s_ownerThreads.TryRemove(handle, out uint ownerThread))
            {
                int remaining = s_threadWindowCounts.AddOrUpdate(
                    ownerThread,
                    0,
                    static (_, count) => Math.Max(0, count - 1));
                if (remaining == 0)
                {
                    s_threadWindowCounts.TryRemove(ownerThread, out _);
                    PInvoke.PostQuitMessage(0);
                }
            }
            return default;
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    private static void InvokeActivation(nint handle)
    {
        if (s_activations.TryGetValue(handle, out Action? activated))
        {
            try
            {
                activated();
            }
            catch (Exception exception)
            {
                Trace.TraceError("SIDEY hotspot callback failed: {0}", exception);
            }
        }
    }

    private sealed class LeftClickState
    {
        internal bool _ignoreRelease;
    }
}

internal static class WindowStyles
{
    internal static WINDOW_EX_STYLE ExtendedStyle(NativeOverlayWindowRole role)
    {
        WINDOW_EX_STYLE style = WINDOW_EX_STYLE.WS_EX_TOPMOST
            | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_LAYERED;
        return role == NativeOverlayWindowRole.World
            ? style | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
            : style;
    }
}
