using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UITextLayoutKernelTests
{
    private static readonly FrozenDictionary<int, UIRuneMetrics> Metrics =
        new Dictionary<int, UIRuneMetrics>
        {
            ['A'] = new(Vector2.Zero, new(8, 10), 9, new(0, 0, 8, 10)),
            ['B'] = new(Vector2.Zero, new(8, 10), 9, new(8, 0, 16, 10)),
            ['e'] = new(Vector2.Zero, new(8, 10), 9, new(16, 0, 24, 10)),
            [0x0301] = new(Vector2.Zero, new(2, 3), 0, new(24, 0, 26, 3)),
            [0x1F441] = new(Vector2.Zero, new(8, 10), 9, new(26, 0, 34, 10)),
        }.ToFrozenDictionary();

    private static UITextLayoutInput Input(string text,
        FontGlyphSet.EWrapMode wrap = FontGlyphSet.EWrapMode.None,
        EHorizontalAlignment horizontal = EHorizontalAlignment.Left,
        EVerticalAlignment vertical = EVerticalAlignment.Bottom)
        => new(text, Metrics, 4, 7, 10, new(64, 64), 10, wrap, 0, 2,
            horizontal, vertical, Vector4.Zero);

    [Test]
    public void LayoutMetricsReuseOwnedPageAndIgnoreUnassignedGlyphMutations()
    {
        var atlas = new XRTexture2D
        {
            Mipmaps = [new Mipmap2D(8u, 8u, XREngine.Data.Rendering.EPixelInternalFormat.Rgba8,
                XREngine.Data.Rendering.EPixelFormat.Rgba, XREngine.Data.Rendering.EPixelType.UnsignedByte,
                allocateData: false)],
        };
        var font = new FontGlyphSet
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph> { ["A"] = new(new Vector2(8), Vector2.Zero) },
            Atlas = atlas,
            LayoutEmSize = 8,
        };
        try
        {
            int notifications = 0;
            font.LayoutMetricsChanged += _ => notifications++;
            font.TryCaptureLayoutMetrics(out var first, out _, out _, out long firstRevision).ShouldBeTrue();
            font.TryCaptureLayoutMetrics(out var cached, out _, out _, out long cachedRevision).ShouldBeTrue();
            cached.ShouldBeSameAs(first);
            cachedRevision.ShouldBe(firstRevision);
            Dictionary<string, FontGlyphSet.Glyph> edited = font.Glyphs.ShouldNotBeNull();
            edited["A"].AdvanceX = 20;
            font.TryCaptureLayoutMetrics(out var stillOwned, out _, out _, out long unchangedRevision).ShouldBeTrue();
            stillOwned.ShouldBeSameAs(first);
            unchangedRevision.ShouldBe(firstRevision);
            font.Glyphs = edited;
            notifications.ShouldBe(1);
            font.TryCaptureLayoutMetrics(out var changed, out _, out _, out long changedRevision).ShouldBeTrue();
            changed.ShouldNotBeSameAs(first);
            changedRevision.ShouldNotBe(firstRevision);
            changed['A'].Advance.ShouldBe(20f);
            edited["A"].AdvanceX = 30;
            font.TryCaptureLayoutMetrics(out var stillCommitted, out _, out _, out long committedRevision).ShouldBeTrue();
            stillCommitted.ShouldBeSameAs(changed);
            committedRevision.ShouldBe(changedRevision);
            stillCommitted['A'].Advance.ShouldBe(20f);

            font.AtlasType = EFontAtlasType.Msdf;
            font.DistanceRange = 4;
            font.DistanceRangeMiddle = 0.75f;
            notifications.ShouldBe(4);
            font.TryCaptureLayoutMetrics(out var afterPaintChange, out _, out _, out long paintRevision).ShouldBeTrue();
            afterPaintChange.ShouldBeSameAs(stillCommitted);
            paintRevision.ShouldBe(committedRevision);
            font.DistanceRangeMiddle = 0.75f;
            notifications.ShouldBe(4);
        }
        finally
        {
            font.Destroy(true);
            atlas.Destroy(true);
        }
    }

    [Test]
    public void Alignment_DoesNotChangeIntrinsicMeasurement()
    {
        UITextLayoutResult left = UITextLayoutKernel.Measure(Input("AB"), new(100, 50));
        UITextLayoutResult aligned = UITextLayoutKernel.Measure(
            Input("AB", horizontal: EHorizontalAlignment.Right, vertical: EVerticalAlignment.Top), new(100, 50));

        aligned.IntrinsicSize.ShouldBe(left.IntrinsicSize);
        aligned.Glyphs.Span[0].Transform.X.ShouldBeGreaterThan(left.Glyphs.Span[0].Transform.X);
        aligned.Glyphs.Span[0].Transform.Y.ShouldBeGreaterThan(left.Glyphs.Span[0].Transform.Y);
        aligned.TextRevision.ShouldBe(4);
        aligned.FontRevision.ShouldBe(7);
    }

    [Test]
    public void CharacterWrap_AssignsDistinctLinesAndKeepsIntrinsicWidth()
    {
        UITextLayoutResult result = UITextLayoutKernel.Measure(
            Input("AB", FontGlyphSet.EWrapMode.Character), new(10, 100));

        result.Glyphs.Length.ShouldBe(2);
        result.Glyphs.Span[0].Line.ShouldBe(0);
        result.Glyphs.Span[1].Line.ShouldBe(1);
        result.IntrinsicSize.X.ShouldBe(9);
        result.IntrinsicSize.Y.ShouldBeGreaterThan(10);
    }

    [Test]
    public void WordWrap_MovesWholeWordToNextLine()
    {
        UITextLayoutResult result = UITextLayoutKernel.Measure(
            Input("A BB", FontGlyphSet.EWrapMode.Word), new(28, 100));

        result.Glyphs.Length.ShouldBe(3);
        result.Glyphs.Span[0].Line.ShouldBe(0);
        result.Glyphs.Span[1].Line.ShouldBe(1);
        result.Glyphs.Span[2].Line.ShouldBe(1);
    }

    [Test]
    public void Glyphs_MapBackToUtf16GraphemeClusters()
    {
        UITextLayoutResult result = UITextLayoutKernel.Measure(Input("e\u0301\U0001F441"), new(100, 50));

        result.Glyphs.Length.ShouldBe(3);
        result.Glyphs.Span[0].ClusterStart.ShouldBe(0);
        result.Glyphs.Span[0].ClusterLength.ShouldBe(2);
        result.Glyphs.Span[1].ClusterStart.ShouldBe(0);
        result.Glyphs.Span[1].ClusterLength.ShouldBe(2);
        result.Glyphs.Span[2].ClusterStart.ShouldBe(2);
        result.Glyphs.Span[2].ClusterLength.ShouldBe(2);
    }

    [Test]
    public void CharacterWrap_KeepsCombiningClusterOnOneLine()
    {
        UITextLayoutResult result = UITextLayoutKernel.Measure(
            Input("Ae\u0301", FontGlyphSet.EWrapMode.Character), new(18, 100));

        result.Glyphs.Span[0].Line.ShouldBe(0);
        result.Glyphs.Span[1].Line.ShouldBe(1);
        result.Glyphs.Span[2].Line.ShouldBe(1);
        foreach (UITextCaretStop caret in result.Carets.Span)
            caret.Utf16Offset.ShouldNotBe(2);
    }

    [Test]
    public void CapturedMetrics_DetermineQuadAndAtlasCoordinates()
    {
        UITextLayoutResult result = UITextLayoutKernel.Measure(Input("A"), new(100, 50));
        UITextLayoutGlyph glyph = result.Glyphs.Span[0];

        glyph.Transform.ShouldBe(new Vector4(0, 0, 8, -10));
        glyph.Uvs.ShouldBe(new Vector4(0, 0, 0.125f, 0.15625f));
        result.IntrinsicSize.ShouldBe(new Vector2(9, 10));
    }

    [Test]
    public void CanvasDpiScaleRescalesGlyphsAndWrapConstraints()
    {
        UITextLayoutResult normal = UITextLayoutKernel.Measure(
            Input("AB", FontGlyphSet.EWrapMode.Character), new(18, 100));
        UITextLayoutResult scaled = UITextLayoutKernel.Measure(
            Input("AB", FontGlyphSet.EWrapMode.Character) with { DpiScale = 2 }, new(18, 100));

        normal.Glyphs.Span[1].Line.ShouldBe(0);
        scaled.Glyphs.Span[1].Line.ShouldBe(1);
        scaled.IntrinsicSize.X.ShouldBe(18f);
        scaled.Glyphs.Span[0].Transform.Z.ShouldBe(16f);
    }

    [Test]
    public void CaretStops_IncludeWhitespaceLineBreaksAndMissingGlyphs()
    {
        UITextLayoutResult result = UITextLayoutKernel.Measure(Input("A ?\nB"), new(100, 100));

        result.Carets.Length.ShouldBe(6);
        result.Carets.Span[0].Utf16Offset.ShouldBe(0);
        result.Carets.Span[1].Utf16Offset.ShouldBe(1);
        result.Carets.Span[2].Utf16Offset.ShouldBe(2);
        result.Carets.Span[3].Utf16Offset.ShouldBe(3);
        result.Carets.Span[4].Utf16Offset.ShouldBe(4);
        result.Carets.Span[4].Line.ShouldBe(1);
        result.Carets.Span[5].Utf16Offset.ShouldBe(5);
        result.TextRevision.ShouldBe(4);
    }

    [Test]
    public void ShapedGlyphIdsAndVisualCaretOrderReachLayoutResult()
    {
        UIRuneMetrics a = Metrics['A'];
        UIRuneMetrics b = Metrics['B'];
        UIFontFaceSnapshot face = new([1], 0, 10,
            new Dictionary<uint, UIRuneMetrics> { [51] = a, [52] = b });
        UITextShapingRequest request = new("AB", face, "en-US", UITextDirection.RightToLeft);
        UITextShapingResult shaped = new(request,
        [
            new(52, b, new Vector2(9, 0), Vector2.Zero, 1, 1),
            new(51, a, new Vector2(9, 0), Vector2.Zero, 0, 1)
        ]);

        UITextLayoutResult result = UITextLayoutKernel.Measure(
            Input("AB") with { ShapedRun = new UITextShapedRunInput(shaped) }, new(100, 50));

        result.Glyphs.Span[0].GlyphId.ShouldBe(52u);
        result.Glyphs.Span[1].GlyphId.ShouldBe(51u);
        result.Glyphs.Span[1].Transform.X.ShouldBe(9f);
        result.HasVisualCarets.ShouldBeTrue();
        result.UsesShapedGlyphs.ShouldBeTrue();
        result.HasCompleteGraphemeCaretStops.ShouldBeTrue();
        result.Carets.Span[0].Utf16Offset.ShouldBe(2);
        result.Carets.Span[^1].Utf16Offset.ShouldBe(0);
        for (int i = 0; i < result.Carets.Length; i++)
            result.Carets.Span[i].VisualIndex.ShouldBe(i);
    }

    [Test]
    public void EqualPlacementInputsReuseLayoutStorageWithTheNewRevision()
    {
        UITextLayoutInput first = Input("cache ABBA reuse", FontGlyphSet.EWrapMode.Word);
        UITextLayoutInput second = first with { TextRevision = first.TextRevision + 40 };

        UITextLayoutResult a = UITextLayoutKernel.Measure(first, new(60, 200));
        UITextLayoutResult b = UITextLayoutKernel.Measure(second, new(60, 200));

        b.Glyphs.Equals(a.Glyphs).ShouldBeTrue();
        b.Carets.Equals(a.Carets).ShouldBeTrue();
        b.TextRevision.ShouldBe(second.TextRevision);
        a.TextRevision.ShouldBe(first.TextRevision);
        UITextLayoutKernel.Measure(first, new(60, 200)).ShouldBeSameAs(a);
    }

    [Test]
    public void EachGlyphPlacementInputSelectsItsOwnLayout()
    {
        UITextLayoutInput baseline = Input("cache AB e variants", FontGlyphSet.EWrapMode.Word);
        Vector2 size = new(60, 200);
        UITextLayoutResult reference = UITextLayoutKernel.Measure(baseline, size);
        FrozenDictionary<int, UIRuneMetrics> copiedMetrics =
            new Dictionary<int, UIRuneMetrics>(Metrics).ToFrozenDictionary();
        UITextLayoutInput[] variants =
        [
            baseline with { Text = "cache AB e variantz" },
            baseline with { Runes = copiedMetrics },
            baseline with { FontRevision = baseline.FontRevision + 1 },
            baseline with { EmSize = 11 },
            baseline with { AtlasSize = new(128, 64) },
            baseline with { FontSize = 12 },
            baseline with { Wrap = FontGlyphSet.EWrapMode.Character },
            baseline with { Spacing = 1 },
            baseline with { LineSpacing = 3 },
            baseline with { HorizontalAlignment = EHorizontalAlignment.Right },
            baseline with { VerticalAlignment = EVerticalAlignment.Top },
            baseline with { Margins = new(1, 0, 0, 0) },
            baseline with { DpiScale = 2 },
            baseline with { TrimFirstBearing = false },
        ];
        foreach (UITextLayoutInput variant in variants)
        {
            // Seed the baseline slot before each key check. A previous variant may evict it.
            UITextLayoutResult seeded = UITextLayoutKernel.Measure(baseline, size);
            UITextLayoutResult distinct = UITextLayoutKernel.Measure(variant, size);
            distinct.Glyphs.Equals(seeded.Glyphs).ShouldBeFalse();
            distinct.Carets.Equals(seeded.Carets).ShouldBeFalse();
        }
        UITextLayoutResult beforeSize = UITextLayoutKernel.Measure(baseline, size);
        UITextLayoutResult otherSize = UITextLayoutKernel.Measure(baseline, new(61, 200));
        otherSize.Glyphs.Equals(beforeSize.Glyphs).ShouldBeFalse();
        otherSize.Carets.Equals(beforeSize.Carets).ShouldBeFalse();
        UITextLayoutResult restored = UITextLayoutKernel.Measure(baseline, size);
        AssertEquivalentPlacement(restored, reference);
        UITextLayoutKernel.Measure(baseline, size).ShouldBeSameAs(restored);
    }

    [Test]
    public void EvictedLayoutRecomputesIdenticalPlacementAndCanBeReused()
    {
        int slots = (int)typeof(UITextLayoutCache).GetField("SlotCount",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Dictionary<int, UITextLayoutInput> inputsBySlot = [];
        UITextLayoutInput? first = null, second = null;
        Vector2 size = new(600);
        // One more distinct key than slots guarantees a collision for every hash seed.
        for (int i = 0; i <= slots; i++)
        {
            UITextLayoutInput input = Input("cache collision AB") with { FontSize = 16 + i };
            UITextLayoutCache.TryGet(input, size, out int hash, out _);
            int slot = hash & (slots - 1);
            if (inputsBySlot.TryGetValue(slot, out first))
            {
                second = input;
                break;
            }
            inputsBySlot.Add(slot, input);
        }
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        UITextLayoutResult original = UITextLayoutKernel.Measure(first, size);
        UITextLayoutResult collision = UITextLayoutKernel.Measure(second, size);
        collision.Glyphs.Equals(original.Glyphs).ShouldBeFalse();
        UITextLayoutResult restored = UITextLayoutKernel.Measure(first, size);
        restored.Glyphs.Equals(original.Glyphs).ShouldBeFalse();
        restored.Carets.Equals(original.Carets).ShouldBeFalse();
        AssertEquivalentPlacement(restored, original);
        UITextLayoutKernel.Measure(first, size).ShouldBeSameAs(restored);
    }

    private static void AssertEquivalentPlacement(UITextLayoutResult actual, UITextLayoutResult expected)
    {
        actual.Glyphs.Span.SequenceEqual(expected.Glyphs.Span).ShouldBeTrue();
        actual.Carets.Span.SequenceEqual(expected.Carets.Span).ShouldBeTrue();
        actual.IntrinsicSize.ShouldBe(expected.IntrinsicSize);
        actual.TextRevision.ShouldBe(expected.TextRevision);
        actual.FontRevision.ShouldBe(expected.FontRevision);
        actual.HasVisualCarets.ShouldBe(expected.HasVisualCarets);
        actual.UsesShapedGlyphs.ShouldBe(expected.UsesShapedGlyphs);
    }

    [Test]
    public void ShapedRunLocaleDirectionAndFeaturesSelectTheirOwnLayout()
    {
        UIRuneMetrics a = Metrics['A'];
        UIRuneMetrics b = Metrics['B'];
        UIFontFaceSnapshot face = new([7], 0, 10,
            new Dictionary<uint, UIRuneMetrics> { [51] = a, [52] = b });
        UITextLayoutInput Shaped(string language, UITextDirection direction, string[]? features)
        {
            UITextShapingRequest request = new("AB", face, language, direction, features);
            UITextShapingResult shaped = new(request,
            [
                new(51, a, new Vector2(9, 0), Vector2.Zero, 0, 1),
                new(52, b, new Vector2(9, 0), Vector2.Zero, 1, 1)
            ]);
            return Input("AB") with { ShapedRun = new UITextShapedRunInput(shaped) };
        }

        Vector2 size = new(100, 50);
        UITextLayoutResult reference = UITextLayoutKernel.Measure(Shaped("en-US", UITextDirection.LeftToRight, null), size);
        UITextLayoutKernel.Measure(Shaped("en-US", UITextDirection.LeftToRight, null), size)
            .Glyphs.Equals(reference.Glyphs).ShouldBeTrue();
        UITextLayoutKernel.Measure(Shaped("de-DE", UITextDirection.LeftToRight, null), size)
            .Glyphs.Equals(reference.Glyphs).ShouldBeFalse();
        UITextLayoutKernel.Measure(Shaped("en-US", UITextDirection.RightToLeft, null), size)
            .Glyphs.Equals(reference.Glyphs).ShouldBeFalse();
        UITextLayoutKernel.Measure(Shaped("en-US", UITextDirection.LeftToRight, ["liga"]), size)
            .Glyphs.Equals(reference.Glyphs).ShouldBeFalse();
    }

    [Test]
    public void LigatureWithoutInteriorCaretReportsIncompleteVisualCarets()
    {
        UIRuneMetrics metric = Metrics['A'];
        UIFontFaceSnapshot face = new([1], 0, 10,
            new Dictionary<uint, UIRuneMetrics> { [81] = metric });
        UITextShapingResult shaped = new(
            new UITextShapingRequest("AB", face, "en-US", UITextDirection.LeftToRight),
            [new(81, metric, new Vector2(18, 0), Vector2.Zero, 0, 2)]);

        UITextLayoutResult result = UITextLayoutKernel.Measure(
            Input("AB") with { ShapedRun = new UITextShapedRunInput(shaped) }, new(100, 50));

        result.Glyphs.Span[0].ClusterLength.ShouldBe(2);
        result.HasVisualCarets.ShouldBeFalse();
        result.UsesShapedGlyphs.ShouldBeTrue();
        result.HasCompleteGraphemeCaretStops.ShouldBeFalse();
        result.Carets.Span[0].Utf16Offset.ShouldBe(0);
        result.Carets.Span[^1].Utf16Offset.ShouldBe(2);

        UITextLayoutResult scaled = UITextLayoutKernel.Measure(
            Input("AB") with { ShapedRun = new UITextShapedRunInput(shaped), DpiScale = 2 },
            new(200, 100));
        scaled.UsesShapedGlyphs.ShouldBeTrue();
        scaled.HasCompleteGraphemeCaretStops.ShouldBeFalse();
    }

    [Test]
    public void ShapedIntrinsicWithNoBoundDoesNotFitToFloatMaxValue()
    {
        UIRuneMetrics metric = Metrics['A'];
        UIFontFaceSnapshot face = new([1], 0, 10,
            new Dictionary<uint, UIRuneMetrics> { [51] = metric });
        UITextShapingResult shaped = new(
            new UITextShapingRequest("A", face, "en-US", UITextDirection.LeftToRight),
            [new(51, metric, new Vector2(9, 0), Vector2.Zero, 0, 1)]);

        UITextLayoutResult result = UITextLayoutKernel.Measure(
            Input("A") with { FontSize = null, ShapedRun = new UITextShapedRunInput(shaped) },
            new(float.MaxValue, float.MaxValue));

        float.IsFinite(result.IntrinsicSize.X).ShouldBeTrue();
        result.IntrinsicSize.X.ShouldBe(0.9f, 0.00001f);
        result.IntrinsicSize.Y.ShouldBe(1f);
    }

    [TestCase("אב12", UITextDirection.RightToLeft)]
    [TestCase("12", UITextDirection.RightToLeft)]
    [TestCase("אב\u2067", UITextDirection.RightToLeft)]
    [TestCase("A\u200F", UITextDirection.LeftToRight)]
    public void UnsupportedBidiAnalysisFailsBeforeSingleRunShaping(string text, UITextDirection direction)
    {
        NotSupportedException error = Should.Throw<NotSupportedException>(() =>
            UITextComponent.EnsureOneDirection(text, direction));
        error.Message.ShouldContain("UI.BidiAnalysisUnavailable");
    }

    [Test]
    public void ShapingRequestRejectsOversizedFeatureSetAndGlyphExpansion()
    {
        UIRuneMetrics metric = Metrics['A'];
        UIFontFaceSnapshot face = new([1], 0, 10,
            new Dictionary<uint, UIRuneMetrics> { [51] = metric });
        string[] features = Enumerable.Repeat("liga=1", 33).ToArray();
        Should.Throw<NotSupportedException>(() => new UITextShapingRequest(
            "A", face, "en-US", UITextDirection.LeftToRight, features))
            .Message.ShouldContain("UI.ShapingFeaturesExceeded");
        Should.Throw<ArgumentException>(() => new UITextShapingRequest(
            "A", face, new string('x', 65), UITextDirection.LeftToRight));

        UITextShapingRequest request = new("A", face, "en-US", UITextDirection.LeftToRight);
        UITextShapedGlyph[] glyphs = new UITextShapedGlyph[5];
        Should.Throw<NotSupportedException>(() => new UITextShapingResult(request, glyphs))
            .Message.ShouldContain("UI.ShapedGlyphCountExceeded");
    }
}
