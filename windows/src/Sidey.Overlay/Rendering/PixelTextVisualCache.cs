using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Core.Overlay;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.UI;
using Windows.UI.Text;

namespace Sidey.Overlay.Rendering;

internal readonly record struct BubblePalette(
    byte FillRed,
    byte FillGreen,
    byte FillBlue,
    byte FillAlpha,
    byte BorderRed,
    byte BorderGreen,
    byte BorderBlue,
    byte BorderAlpha);

internal readonly record struct PixelVisualBodyBounds(
    int X,
    int Y,
    int Width,
    int Height);

internal sealed record PremultipliedVisual(
    byte[] Pixels,
    int Width,
    int Height,
    BubblePalette? BubblePalette = null,
    PixelVisualBodyBounds? BubbleBodyBounds = null);

internal sealed record PixelMemberVisuals(
    PremultipliedVisual Nameplate,
    IReadOnlyDictionary<Guid, PremultipliedVisual> MessageBubbles,
    IReadOnlyList<PremultipliedVisual> TypingFrames,
    PremultipliedVisual? Doze);

/// <summary>
/// Rasterizes text only when an immutable world snapshot changes. The 30 FPS
/// render loop composites cached premultiplied BGRA buffers and never creates
/// text layouts, bitmaps, or GPU surfaces per tick.
/// </summary>
internal sealed class PixelTextVisualCache : IDisposable
{
    private const float NameplateHeightDip = 20f;
    private const float NameplateMinimumWidthDip = 20f;
    private const float NameplateMaximumWidthDip = 190f;
    private const float NameplateHorizontalPaddingDip = 4f;
    private const float NameplateStatusSpacingDip = 5f;
    private const float NameplateStatusRadiusDip = 3f;
    private const float NameplateFontSizeDip = 11f;
    private const float BubbleMaximumWidthDip = 220f;
    private const float BubbleMinimumWidthDip = 28f;
    private const float BubbleHorizontalPaddingDip = 8f;
    private const float BubbleVerticalPaddingDip = 7f;
    private const float BubbleFontSizeDip = NameplateFontSizeDip;
    private const float BubbleCornerRadiusDip = 9f;
    private const float BubbleBorderWidthDip = 1f;
    private const float TypingBubbleWidthDip = 42f;
    private const float TypingBubbleHeightDip = 30f;
    private const float DozeWidthDip = 42f;
    private const float DozeHeightDip = 28f;
    private const float DozeFontSizeDip = 14f;
    private const float DozeOutlineWidthDip = 2f;
    private const int DozeOutlineSampleCount = 12;
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly float _dpi;
    private readonly OverlayEdge _edge;
    private readonly float _bubbleMaximumWidthDip;
    private readonly int _decorationScale;
    private readonly Dictionary<string, PremultipliedVisual> _decorations = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CacheEntry> _entries = [];
    private readonly Dictionary<Guid, List<ActiveBubble>> _bubblesBySender = [];
    private readonly HashSet<Guid> _currentMemberIds = [];
    private readonly List<Guid> _removedMemberIds = [];
    private bool _disposed;

    public PixelTextVisualCache(
        uint dpi,
        OverlayEdge edge,
        float bubbleMaximumWidthDip = BubbleMaximumWidthDip,
        string? bubbleAssetRoot = null)
    {
        _dpi = dpi;
        _edge = edge;
        _bubbleMaximumWidthDip = Math.Clamp(
            bubbleMaximumWidthDip,
            24f,
            BubbleMaximumWidthDip);
        // Bubble decorations are authored at their 16-DIP presentation size.
        // Character sprites intentionally use a separate 2x logical scale.
        _decorationScale = Math.Max(
            1,
            (int)Math.Round(dpi / 96d, MidpointRounding.AwayFromZero));
        if (bubbleAssetRoot is not null)
        {
            foreach (string id in CosmeticCatalog.BubbleStyleIds)
            {
                _decorations[id] = LoadDecoration(bubbleAssetRoot, id);
            }
        }
    }

