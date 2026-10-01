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

public enum NativeDragPointerKind { Began, Moved, Released, Cancelled }

public readonly record struct NativeDragPointerEvent(NativeDragPointerKind Kind, int ScreenX, int ScreenY);

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
    private static readonly ConcurrentDictionary<nint, Action<NativeDragPointerEvent>> s_dragPointers = new();
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
        Action<bool>? rightClicked,
        Action<NativeDragPointerEvent>? dragPointer)
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
        if (role == NativeOverlayWindowRole.Hotspot)
        {
            s_leftClicks[handleValue] = new LeftClickState();
        }
        if (doubleClicked is not null)
        {
            s_doubleClickActivations[handleValue] = doubleClicked;
        }
        if (rightClicked is not null)
        {
            s_rightClickActivations[handleValue] = rightClicked;
        }
        if (dragPointer is not null)
        {
            s_dragPointers[handleValue] = dragPointer;
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
        Action<bool>? rightClicked = null,
        Action<NativeDragPointerEvent>? dragPointer = null)
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

        var window = new NativeOverlayWindow(handle, role, activated, doubleClicked, rightClicked, dragPointer);
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

        // Rendering can reposition hotspots while holding its frame lock. Queue cross-thread
        // window moves so the HWND thread never waits for that lock while we wait for it.
        SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out ScreenPoint point);

        [DllImport("user32.dll")]
        internal static extern nint SetCapture(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern nint GetCapture();

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

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
            s_dragPointers.TryRemove((nint)handle.Value, out _);
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
                CancelDrag(handleValue, leftClick);
            }
            else if (message is 0x0201 or 0x0203) // WM_LBUTTONDOWN / WM_LBUTTONDBLCLK
            {
                if (NativeMethods.GetCursorPos(out ScreenPoint point))
                {
                    leftClick._pressed = true;
                    leftClick._dragging = false;
                    leftClick._doubleClick = message == 0x0203;
                    leftClick._startX = point.X;
                    leftClick._startY = point.Y;
                    NativeMethods.SetCapture(handleValue);
                }
                return default;
            }
            else if (message == 0x0200 && leftClick._pressed) // WM_MOUSEMOVE
            {
                if (NativeMethods.GetCursorPos(out ScreenPoint point))
                {
                    double dx = point.X - leftClick._startX;
                    double dy = point.Y - leftClick._startY;
                    double threshold = 5d * Math.Max(1d, NativeMethods.GetDpiForWindow(handleValue) / 96d);
                    if (!leftClick._dragging && dx * dx + dy * dy >= threshold * threshold)
                    {
                        leftClick._dragging = true;
                        InvokeDrag(handleValue, new NativeDragPointerEvent(
                            NativeDragPointerKind.Began, leftClick._startX, leftClick._startY));
                    }
                    if (leftClick._dragging)
                    {
                        InvokeDrag(handleValue, new NativeDragPointerEvent(
                            NativeDragPointerKind.Moved, point.X, point.Y));
                    }
                }
                return default;
            }
            else if (message == PInvoke.WM_LBUTTONUP && leftClick._pressed)
            {
                leftClick._pressed = false;
                bool doubleClick = leftClick._doubleClick;
                if (leftClick._dragging)
                {
                    if (NativeMethods.GetCursorPos(out ScreenPoint point))
                    {
                        InvokeDrag(handleValue, new NativeDragPointerEvent(
                            NativeDragPointerKind.Moved, point.X, point.Y));
                        InvokeDrag(handleValue, new NativeDragPointerEvent(
                            NativeDragPointerKind.Released, point.X, point.Y));
                    }
                    else
                    {
                        InvokeDrag(handleValue, new NativeDragPointerEvent(NativeDragPointerKind.Cancelled, 0, 0));
                    }
                    leftClick._dragging = false;
                    if (NativeMethods.GetCapture() == handleValue)
                    {
                        NativeMethods.ReleaseCapture();
                    }
                    return default;
                }
                if (NativeMethods.GetCapture() == handleValue)
                {
                    NativeMethods.ReleaseCapture();
                }
                if (doubleClick)
                {
                    InvokeDoubleClick(handleValue);
                }
                else
                {
                    InvokeActivation(handleValue);
                }
                return default;
            }
            else if (message is 0x0215 or 0x001F) // WM_CAPTURECHANGED / WM_CANCELMODE
            {
                CancelDrag(handleValue, leftClick);
            }
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
                CancelDrag(handle, pendingClick);
            }
            s_rightClickActivations.TryRemove(handle, out _);
            s_dragPointers.TryRemove(handle, out _);
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

    private static void InvokeDoubleClick(nint handle)
    {
        if (s_doubleClickActivations.TryGetValue(handle, out Action? doubleClicked))
        {
            try
            { doubleClicked(); }
            catch (Exception exception) { Trace.TraceError("SIDEY hotspot double-click callback failed: {0}", exception); }
        }
    }

    private static void InvokeDrag(nint handle, NativeDragPointerEvent pointer)
    {
        if (s_dragPointers.TryGetValue(handle, out Action<NativeDragPointerEvent>? callback))
        {
            try
            { callback(pointer); }
            catch (Exception) { Trace.TraceError("SIDEY hotspot drag callback failed."); }
        }
    }

    private static void CancelDrag(nint handle, LeftClickState click)
    {
        if (click._pressed && click._dragging)
        {
            InvokeDrag(handle, new NativeDragPointerEvent(NativeDragPointerKind.Cancelled, 0, 0));
        }
        click._pressed = false;
        click._dragging = false;
        click._doubleClick = false;
        if (NativeMethods.GetCapture() == handle)
        {
            NativeMethods.ReleaseCapture();
        }
    }

    private sealed class LeftClickState
    {
        internal bool _pressed;
        internal bool _dragging;
        internal bool _doubleClick;
        internal int _startX;
        internal int _startY;
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
