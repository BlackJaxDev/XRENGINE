using System.Collections.Frozen;
using System.Numerics;
using System.Text;
using System.Threading;
using XREngine.Data.Vectors;
using XREngine.Rendering.UI;

namespace XREngine.Rendering;

public partial class FontGlyphSet
{
    private static void LayoutQuads(string? text, Dictionary<string, Glyph> glyphs,
        IVector2 atlasSize, float emSize, List<(Vector4 transform, Vector4 uvs)> quads,
        Vector2 offset, float? fontSize, float maxWidth, float maxHeight,
        EWrapMode wrap, float spacing, float lineSpacing, bool trimFirstBearing = true)
    {
        quads.Clear();
        if (string.IsNullOrEmpty(text) || atlasSize.X <= 0 || atlasSize.Y <= 0)
            return;

        Dictionary<int, UIRuneMetrics> captured = new(glyphs.Count);
        foreach ((string key, Glyph glyph) in glyphs)
        {
            if (glyph is null || !Rune.TryGetRuneAt(key, 0, out Rune rune) || rune.Utf16SequenceLength != key.Length)
                continue;
            Vector2 extent = glyph.EffectiveAtlasSize;
            captured[rune.Value] = new(glyph.Bearing, glyph.Size, glyph.EffectiveAdvance,
                new(glyph.Position.X, glyph.Position.Y,
                    glyph.Position.X + extent.X, glyph.Position.Y + extent.Y));
        }
        UITextLayoutInput input = new(text, captured.ToFrozenDictionary(), 0, 0,
            emSize, new(atlasSize.X, atlasSize.Y), fontSize, wrap, spacing, lineSpacing,
            EHorizontalAlignment.Left, EVerticalAlignment.Bottom, Vector4.Zero,
            TrimFirstBearing: trimFirstBearing);
        UITextLayoutResult result = UITextLayoutKernel.Measure(input, new(maxWidth, maxHeight));
        foreach (UITextLayoutGlyph glyph in result.Glyphs.Span)
        {
            Vector4 transform = glyph.Transform;
            transform.X += offset.X;
            transform.Y += offset.Y;
            quads.Add((transform, glyph.Uvs));
        }
    }

    private long _layoutMetricsRevision;
    internal long LayoutMetricsRevision => Volatile.Read(ref _layoutMetricsRevision);
    private static long _globalLayoutMetricsGeneration;
    internal static long GlobalLayoutMetricsGeneration => Volatile.Read(ref _globalLayoutMetricsGeneration);
    private long _cachedLayoutMetricsRevision = -1;
    private Vector2 _cachedLayoutAtlasSize;
    private float _cachedLayoutEmSize;
    private FrozenDictionary<int, UIRuneMetrics>? _cachedLayoutMetrics;

    /// <summary>Raised after an owned glyph table, atlas, or em-size change.</summary>
    public event Action<FontGlyphSet>? LayoutMetricsChanged;

    private void InvalidateLayoutMetrics()
    {
        Interlocked.Increment(ref _layoutMetricsRevision);
        Interlocked.Increment(ref _globalLayoutMetricsGeneration);
        _cachedLayoutMetrics = null;
        LayoutMetricsChanged?.Invoke(this);
    }

    /// <summary>Copy numeric glyph metrics for worker-safe text layout.</summary>
    public bool TryCaptureLayoutMetrics(out FrozenDictionary<int, UIRuneMetrics> metrics,
        out Vector2 atlasSize, out float emSize, out long revision)
    {
        // The owned glyph table cannot change without a metrics revision. Reuse its
        // validated numeric page before the resource check scans every glyph.
        XRTexture2D? currentAtlas = Atlas;
        if (_cachedLayoutMetrics is { } retained && _glyphs is not null &&
            currentAtlas is not null && currentAtlas.Mipmaps is { Length: > 0 } &&
            currentAtlas.Width > 0 && currentAtlas.Height > 0 &&
            _cachedLayoutMetricsRevision == Volatile.Read(ref _layoutMetricsRevision) &&
            _cachedLayoutAtlasSize == new Vector2(currentAtlas.Width, currentAtlas.Height) &&
            _cachedLayoutEmSize == LayoutEmSize)
        {
            metrics = retained;
            atlasSize = _cachedLayoutAtlasSize;
            emSize = _cachedLayoutEmSize;
            revision = _cachedLayoutMetricsRevision;
            return true;
        }

        if (!EnsureLayoutResourcesReady())
        {
            metrics = FrozenDictionary<int, UIRuneMetrics>.Empty;
            atlasSize = Vector2.Zero;
            emSize = LayoutEmSize;
            revision = 0;
            return false;
        }

        atlasSize = new(Atlas!.Width, Atlas.Height);
        emSize = LayoutEmSize;
        if (_cachedLayoutMetrics is { } cached &&
            _cachedLayoutMetricsRevision == Volatile.Read(ref _layoutMetricsRevision) &&
            _cachedLayoutAtlasSize == atlasSize && _cachedLayoutEmSize == emSize)
        {
            metrics = cached;
            revision = _cachedLayoutMetricsRevision;
            return true;
        }

        long capturedRevision = Volatile.Read(ref _layoutMetricsRevision);
        Dictionary<string, Glyph> glyphs = _glyphs!;
        Dictionary<int, UIRuneMetrics> captured = new(glyphs.Count);
        foreach ((string key, Glyph glyph) in glyphs)
        {
            if (glyph is null || !Rune.TryGetRuneAt(key, 0, out Rune rune) || rune.Utf16SequenceLength != key.Length)
                continue;
            Vector2 atlasExtent = glyph.EffectiveAtlasSize;
            UIRuneMetrics value = new(glyph.Bearing, glyph.Size, glyph.EffectiveAdvance,
                new(glyph.Position.X, glyph.Position.Y,
                    glyph.Position.X + atlasExtent.X, glyph.Position.Y + atlasExtent.Y));
            captured[rune.Value] = value;
        }
        if (capturedRevision != Volatile.Read(ref _layoutMetricsRevision))
            throw new InvalidOperationException("Font metrics changed during layout capture.");
        metrics = captured.ToFrozenDictionary();
        _cachedLayoutMetrics = metrics;
        _cachedLayoutMetricsRevision = capturedRevision;
        _cachedLayoutAtlasSize = atlasSize;
        _cachedLayoutEmSize = emSize;
        revision = _cachedLayoutMetricsRevision;
        return true;
    }
}
