using System.Threading;
using System.Numerics;
using MemoryPack;
using YamlDotNet.Serialization;
using XREngine.Rendering.UI;

namespace XREngine.Rendering;

public partial class FontGlyphSet
{
    [MemoryPackIgnore, YamlIgnore]
    private UIFontFaceSnapshot? _runtimeShapingFace;
    private long _runtimeShapingFaceRevision = -1;

    /// <summary>Install a face that uses glyph IDs from this font's current atlas.</summary>
    public void InstallRuntimeShapingFace(UIFontFaceSnapshot face)
    {
        ArgumentNullException.ThrowIfNull(face);
        SetField(ref _runtimeShapingFace, face);
        // A different shaping face changes glyph placement even if the bitmap
        // atlas stays the same. Advance the same dependency key as metrics.
        long revision = Interlocked.Increment(ref _layoutMetricsRevision);
        Interlocked.Increment(ref _globalLayoutMetricsGeneration);
        _cachedLayoutMetrics = null;
        _runtimeShapingFaceRevision = revision;
        LayoutMetricsChanged?.Invoke(this);
    }

    /// <summary>Get the face only while its atlas metrics still match.</summary>
    public bool TryGetRuntimeShapingFace(out UIFontFaceSnapshot face)
    {
        if (_runtimeShapingFace is { } current &&
            _runtimeShapingFaceRevision == Volatile.Read(ref _layoutMetricsRevision))
        {
            face = current;
            return true;
        }
        face = null!;
        return false;
    }

    private void InstallBitmapRuntimeShapingFace(FontBitmapAtlasResult result)
    {
        if (result.GlyphIds is not { } ids || result.CopyFaceBytes() is not { } faceBytes)
            return;
        Dictionary<uint, UIRuneMetrics> metrics = new(ids.Count);
        foreach ((string character, ushort id) in ids)
        {
            if (!result.Glyphs.TryGetValue(character, out Glyph? glyph))
                continue;
            Vector2 extent = glyph.EffectiveAtlasSize;
            metrics.TryAdd(id, new UIRuneMetrics(glyph.Bearing, glyph.Size, glyph.EffectiveAdvance,
                new(glyph.Position.X, glyph.Position.Y,
                    glyph.Position.X + extent.X, glyph.Position.Y + extent.Y)));
        }
        InstallRuntimeShapingFace(new UIFontFaceSnapshot(faceBytes, result.FaceIndex,
            LayoutEmSize, metrics));
    }
}
