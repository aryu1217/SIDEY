using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Sidey.Platform.Windows.Overlay;

/// <summary>
/// Owns one premultiplied BGRA DIB and presents it through UpdateLayeredWindow.
/// The DIB itself is allocated once. A world renderer can update its pixels
/// without allocating another bitmap or surface on a 30 FPS tick.
/// </summary>
public sealed unsafe class NativeLayeredBitmap : IDisposable
{
    private const byte SourceOver = 0;
    private const byte SourceAlpha = 1;

    private readonly HWND _window;
    private readonly HDC _memoryDeviceContext;
    private readonly HBITMAP _bitmap;
    private readonly HGDIOBJ _previousObject;
    private readonly nint _destination;
    private readonly int _byteCount;
    private bool _disposed;

    public NativeLayeredBitmap(nint windowHandle, int width, int height)
        : this(windowHandle, width, height, [])
    {
    }

    public NativeLayeredBitmap(
        nint windowHandle,
        int width,
        int height,
        ReadOnlySpan<byte> premultipliedBgra)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Layered bitmap presentation requires Windows.");
        }

        if (windowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHandle));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        int expectedByteCount = checked(width * height * 4);
        if (!premultipliedBgra.IsEmpty && premultipliedBgra.Length != expectedByteCount)
        {
            throw new ArgumentException(
                $"Expected {expectedByteCount} BGRA bytes but received {premultipliedBgra.Length}.",
                nameof(premultipliedBgra));
        }

        _window = new HWND((void*)windowHandle);
        Width = width;
        Height = height;

        HDC screenDeviceContext = PInvoke.GetDC(HWND.Null);
        if (screenDeviceContext.IsNull)
        {
            throw LastWin32Exception("GetDC failed while preparing a layered bitmap.");
        }

        HDC memoryDeviceContext = default;
        HBITMAP bitmap = default;
        HGDIOBJ previousObject = default;
        try
        {
            memoryDeviceContext = PInvoke.CreateCompatibleDC(screenDeviceContext);
            if (memoryDeviceContext.IsNull)
            {
                throw LastWin32Exception("CreateCompatibleDC failed.");
            }

            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)expectedByteCount,
                },
            };
            void* destination;
            bitmap = PInvoke.CreateDIBSection(
                screenDeviceContext,
                &bitmapInfo,
                DIB_USAGE.DIB_RGB_COLORS,
                &destination,
                HANDLE.Null,
                0);
            if (bitmap.IsNull || destination is null)
            {
                throw LastWin32Exception("CreateDIBSection failed.");
            }

            if (premultipliedBgra.IsEmpty)
            {
                new Span<byte>(destination, expectedByteCount).Clear();
            }
            else
            {
                fixed (byte* source = premultipliedBgra)
                {
                    Buffer.MemoryCopy(source, destination, expectedByteCount, expectedByteCount);
                }
            }

            previousObject = PInvoke.SelectObject(memoryDeviceContext, bitmap);
            if (previousObject.IsNull || (nint)previousObject.Value == -1)
            {
                throw LastWin32Exception("SelectObject failed for the layered bitmap.");
            }

            _memoryDeviceContext = memoryDeviceContext;
            _bitmap = bitmap;
            _previousObject = previousObject;
            _destination = (nint)destination;
            _byteCount = expectedByteCount;
        }
        catch
        {
            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(bitmap);
            }

            if (!memoryDeviceContext.IsNull)
            {
                PInvoke.DeleteDC(memoryDeviceContext);
            }

            throw;
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screenDeviceContext);
        }
    }

    public int Width { get; }
    public int Height { get; }
    public nint WindowHandle => (nint)_window.Value;

    public Span<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<byte>((void*)_destination, _byteCount);
        }
    }

    public void UpdatePixels(ReadOnlySpan<byte> premultipliedBgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (premultipliedBgra.Length != _byteCount)
        {
            throw new ArgumentException(
                $"Expected {_byteCount} BGRA bytes but received {premultipliedBgra.Length}.",
                nameof(premultipliedBgra));
        }

        fixed (byte* source = premultipliedBgra)
        {
            Buffer.MemoryCopy(source, (void*)_destination, _byteCount, _byteCount);
        }
    }

    public void Present(int screenX, int screenY, byte opacity = byte.MaxValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HDC screenDeviceContext = PInvoke.GetDC(HWND.Null);
        if (screenDeviceContext.IsNull)
        {
            throw LastWin32Exception("GetDC failed while presenting a layered bitmap.");
        }

        try
        {
            var destination = new Point(screenX, screenY);
            var size = new SIZE(Width, Height);
            Point source = Point.Empty;
            var blend = new BLENDFUNCTION
            {
                BlendOp = SourceOver,
                BlendFlags = 0,
                SourceConstantAlpha = opacity,
                AlphaFormat = SourceAlpha,
            };
            if (!PInvoke.UpdateLayeredWindow(
                    _window,
                    screenDeviceContext,
                    destination,
                    size,
                    _memoryDeviceContext,
                    source,
                    default,
                    blend,
                    UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
            {
                throw LastWin32Exception("UpdateLayeredWindow failed.");
            }
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screenDeviceContext);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PInvoke.SelectObject(_memoryDeviceContext, _previousObject);
        PInvoke.DeleteObject(_bitmap);
        PInvoke.DeleteDC(_memoryDeviceContext);
    }

    private static Win32Exception LastWin32Exception(string message) =>
        new(Marshal.GetLastPInvokeError(), message);
}
