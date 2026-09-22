using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sidey.Platform.Windows.Windowing;

// Keep the native window/presenter intact and remove only its non-client area.
// Attach and dispose on the window's owning UI thread.
public sealed class WindowsBorderlessWindowController : IDisposable
{
    private const nuint SubclassId = 0x5349424C;
    private readonly WindowSubclassProcedure _windowProcedure;
    private nint _windowHandle;
    private DragAnchor? _dragAnchor;
    private bool _dragGripHovered;

    public event Action? DisplayConfigurationChanged;

    public WindowsBorderlessWindowController(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _windowProcedure = WindowProcedure;
        if (!SetWindowSubclass(windowHandle, _windowProcedure, SubclassId, 0))
        {
            _windowHandle = nint.Zero;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // SWP_FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE.
        if (!SetWindowPos(windowHandle, 0, 0, 0, 0, 0, 0x0037))
        {
            int error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }

        // A fully client-drawn window no longer qualifies for automatic rounding.
        // Opt into DWM's smooth outer corners without changing the presenter.
        int cornerPreference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(windowHandle, 33, ref cornerPreference, sizeof(int));
    }

    // Track the pointer and window in physical screen pixels. Client coordinates move
    // with the window and create a feedback loop when used to position that same window.
    public void BeginDrag()
    {
        if (_windowHandle != nint.Zero && GetCursorPos(out NativePoint pointer))
        {
            BeginDragAtScreenPosition(pointer.X, pointer.Y);
        }
    }

    public void DragTo()
    {
        if (_windowHandle != nint.Zero && GetCursorPos(out NativePoint pointer))
        {
            DragToScreenPosition(pointer.X, pointer.Y);
        }
    }

    internal void BeginDragAtScreenPosition(int pointerX, int pointerY)
    {
        if (_windowHandle != nint.Zero && GetWindowRect(_windowHandle, out WindowRect bounds))
        {
            _dragAnchor = new DragAnchor(pointerX, pointerY, bounds.Left, bounds.Top);
        }
    }

    internal void DragToScreenPosition(int pointerX, int pointerY)
    {
        if (_windowHandle == nint.Zero || _dragAnchor is not { } anchor)
        {
            return;
        }

        int targetX = anchor.WindowX + pointerX - anchor.PointerX;
        int targetY = anchor.WindowY + pointerY - anchor.PointerY;
        if (GetWindowRect(_windowHandle, out WindowRect bounds) &&
            (bounds.Left != targetX || bounds.Top != targetY))
        {
            // NOSIZE | NOZORDER | NOACTIVATE; update the real window on every move.
            _ = SetWindowPos(_windowHandle, 0, targetX, targetY, 0, 0, 0x0015);
        }
    }

    public void EndDrag() => _dragAnchor = null;

    public void SetDragGripHovered(bool hovered)
    {
        _dragGripHovered = hovered;
        if (_windowHandle != nint.Zero)
            _ = SetCursor(LoadCursor(0, (nint)(hovered ? 32649 : 32512)));
    }

    public void RefreshDragGripCursor()
    {
        if (_dragGripHovered && _windowHandle != nint.Zero)
            _ = SetCursor(LoadCursor(0, (nint)32649)); // IDC_HAND
    }

    public void Dispose()
    {
        EndDrag();
        if (_windowHandle != nint.Zero)
        {
            _ = RemoveWindowSubclass(_windowHandle, _windowProcedure, SubclassId);
            _windowHandle = nint.Zero;
        }

        GC.KeepAlive(_windowProcedure);
    }

    private nint WindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        switch (message)
        {
            case 0x0020 when _dragGripHovered: // WM_SETCURSOR
                RefreshDragGripCursor();
                return 1;
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x02E0: // WM_DPICHANGED
            case 0x001A: // WM_SETTINGCHANGE (includes work area changes)
                DisplayConfigurationChanged?.Invoke();
                break;
            case 0x0083: // WM_NCCALCSIZE: retain the full proposed window rectangle.
                return 0;
            case 0x0084: // WM_NCHITTEST: no invisible caption buttons or resize edges.
                return 1; // HTCLIENT
            case 0x0082: // WM_NCDESTROY: release the callback before the HWND is reused.
                Dispose();
                break;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclassProcedure(
        nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint window, WindowSubclassProcedure callback, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint window, WindowSubclassProcedure callback, nuint subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", ExactSpelling = true)]
    private static extern nint LoadCursor(nint instance, nint name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetCursor(nint cursor);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint window, uint attribute, ref int value, int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private readonly record struct DragAnchor(int PointerX, int PointerY, int WindowX, int WindowY);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect bounds);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