    public void Update(WorldSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _currentMemberIds.Clear();
        foreach (PixelWorldMember member in snapshot.Members)
        {
            _currentMemberIds.Add(member.Id);
        }

        _removedMemberIds.Clear();
        foreach (Guid memberId in _entries.Keys)
        {
            if (!_currentMemberIds.Contains(memberId))
            {
                _removedMemberIds.Add(memberId);
            }
        }
        foreach (Guid removed in _removedMemberIds)
        {
            Clear(_entries[removed].Visuals);
            _entries.Remove(removed);
        }

        _bubblesBySender.Clear();
        foreach (ActiveBubble bubble in snapshot.Bubbles)
        {
            if (!_bubblesBySender.TryGetValue(bubble.SenderId, out List<ActiveBubble>? senderBubbles))
            {
                senderBubbles = [];
                _bubblesBySender.Add(bubble.SenderId, senderBubbles);
            }
            senderBubbles.Add(bubble);
        }
        foreach (List<ActiveBubble> senderBubbles in _bubblesBySender.Values)
        {
            senderBubbles.Sort(CompareBubbles);
            if (senderBubbles.Count > ActiveBubbleLedger.MaximumVisiblePerSender)
            {
                senderBubbles.RemoveRange(
                    0,
                    senderBubbles.Count - ActiveBubbleLedger.MaximumVisiblePerSender);
            }
        }
        foreach (PixelWorldMember member in snapshot.Members)
        {
            _bubblesBySender.TryGetValue(member.Id, out List<ActiveBubble>? bubbles);
            BubbleVisualKey? olderBubble = bubbles is { Count: 2 }
                ? new BubbleVisualKey(bubbles[0].MessageId, bubbles[0].Body, bubbles[0].BubbleStyleId)
                : null;
            BubbleVisualKey? latestBubble = bubbles is { Count: > 0 }
                ? new BubbleVisualKey(bubbles[^1].MessageId, bubbles[^1].Body, bubbles[^1].BubbleStyleId)
                : null;
            var key = new VisualKey(
                member.IsCurrentUser
                    ? I18n.Format("overlay.currentUser", member.Nickname)
                    : member.Nickname,
                member.Presence,
                olderBubble,
                latestBubble,
                member.IsTyping,
                member.EquippedBubbleStyleId);
            if (_entries.TryGetValue(member.Id, out CacheEntry? existing) && existing.Key == key)
            {
                continue;
            }

            if (existing is not null)
            {
                Clear(existing.Visuals);
            }
            _entries[member.Id] = new CacheEntry(key, Build(key));
        }
    }

