using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UICompositionSegmentTests
{
    private static readonly UICompositionOwner Chrome = new(new(100, 1), EUICompositionLayerKind.StaticChrome);
    private static readonly UICompositionOwner Overlay = new(new(200, 1), EUICompositionLayerKind.Overlay);

    [Test]
    public void OverlayPaintPreservesSeparateChromeSpansAndTheirRevisions()
    {
        UIRenderRecord left = Quad(1, Chrome), overlay = Quad(2, Overlay), right = Quad(3, Chrome);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build([left, overlay, right], null);
        UICompositionSegmentSet second = UICompositionSegmentSet.Build(
            [left, overlay with { QuadColor = Vector4.Zero }, right], first);

        second.Segments.Length.ShouldBe(3);
        second.Segments[0].ShouldBeSameAs(first.Segments[0]);
        second.Segments[2].ShouldBeSameAs(first.Segments[2]);
        second.Segments[1].Identity.ShouldBe(first.Segments[1].Identity);
        second.Segments[1].Revision.ShouldBeGreaterThan(first.Segments[1].Revision);
        second.Segments[0].Identity.ShouldNotBe(second.Segments[2].Identity);
    }

    [Test]
    public void EarlierOverlayInsertionDoesNotRenumberUnchangedChrome()
    {
        UIRenderRecord chrome = Quad(1, Chrome);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build([chrome], null);
        UICompositionSegmentSet second = UICompositionSegmentSet.Build([Quad(2, Overlay), chrome], first);
        second.Segments[1].ShouldBeSameAs(first.Segments[0]);
        UICompositionSegmentSet third = UICompositionSegmentSet.Build([chrome], second);
        third.Segments[0].ShouldBeSameAs(first.Segments[0]);
    }

    [Test]
    public void ChangedClipAndOwnerReplaceTheAffectedSegment()
    {
        UIRenderRecord firstRecord = Quad(1, Chrome);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build([firstRecord], null);
        UICompositionSegmentSet clipped = UICompositionSegmentSet.Build(
            [firstRecord with { Crop = new BoundingRectangle(1, 2, 3, 4) }], first);
        clipped.Segments[0].Revision.ShouldBeGreaterThan(first.Segments[0].Revision);
        UICompositionSegmentSet moved = UICompositionSegmentSet.Build(
            [firstRecord with { CompositionOwner = Overlay }], clipped);
        moved.Segments[0].Identity.ShouldNotBe(clipped.Segments[0].Identity);
    }

    [Test]
    public void OutputMappingSharesSegmentsAndVariantChangesOnlyItsEditedSpan()
    {
        UIRenderRecord chrome = Quad(1, Chrome), overlay = Quad(2, Overlay);
        BoundingRectangleF bounds = new(0, 0, 100, 100);
        UIPublication publication = new(1, 1, 1, 1, 1, default, default, [],
            [chrome, overlay], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        UIPublication mapped = publication.WithOutputMapping(Matrix4x4.CreateScale(2),
            Matrix4x4.CreateScale(0.5f), ECanvasDrawSpace.Screen, bounds,
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);
        UIPublication variant = publication.WithOutputVariant(
            [chrome, overlay with { QuadColor = Vector4.Zero }], 2,
            Matrix4x4.Identity, Matrix4x4.Identity, bounds);
        try
        {
            mapped.CompositionSegments[0].ShouldBeSameAs(publication.CompositionSegments[0]);
            mapped.CompositionSegments[1].ShouldBeSameAs(publication.CompositionSegments[1]);
            variant.CompositionSegments[0].ShouldBeSameAs(publication.CompositionSegments[0]);
            variant.CompositionSegments[1].Revision.ShouldBeGreaterThan(publication.CompositionSegments[1].Revision);
        }
        finally
        {
            variant.Release();
            mapped.Release();
            publication.Release();
        }
    }

    [Test]
    public void SiblingVariantsHaveDistinctChangedContentRevisions()
    {
        UIRenderRecord original = Quad(1, Overlay);
        UICompositionSegmentSet source = UICompositionSegmentSet.Build([original], null);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build([original with { QuadColor = Vector4.Zero }], source);
        UICompositionSegmentSet second = UICompositionSegmentSet.Build([original with { QuadColor = new Vector4(2) }], source);
        first.Segments[0].Identity.ShouldBe(second.Segments[0].Identity);
        first.Segments[0].Revision.ShouldNotBe(second.Segments[0].Revision);
        first.Segments[0].Records[0].QuadColor.ShouldNotBe(second.Segments[0].Records[0].QuadColor);
    }

    [Test]
    public void CanvasLifetimeChangeDoesNotReuseSegmentIdentity()
    {
        UIRenderRecord[] records = [Quad(1, Chrome)];
        BoundingRectangleF bounds = new(0, 0, 100, 100);
        UIPublication first = new(1, 1, 1, 1, 1, default, default, [], records,
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        UIPublication replacement = new(2, 2, 1, 1, 1, default, default, [], records,
            bounds, Matrix4x4.Identity, Matrix4x4.Identity, previous: first);
        try
        {
            replacement.CompositionSegments[0].Identity.ShouldNotBe(first.CompositionSegments[0].Identity);
        }
        finally
        {
            replacement.Release();
            first.Release();
        }
    }

    [Test]
    public void CanvasLifetimeChangeDoesNotReuseCollectedRunMetadata()
    {
        using UITestShaderScope shaders = new();
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?> { [pass] = null });
        UIRenderRecord[] records = [Quad(1, Chrome)];
        BoundingRectangleF bounds = new(0, 0, 100, 100);
        UIPublication first = new(1, 1, 1, 1, 1, default, default, [], records,
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        UIPublication replacement = new(2, 2, 1, 1, 1, default, default, [], records,
            bounds, Matrix4x4.Identity, Matrix4x4.Identity, previous: first);
        try
        {
            collector.CollectPublishedRuns(first, commands, _ => Assert.Fail("Unexpected unbatched draw."));
            collector.SwapBuffers();
            commands.DiscardUpdatingCommands();
            collector.CollectPublishedRuns(replacement, commands, _ => Assert.Fail("Unexpected unbatched draw."));
            collector.RetainedRunReuseCount.ShouldBe(0UL);
        }
        finally
        {
            replacement.Release();
            first.Release();
        }
    }

    [Test]
    public void LayerBoundarySplitsOtherwiseCompatibleMaterialRuns()
    {
        using UITestShaderScope shaders = new();
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?> { [pass] = null });
        UIPublication publication = new(1, 1, 1, 1, 1, default, default, [],
            [Quad(1, Chrome), Quad(2, Overlay), Quad(3, Chrome)],
            new BoundingRectangleF(0, 0, 100, 100), Matrix4x4.Identity, Matrix4x4.Identity);
        try
        {
            collector.CollectPublishedRuns(publication, commands, _ => Assert.Fail("Unexpected unbatched draw."));
            commands.GetUpdatingPassCommandCount(pass).ShouldBe(3);
        }
        finally { publication.Release(); }
    }

    private static UIRenderRecord Quad(long identity, UICompositionOwner owner) => new()
    {
        RenderInfo = null!, MembershipId = identity, Identity = new(identity, 1),
        CompositionOwner = owner, WorldMatrix = Matrix4x4.Identity,
        Bounds = new Vector4(0, 0, 8, 8), RenderPass = (int)EDefaultRenderPass.TransparentForward,
        ZIndex = 0, Material = null, Visible = true, SupportsBatch = true, QuadColor = Vector4.One,
    };
}
