using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>
/// A bounded, thread-safe cache of text layout results. The key holds every value that
/// changes glyph placement: the text, the captured font metrics page and its revision,
/// em and atlas size, font size, wrap, spacing, line spacing, alignment, margins, DPI scale,
/// first-bearing trim, the shaped run (face content identity, language, direction,
/// features, paragraph analysis identity, and glyphs), and the available size. Text and layout revisions are not part
/// of the key. A hit with another text revision returns a result that shares the cached
/// glyph and caret storage and carries the requested revisions.
/// </summary>
/// <remarks>
/// The cache is direct-mapped with a fixed slot count, so it holds at most that many
/// results and their inputs. A lookup does not allocate. A miss stores one small entry.
/// Font metrics pages are compared by reference, because a font keeps one page for each
/// metrics revision. Equal metrics in two pages give a miss, never a wrong layout.
/// </remarks>
internal static class UITextLayoutCache
{
    private const int SlotCount = 512;
    private static readonly Entry?[] Slots = new Entry?[SlotCount];
    private static readonly Lock SlotLock = new();

    private sealed class Entry(UITextLayoutInput input, Vector2 availableSize, int hash, UITextLayoutResult result)
    {
        public UITextLayoutInput Input { get; } = input;
        public Vector2 AvailableSize { get; } = availableSize;
        public int Hash { get; } = hash;
        public UITextLayoutResult Result { get; } = result;
    }

    /// <summary>Finds a cached layout for equal glyph-placement inputs.</summary>
    internal static bool TryGet(UITextLayoutInput input, Vector2 availableSize,
        out int hash, out UITextLayoutResult result)
    {
        hash = ComputeHash(input, availableSize);
        Entry? entry;
        lock (SlotLock)
            entry = Slots[hash & (SlotCount - 1)];
        if (entry is null || entry.Hash != hash || entry.AvailableSize != availableSize ||
            !HasSamePlacementInputs(entry.Input, input))
        {
            UIRuntimeCounters.Add(UIWorkCounter.TextLayoutCacheMisses);
            result = null!;
            return false;
        }
        UIRuntimeCounters.Add(UIWorkCounter.TextLayoutCacheHits);
        UITextLayoutResult cached = entry.Result;
        result = cached.TextRevision == input.TextRevision && cached.FontRevision == input.FontRevision
            ? cached
            : cached.WithRevisions(input.TextRevision, input.FontRevision);
        return true;
    }

    /// <summary>Stores a layout for the hash that <see cref="TryGet"/> returned.</summary>
    internal static void Store(UITextLayoutInput input, Vector2 availableSize, int hash, UITextLayoutResult result)
    {
        Entry entry = new(input, availableSize, hash, result);
        lock (SlotLock)
            Slots[hash & (SlotCount - 1)] = entry;
    }

    private static int ComputeHash(UITextLayoutInput input, Vector2 availableSize)
    {
        HashCode hash = new();
        hash.Add(input.Text, StringComparer.Ordinal);
        hash.Add(RuntimeHelpers.GetHashCode(input.Runes));
        hash.Add(input.FontRevision);
        hash.Add(input.EmSize);
        hash.Add(input.AtlasSize);
        hash.Add(input.FontSize);
        hash.Add(input.Wrap);
        hash.Add(input.Spacing);
        hash.Add(input.LineSpacing);
        hash.Add(input.HorizontalAlignment);
        hash.Add(input.VerticalAlignment);
        hash.Add(input.Margins);
        hash.Add(input.DpiScale);
        hash.Add(input.TrimFirstBearing);
        if (input.ShapedRun is { } run)
        {
            hash.Add(run.SourceText, StringComparer.Ordinal);
            hash.Add(run.FaceContentIdentity, StringComparer.Ordinal);
            hash.Add(run.Language, StringComparer.Ordinal);
            hash.Add(run.Direction);
            hash.Add(run.HasMeasuredHardLines);
            hash.Add(run.ParagraphAnalysis is null ? 0 : RuntimeHelpers.GetHashCode(run.ParagraphAnalysis));
            hash.Add(run.Glyphs.Length);
        }
        hash.Add(availableSize);
        return hash.ToHashCode();
    }

    private static bool HasSamePlacementInputs(UITextLayoutInput left, UITextLayoutInput right)
        => ReferenceEquals(left, right) ||
            string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
            ReferenceEquals(left.Runes, right.Runes) &&
            left.FontRevision == right.FontRevision &&
            left.EmSize == right.EmSize &&
            left.AtlasSize == right.AtlasSize &&
            left.FontSize == right.FontSize &&
            left.Wrap == right.Wrap &&
            left.Spacing == right.Spacing &&
            left.LineSpacing == right.LineSpacing &&
            left.HorizontalAlignment == right.HorizontalAlignment &&
            left.VerticalAlignment == right.VerticalAlignment &&
            left.Margins == right.Margins &&
            left.DpiScale == right.DpiScale &&
            left.TrimFirstBearing == right.TrimFirstBearing &&
            HasSameShapedRun(left.ShapedRun, right.ShapedRun);

    private static bool HasSameShapedRun(UITextShapedRunInput? left, UITextShapedRunInput? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null ||
            !string.Equals(left.SourceText, right.SourceText, StringComparison.Ordinal) ||
            !string.Equals(left.FaceContentIdentity, right.FaceContentIdentity, StringComparison.Ordinal) ||
            !string.Equals(left.Language, right.Language, StringComparison.Ordinal) ||
            left.Direction != right.Direction ||
            left.HasMeasuredHardLines != right.HasMeasuredHardLines ||
            !ReferenceEquals(left.ParagraphAnalysis, right.ParagraphAnalysis) ||
            !string.Equals(left.FeatureSource, right.FeatureSource, StringComparison.Ordinal) ||
            left.Features.Length != right.Features.Length ||
            left.Glyphs.Length != right.Glyphs.Length)
            return false;
        for (int i = 0; i < left.Features.Length; i++)
            if (!string.Equals(left.Features[i], right.Features[i], StringComparison.Ordinal))
                return false;
        return left.Glyphs.AsSpan().SequenceEqual(right.Glyphs.AsSpan());
    }
}