    public PixelMemberVisuals Get(Guid memberId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _entries.TryGetValue(memberId, out CacheEntry? entry)
            ? entry.Visuals
            : throw new KeyNotFoundException($"No cached text visuals for member {memberId:D}.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (CacheEntry entry in _entries.Values)
        {
            Clear(entry.Visuals);
        }
        _entries.Clear();
        foreach (PremultipliedVisual decoration in _decorations.Values)
        {
            Array.Clear(decoration.Pixels);
        }
        _decorations.Clear();
    }

    private PixelMemberVisuals Build(VisualKey key)
    {
        PremultipliedVisual nameplate = PixelVisualOrientation.ApplyToNameplate(RasterizeNameplate(
            key.Name,
            StatusColor(key.Presence)), _edge);

        var messageBubbles = new Dictionary<Guid, PremultipliedVisual>(
            ActiveBubbleLedger.MaximumVisiblePerSender);
        foreach (BubbleVisualKey bubble in BubbleKeys(key))
        {
            BubbleTheme theme = ResolveTheme(bubble.BubbleStyleId);
            (float width, float height) = MeasureBubble(bubble.Body);
            PremultipliedVisual visual = Rasterize(
                bubble.Body,
                width,
                height,
                background: theme.Background,
                foreground: theme.Foreground,
                cornerRadius: BubbleCornerRadiusDip,
                fontSize: BubbleFontSizeDip,
                horizontalPadding: BubbleHorizontalPaddingDip,
                verticalPadding: BubbleVerticalPaddingDip,
                border: theme.Border) with
            {
                BubblePalette = theme.Palette,
            };
            visual = Decorate(visual, bubble.BubbleStyleId);
            messageBubbles.Add(bubble.MessageId, PixelVisualOrientation.Apply(visual, _edge));
        }
        IReadOnlyList<PremultipliedVisual> typingFrames = key.IsTyping
            ? [
                BuildTypingFrame(".", key.TypingBubbleStyleId),
                BuildTypingFrame("..", key.TypingBubbleStyleId),
                BuildTypingFrame("...", key.TypingBubbleStyleId),
            ]
            : [];

        PremultipliedVisual? doze = key.Presence == PresenceState.Away
            ? PixelVisualOrientation.Apply(RasterizeDoze(), _edge)
            : null;
        return new PixelMemberVisuals(nameplate, messageBubbles, typingFrames, doze);
    }

    private PremultipliedVisual BuildTypingFrame(string body, string? bubbleStyleId)
    {
        BubbleTheme theme = ResolveTheme(bubbleStyleId);
        PremultipliedVisual visual = Rasterize(
            body,
            Math.Min(TypingBubbleWidthDip, _bubbleMaximumWidthDip),
            TypingBubbleHeightDip,
            background: theme.Background,
            foreground: theme.Foreground,
            cornerRadius: BubbleCornerRadiusDip,
            fontSize: 16f,
            border: theme.Border) with
        {
            BubblePalette = theme.Palette,
        };
        visual = Decorate(visual, bubbleStyleId);
        return PixelVisualOrientation.Apply(visual, _edge);
    }

    private static IEnumerable<BubbleVisualKey> BubbleKeys(VisualKey key)
    {
        if (key.OlderBubble is { } older)
        {
            yield return older;
        }
        if (key.LatestBubble is { } latest)
        {
            yield return latest;
        }
    }

    private PremultipliedVisual RasterizeDoze()
    {
        using var target = new CanvasRenderTarget(
            _device,
            DozeWidthDip,
            DozeHeightDip,
            _dpi,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        using (var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = DozeFontSizeDip,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        })
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            var textBounds = new Rect(
                DozeOutlineWidthDip,
                DozeOutlineWidthDip,
                DozeWidthDip - (DozeOutlineWidthDip * 2f),
                DozeHeightDip - (DozeOutlineWidthDip * 2f));
            var outline = Color.FromArgb(235, 20, 18, 15);
            for (int index = 0; index < DozeOutlineSampleCount; index++)
            {
                double angle = index * Math.PI * 2d / DozeOutlineSampleCount;
                drawing.DrawText(
                    "Zzz",
                    new Rect(
                        textBounds.X + (Math.Cos(angle) * DozeOutlineWidthDip),
                        textBounds.Y + (Math.Sin(angle) * DozeOutlineWidthDip),
                        textBounds.Width,
                        textBounds.Height),
                    outline,
                    format);
            }

            drawing.DrawText(
                "Zzz",
                textBounds,
                Color.FromArgb(255, 255, 149, 0),
                format);
        }

        BitmapSize size = target.SizeInPixels;
        return new PremultipliedVisual(
            target.GetPixelBytes(),
            checked((int)size.Width),
            checked((int)size.Height));
    }

    private (float Width, float Height) MeasureBubble(string body)
    {
        using var naturalFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = BubbleFontSizeDip,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var naturalLayout = new CanvasTextLayout(
            _device,
            body,
            naturalFormat,
            10_000f,
            1_000f);
        Rect naturalBounds = naturalLayout.LayoutBoundsIncludingTrailingWhitespace;
        float width = Math.Min(
            _bubbleMaximumWidthDip,
            Math.Max(
                BubbleMinimumWidthDip,
                (float)Math.Ceiling(naturalBounds.Width) + (BubbleHorizontalPaddingDip * 2f)));

        using var wrappedFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = BubbleFontSizeDip,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        using var wrappedLayout = new CanvasTextLayout(
            _device,
            body,
            wrappedFormat,
            width - (BubbleHorizontalPaddingDip * 2f),
            1_000f);
        Rect wrappedBounds = wrappedLayout.LayoutBoundsIncludingTrailingWhitespace;
        float height = Math.Max(
            28f,
            (float)Math.Ceiling(wrappedBounds.Height) + (BubbleVerticalPaddingDip * 2f));
        return (width, height);
    }

    private PremultipliedVisual RasterizeNameplate(
        string text,
        Color status)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = NameplateFontSizeDip,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(
            _device,
            text,
            format,
            NameplateMaximumWidthDip,
            NameplateHeightDip);
        float measuredTextWidth = (float)Math.Ceiling(layout.DrawBounds.Width);
        float boxWidthDip = Math.Clamp(
            measuredTextWidth + (NameplateHorizontalPaddingDip * 2f),
            NameplateMinimumWidthDip,
            NameplateMaximumWidthDip);
        float boxLeftDip = (NameplateStatusRadiusDip * 2f) + NameplateStatusSpacingDip;
        float widthDip = boxLeftDip + boxWidthDip;
        using var target = new CanvasRenderTarget(
            _device,
            widthDip,
            NameplateHeightDip,
            _dpi,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.FillRoundedRectangle(
                boxLeftDip,
                0,
                boxWidthDip,
                NameplateHeightDip,
                6f,
                6f,
                Color.FromArgb(158, 5, 6, 9));
            drawing.FillCircle(
                NameplateStatusRadiusDip,
                NameplateHeightDip / 2f,
                NameplateStatusRadiusDip,
                status);
            drawing.DrawText(
                text,
                new Rect(
                    boxLeftDip + NameplateHorizontalPaddingDip,
                    1f,
                    boxWidthDip - (NameplateHorizontalPaddingDip * 2f),
                    NameplateHeightDip - 2f),
                Color.FromArgb(255, 255, 255, 255),
                format);
        }

