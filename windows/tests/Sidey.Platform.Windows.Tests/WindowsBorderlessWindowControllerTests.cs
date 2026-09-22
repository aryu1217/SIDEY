using System.Runtime.InteropServices;
using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsBorderlessWindowControllerTests
{
    [Fact]
    public void KeepsTheWholeClientAreaAtCommonDpiSizesAndRestoresNormalFrameOnDetach()
    {
        nint window = CreateWindowEx(0, "STATIC", "SIDEY frame test", 0x00CF0000,
            0, 0, 400, 100, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            using (var frame = new WindowsBorderlessWindowController(window))
            {
                foreach ((int width, int height) in new[] { (400, 56), (500, 70), (600, 84), (800, 112) })
                {
                    Assert.True(SetWindowPos(window, 0, 0, 0, width, height, 0x0036));
                    Assert.True(GetClientRect(window, out Rect client));
                    Assert.Equal(width, client.Right - client.Left);
                    Assert.Equal(height, client.Bottom - client.Top);
                    // The area formerly occupied by the caption is now interactive content.
                    Assert.Equal((nint)1, SendMessage(window, 0x0084, 0, 0));
                }
            }

            Assert.True(SetWindowPos(window, 0, 0, 0, 400, 100, 0x0036));
            Assert.True(GetClientRect(window, out Rect restoredClient));
            Assert.True(restoredClient.Bottom - restoredClient.Top < 100);
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    [Fact]
    public void CanDisposeAfterNativeWindowDestruction()
    {
        nint window = CreateWindowEx(0, "STATIC", "SIDEY frame lifetime test", 0x00CF0000,
            0, 0, 400, 100, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        using var frame = new WindowsBorderlessWindowController(window);
        Assert.True(DestroyWindow(window));
        frame.Dispose();
        frame.BeginDrag();
        frame.DragTo();
        frame.EndDrag();
    }

    [Fact]
    public void ScreenAnchoredDragTracksPointerWithoutWindowCoordinateFeedback()
    {
        nint window = CreateWindowEx(0, "STATIC", "SIDEY drag test", 0x00CF0000,
            100, 100, 400, 100, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            using var frame = new WindowsBorderlessWindowController(window);
            frame.BeginDragAtScreenPosition(108, 120);
            frame.DragToScreenPosition(148, 150);
            Assert.True(GetWindowRect(window, out Rect duringDrag));
            Assert.Equal(140, duringDrag.Left);
            Assert.Equal(130, duringDrag.Top);
            Assert.Equal(400, duringDrag.Right - duringDrag.Left);

            // Every sample remains anchored to the original screen positions, even
            // though the window itself moved after the preceding pointer event.
            frame.DragToScreenPosition(158, 140);
            Assert.True(GetWindowRect(window, out Rect nextMove));
            Assert.Equal(150, nextMove.Left);
            Assert.Equal(120, nextMove.Top);

            frame.DragToScreenPosition(158, 140);
            Assert.True(GetWindowRect(window, out Rect repeatedMove));
            Assert.Equal(nextMove.Left, repeatedMove.Left);
            Assert.Equal(nextMove.Top, repeatedMove.Top);

            frame.EndDrag();
            frame.DragToScreenPosition(200, 200);
            Assert.True(GetWindowRect(window, out Rect afterCancel));
            Assert.Equal(nextMove.Left, afterCancel.Left);
            Assert.Equal(nextMove.Top, afterCancel.Top);

            // Physical screen pixels do not need a client-DIP scale conversion.
            frame.BeginDragAtScreenPosition(154, 132);
            frame.DragToScreenPosition(169, 147);
            Assert.True(GetWindowRect(window, out Rect secondDrag));
            Assert.Equal(165, secondDrag.Left);
            Assert.Equal(135, secondDrag.Top);
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    [Fact]
    public void DisplayChangesNotifyWhileAttachedAndStopAfterDisposal()
    {
        nint window = CreateWindowEx(0, "STATIC", "SIDEY display test", 0x00CF0000,
            0, 0, 400, 100, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            using var frame = new WindowsBorderlessWindowController(window);
            int notifications = 0;
            frame.DisplayConfigurationChanged += () => notifications++;

            _ = SendMessage(window, 0x007E, 32, 0);
            _ = SendMessage(window, 0x001A, 0, 0);
            Assert.Equal(2, notifications);

            frame.Dispose();
            _ = SendMessage(window, 0x007E, 32, 0);
            Assert.Equal(2, notifications);
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}
