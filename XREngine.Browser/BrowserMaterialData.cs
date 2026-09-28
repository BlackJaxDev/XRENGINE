using System.Numerics;

namespace XREngine.Browser;

/// <summary>Opaque unlit material with a linear RGBA tint and optional sRGB texture.</summary>
public sealed class BrowserMaterialData
{
    public BrowserMaterialData(Vector4 tint, BrowserTextureData? texture = null)
    {
        if (!float.IsFinite(tint.X) || !float.IsFinite(tint.Y) || !float.IsFinite(tint.Z) ||
            tint.X < 0 || tint.X > 1 || tint.Y < 0 || tint.Y > 1 || tint.Z < 0 || tint.Z > 1 ||
            tint.W != 1.0f)
            throw new ArgumentException("Unlit material tint must be finite, normalized and fully opaque.", nameof(tint));
        Tint = tint;
        Texture = texture;
    }

    public Vector4 Tint { get; }
    public BrowserTextureData? Texture { get; }
}
