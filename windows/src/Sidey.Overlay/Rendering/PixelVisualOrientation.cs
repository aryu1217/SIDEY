using Sidey.Core.Domain;

namespace Sidey.Overlay.Rendering;

internal static class PixelVisualOrientation
{
    internal static PremultipliedVisual Apply(PremultipliedVisual source, OverlayEdge edge)
    {
        _ = edge;
        // Characters follow the selected edge, but names, messages, typing text,
        // and the doze label stay aligned to the physical screen for readability.
        return source;
    }
}