        BitmapSize size = target.SizeInPixels;
        return new PremultipliedVisual(
            target.GetPixelBytes(),
            checked((int)size.Width),
            checked((int)size.Height));
    }

    private PremultipliedVisual Rasterize(
        string text,
        float widthDip,
        float heightDip,
        Color background,
        Color foreground,
        float cornerRadius,
        float fontSize,
        float horizontalPadding = 4f,
        float verticalPadding = 1f,
        Color? border = null)
    {
        using var target = new CanvasRenderTarget(
            _device,
            widthDip,
            heightDip,
            _dpi,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        using (var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
        })
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            if (background.A > 0)
            {
                drawing.FillRoundedRectangle(
                    0,
                    0,
                    widthDip,
                    heightDip,
                    cornerRadius,
                    cornerRadius,
                    background);
            }
            if (border is { } borderColor)
            {
                float inset = BubbleBorderWidthDip / 2f;
                drawing.DrawRoundedRectangle(
                    inset,
                    inset,
                    widthDip - BubbleBorderWidthDip,
                    heightDip - BubbleBorderWidthDip,
                    cornerRadius,
                    cornerRadius,
                    borderColor,
                    BubbleBorderWidthDip);
            }
            drawing.DrawText(
                text,
                new Rect(
                    horizontalPadding,
                    verticalPadding,
                    widthDip - (horizontalPadding * 2f),
                    heightDip - (verticalPadding * 2f)),
                foreground,
                format);
        }

        BitmapSize size = target.SizeInPixels;
        return new PremultipliedVisual(
            target.GetPixelBytes(),
            checked((int)size.Width),
            checked((int)size.Height));
    }

    private static Color StatusColor(PresenceState presence) => presence switch
    {
        PresenceState.Online or PresenceState.Typing => Color.FromArgb(255, 52, 199, 89),
        PresenceState.Away => Color.FromArgb(255, 255, 149, 0),
        PresenceState.Offline => Color.FromArgb(255, 255, 59, 48),
        PresenceState.Reconnecting => Color.FromArgb(255, 142, 142, 147),
        _ => throw new ArgumentOutOfRangeException(nameof(presence)),
    };

    private static void Clear(PixelMemberVisuals visuals)
    {
        Array.Clear(visuals.Nameplate.Pixels);
        foreach (PremultipliedVisual messageBubble in visuals.MessageBubbles.Values)
        {
            Array.Clear(messageBubble.Pixels);
        }
        foreach (PremultipliedVisual typingBubble in visuals.TypingFrames)
        {
            Array.Clear(typingBubble.Pixels);
        }
        if (visuals.Doze is { } doze)
        {
            Array.Clear(doze.Pixels);
        }
    }

    private sealed record VisualKey(
        string Name,
        PresenceState Presence,
        BubbleVisualKey? OlderBubble,
        BubbleVisualKey? LatestBubble,
        bool IsTyping,
        string? TypingBubbleStyleId);

    private readonly record struct BubbleVisualKey(Guid MessageId, string Body, string? BubbleStyleId);

    private static BubbleTheme ResolveTheme(string? id) => id switch
    {
        "bubble_bunny_pink" => BubbleTheme.Create(0xF7, 0xA9, 0xB8, 0x1C, 0x1F, 0x29),
        "bubble_butter_chick" => BubbleTheme.Create(0xFF, 0xE3, 0x8A, 0x1C, 0x1F, 0x29),
        "bubble_starry_cat" => BubbleTheme.Create(0x40, 0x3A, 0x78, 0xFF, 0xF7, 0xE8),
        _ => BubbleTheme.Create(0xFF, 0xFF, 0xFF, 0x1B, 0x1F, 0x28),
    };

    private PremultipliedVisual LoadDecoration(string root, string id)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(root, id, "decoration.bgra"));
        const int SourceSize = 16;
        if (source.Length != SourceSize * SourceSize * 4)
        {
            throw new InvalidDataException($"{id} decoration has an invalid BGRA byte length.");
        }
        int size = SourceSize * _decorationScale;
        byte[] scaled = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int sourceIndex = (((y / _decorationScale) * SourceSize) + (x / _decorationScale)) * 4;
                int destinationIndex = ((y * size) + x) * 4;
                source.AsSpan(sourceIndex, 4).CopyTo(scaled.AsSpan(destinationIndex, 4));
            }
        }
        Array.Clear(source);
        return new PremultipliedVisual(scaled, size, size);
    }

    private PremultipliedVisual Decorate(PremultipliedVisual body, string? id)
    {
        if (id is null || !_decorations.TryGetValue(id, out PremultipliedVisual? decoration))
        {
            return body;
        }

        int inset = Math.Max(1, (int)Math.Round(2d * _dpi / 96d));
        int leadingOverflow = Math.Max(0, (decoration.Width / 2) - inset);
        int topOverflow = decoration.Height / 2;
        int width = checked(body.Width + leadingOverflow);
        int height = checked(body.Height + topOverflow);
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < body.Height; y++)
        {
            int source = y * body.Width * 4;
            int destination = (((y + topOverflow) * width) + leadingOverflow) * 4;
            body.Pixels.AsSpan(source, body.Width * 4).CopyTo(pixels.AsSpan(destination));
        }

        int decorationX = leadingOverflow + inset - (decoration.Width / 2);
        int decorationY = topOverflow - (decoration.Height / 2);
        for (int y = 0; y < decoration.Height && y + decorationY < height; y++)
        {
            for (int x = 0; x < decoration.Width && x + decorationX < width; x++)
            {
                int source = ((y * decoration.Width) + x) * 4;
                byte alpha = decoration.Pixels[source + 3];
                if (alpha == 0)
                {
                    continue;
                }
                int destination = (((y + decorationY) * width) + x + decorationX) * 4;
                int inverse = 255 - alpha;
                pixels[destination] = (byte)Math.Min(
                    255,
                    decoration.Pixels[source] + (pixels[destination] * inverse / 255));
                pixels[destination + 1] = (byte)Math.Min(
                    255,
                    decoration.Pixels[source + 1] + (pixels[destination + 1] * inverse / 255));
                pixels[destination + 2] = (byte)Math.Min(
                    255,
                    decoration.Pixels[source + 2] + (pixels[destination + 2] * inverse / 255));
                pixels[destination + 3] = (byte)Math.Min(
                    255,
                    alpha + (pixels[destination + 3] * inverse / 255));
            }
        }

        Array.Clear(body.Pixels);
        return new PremultipliedVisual(
            pixels,
            width,
            height,
            body.BubblePalette,
            new PixelVisualBodyBounds(
                leadingOverflow,
                topOverflow,
                body.Width,
                body.Height));
    }

    private readonly record struct BubbleTheme(
        Color Background,
        Color Foreground,
        Color Border,
        BubblePalette Palette)
    {
        public static BubbleTheme Create(
            byte backgroundRed,
            byte backgroundGreen,
            byte backgroundBlue,
            byte foregroundRed,
            byte foregroundGreen,
            byte foregroundBlue)
        {
            const byte FillAlpha = 245;
            const byte BorderAlpha = 41;
            const byte BorderRed = 20;
            const byte BorderGreen = 23;
            const byte BorderBlue = 31;
            return new BubbleTheme(
                Color.FromArgb(FillAlpha, backgroundRed, backgroundGreen, backgroundBlue),
                Color.FromArgb(255, foregroundRed, foregroundGreen, foregroundBlue),
                Color.FromArgb(BorderAlpha, BorderRed, BorderGreen, BorderBlue),
                new BubblePalette(
                    backgroundRed, backgroundGreen, backgroundBlue, FillAlpha,
                    BorderRed, BorderGreen, BorderBlue, BorderAlpha));
        }
    }

    private static int CompareBubbles(ActiveBubble left, ActiveBubble right)
    {
        int dateComparison = left.ExpiresAt.CompareTo(right.ExpiresAt);
        return dateComparison != 0
            ? dateComparison
            : StringComparer.Ordinal.Compare(
                left.MessageId.ToString("D"),
                right.MessageId.ToString("D"));
    }

    private sealed record CacheEntry(VisualKey Key, PixelMemberVisuals Visuals);
}
