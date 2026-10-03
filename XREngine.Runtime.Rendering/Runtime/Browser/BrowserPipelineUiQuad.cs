using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Painter-ordered straight-alpha UI rectangle with pixel clipping and an optional bound texture or explicit atlas region.</summary>
public readonly record struct BrowserPipelineUiQuad(
    BrowserResourceHandle Texture, Vector4 Rectangle, Vector4 UV, Vector4 Tint, Vector4 Clip);
