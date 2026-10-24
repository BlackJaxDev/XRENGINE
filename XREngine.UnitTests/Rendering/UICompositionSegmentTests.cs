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
        try
        {
            second.Segments.Length.ShouldBe(3);
            second.Segments[0].ShouldBeSameAs(first.Segments[0]);
            second.Segments[2].ShouldBeSameAs(first.Segments[2]);
            second.Segments[1].Identity.ShouldBe(first.Segments[1].Identity);
            second.Segments[1].Revision.ShouldBeGreaterThan(first.Segments[1].Revision);
            second.Segments[0].Identity.ShouldNotBe(second.Segments[2].Identity);
        }
        finally { second.Release(); first.Release(); }
    }

    [Test]
    public void EarlierOverlayInsertionDoesNotRenumberUnchangedChrome()
    {
        UIRenderRecord chrome = Quad(1, Chrome);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build([chrome], null);
        UICompositionSegmentSet second = UICompositionSegmentSet.Build([Quad(2, Overlay), chrome], first);
        UICompositionSegmentSet third = UICompositionSegmentSet.Build([chrome], second);
        try
        {
            second.Segments[1].Identity.ShouldBe(first.Segments[0].Identity);
            second.Segments[1].ShouldNotBeSameAs(first.Segments[0]);
            third.Segments[0].Identity.ShouldBe(first.Segments[0].Identity);
            third.Segments[0].ShouldNotBeSameAs(second.Segments[1]);
        }
        finally { third.Release(); second.Release(); first.Release(); }
    }

    [Test]
    public void MovedSegmentUsesItsNewOffsetForLaterLocalUpdate()
    {
        UIRenderRecord chrome = Quad(1, Chrome);
        UIRenderRecord overlay = Quad(2, Overlay);
        UIRecordStore<UIRenderRecord> firstStore = UIRecordStore<UIRenderRecord>.Create([chrome]);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build(firstStore, null);
        UIRecordStore<UIRenderRecord> movedStore =
            UIRecordStore<UIRenderRecord>.Create([overlay, chrome]);
        UICompositionSegmentSet moved = UICompositionSegmentSet.Build(movedStore, first);
        UIRecordStore<UIRenderRecord>? editedStore = null;
        UICompositionSegmentSet? edited = null;
        try
        {
            UIRenderRecord[] changed =
            [overlay, chrome with { QuadColor = Vector4.Zero }];
            editedStore = movedStore.WithReplacedPage(0, changed);
            moved.TryWithReplacedRenderPage(1, 1, 0, editedStore,
                out UICompositionSegmentSet updated).ShouldBeTrue();
            edited = updated;
            edited.GetSegment(1).GetRecord(0).QuadColor.ShouldBe(Vector4.Zero);
            edited.GetSegment(0).ShouldBeSameAs(moved.GetSegment(0));
        }
        finally
        {
            try { edited?.Release(); }
            finally
            {
                try { editedStore?.Release(); }
                finally
                {
                    try { moved.Release(); }
                    finally
                    {
                        try { movedStore.Release(); }
                        finally
                        {
                            try { first.Release(); }
                            finally { firstStore.Release(); }
                        }
                    }
                }
            }
        }
    }

    [Test]
    public void ChangedClipAndOwnerReplaceTheAffectedSegment()
    {
        UIRenderRecord firstRecord = Quad(1, Chrome);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build([firstRecord], null);
        UICompositionSegmentSet clipped = UICompositionSegmentSet.Build(
            [firstRecord with { Crop = new BoundingRectangle(1, 2, 3, 4) }], first);
        UICompositionSegmentSet moved = UICompositionSegmentSet.Build(
            [firstRecord with { CompositionOwner = Overlay }], clipped);
        try
        {
            clipped.Segments[0].Revision.ShouldBeGreaterThan(first.Segments[0].Revision);
            moved.Segments[0].Identity.ShouldNotBe(clipped.Segments[0].Identity);
        }
        finally { moved.Release(); clipped.Release(); first.Release(); }
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
        try
        {
            first.Segments[0].Identity.ShouldBe(second.Segments[0].Identity);
            first.Segments[0].Revision.ShouldNotBe(second.Segments[0].Revision);
            first.Segments[0].Records[0].QuadColor.ShouldNotBe(second.Segments[0].Records[0].QuadColor);
        }
        finally { second.Release(); first.Release(); source.Release(); }
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

    [Test]
    public void EditedSegmentReleasesOnlyItsReplacedLeaf()
    {
        int[] claims = new int[4];
        UIRenderRecord[] records = new UIRenderRecord[768];
        UICompositionOwner[] owners =
        [Chrome, Overlay, new(new UIElementIdentity(300, 1), EUICompositionLayerKind.StaticChrome)];
        for (int index = 0; index < records.Length; index++)
            records[index] = Quad(index + 1, owners[index / 256]);
        int Category(UIRenderRecord record) => record.MembershipId is >= 257 and <= 512 &&
            record.QuadColor == Vector4.Zero ? 3 : (int)((record.MembershipId - 1) / 256);
        UIRecordStore<UIRenderRecord> firstStore = UIRecordStore<UIRenderRecord>.Create(records,
            record => claims[Category(record)]++, record => claims[Category(record)]--);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build(firstStore, null);
        UIRenderRecord[] edited = new UIRenderRecord[256];
        for (int index = 0; index < edited.Length; index++)
            edited[index] = records[256 + index] with { QuadColor = Vector4.Zero };
        UIRecordStore<UIRenderRecord> secondStore = firstStore.WithReplacedPage(1, edited);
        UICompositionSegmentSet second = UICompositionSegmentSet.Build(secondStore, first);

        second.Segments[0].ShouldBeSameAs(first.Segments[0]);
        second.Segments[2].ShouldBeSameAs(first.Segments[2]);
        second.Segments[1].ShouldNotBeSameAs(first.Segments[1]);
        firstStore.Release();
        first.Release();
        claims.ShouldBe(new[] { 256, 0, 256, 256 });
        secondStore.Release();
        second.Release();
        claims.ShouldBe(new[] { 0, 0, 0, 0 });
    }

    [Test]
    public void CompatibilityRecordsMaterializeOnceWhileSegmentBuildReadsPages()
    {
        UIRenderRecord[] records = [Quad(1, Chrome), Quad(2, Chrome)];
        UIRecordStore<UIRenderRecord> store = UIRecordStore<UIRenderRecord>.Create(records);
        UICompositionSegmentSet? first = null;
        UICompositionSegmentSet? second = null;
        bool countersEnabled = UIRuntimeCounters.Enabled;
        try
        {
            UIRuntimeCounters.Enabled = true;
            long before = UIRuntimeCounters.Read(UIWorkCounter.PublicationFlatCellsMaterialized);
            first = UICompositionSegmentSet.Build(store, null);
            second = UICompositionSegmentSet.Build(store, first);
            UIRuntimeCounters.Read(UIWorkCounter.PublicationFlatCellsMaterialized).ShouldBe(before);
            second.Segments[0].Records.Length.ShouldBe(2);
            second.Segments[0].Records.Length.ShouldBe(2);
            UIRuntimeCounters.Read(UIWorkCounter.PublicationFlatCellsMaterialized).ShouldBe(before + 2);
        }
        finally
        {
            UIRuntimeCounters.Enabled = countersEnabled;
            try { second?.Release(); }
            finally
            {
                try { first?.Release(); }
                finally { store.Release(); }
            }
        }
    }

    [Test]
    public void TouchedSegmentReplacesOnePagedSlotBranchWithoutFlattening()
    {
        UIRenderRecord[] records = new UIRenderRecord[300];
        for (int index = 0; index < records.Length; index++)
            records[index] = Quad(index + 1,
                new UICompositionOwner(new UIElementIdentity(index + 1000, 1),
                    EUICompositionLayerKind.StaticChrome));
        UIRecordStore<UIRenderRecord> firstStore = UIRecordStore<UIRenderRecord>.Create(records);
        UICompositionSegmentSet first = UICompositionSegmentSet.Build(firstStore, null);
        UIRecordStore<UIRenderRecord>? secondStore = null;
        UICompositionSegmentSet? second = null;
        bool countersEnabled = UIRuntimeCounters.Enabled;
        try
        {
            UIRenderRecord[] edited = firstStore.GetPage(1).ToArray();
            edited[280 - UIRecordStore<UIRenderRecord>.PageSize] =
                edited[280 - UIRecordStore<UIRenderRecord>.PageSize] with
                { QuadColor = Vector4.Zero };
            secondStore = firstStore.WithReplacedPage(1, edited);
            UIRuntimeCounters.Enabled = true;
            long flattened = UIRuntimeCounters.Read(UIWorkCounter.PublicationSegmentSlotsMaterialized);
            long copied = UIRuntimeCounters.Read(UIWorkCounter.PublicationSegmentSlotsCopied);
            first.TryWithReplacedRenderPage(280, 279, 1, secondStore,
                out _).ShouldBeFalse();
            UIRecordStore<UIRenderRecord> shortStore =
                UIRecordStore<UIRenderRecord>.Create(records.AsSpan(0, 299));
            try
            {
                first.TryWithReplacedRenderPage(280, 280, 1, shortStore,
                    out _).ShouldBeFalse();
            }
            finally { shortStore.Release(); }
            first.TryWithReplacedRenderPage(280, 280, 1, secondStore,
                out UICompositionSegmentSet updated).ShouldBeTrue();
            second = updated;
            second.GetSegment(0).ShouldBeSameAs(first.GetSegment(0));
            second.GetSegment(279).ShouldBeSameAs(first.GetSegment(279));
            second.GetSegment(280).ShouldNotBeSameAs(first.GetSegment(280));
            second.GetSegment(299).ShouldBeSameAs(first.GetSegment(299));
            UIRuntimeCounters.Read(UIWorkCounter.PublicationSegmentSlotsMaterialized)
                .ShouldBe(flattened);
            UIRuntimeCounters.Read(UIWorkCounter.PublicationSegmentSlotsCopied)
                .ShouldBeInRange(copied + 1, copied + UIRecordStore<UIRenderRecord>.PageSize);
        }
        finally
        {
            UIRuntimeCounters.Enabled = countersEnabled;
            try { second?.Release(); }
            finally
            {
                try { secondStore?.Release(); }
                finally
                {
                    try { first.Release(); }
                    finally { firstStore.Release(); }
                }
            }
        }
    }

    [Test]
    public void FailedMembershipBuildReleasesEveryStagedRange()
    {
        UIRenderRecord[] records = [Quad(1, Chrome), Quad(1, Overlay)];
        int claims = 0;
        UIRecordStore<UIRenderRecord> store = UIRecordStore<UIRenderRecord>.Create(records,
            _ => claims++, _ => claims--);
        Should.Throw<ArgumentException>(() => UICompositionSegmentSet.Build(store, null));
        store.Release();
        claims.ShouldBe(0);
    }

    [Test]
    public void CollectorKeepsExactCompositionSetAfterPacketOwnersRetire()
    {
        using UITestShaderScope shaders = new();
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        UIPublication source = new(1, 1, 1, 1, 1, default, default, [],
            [Quad(1, Chrome)], new BoundingRectangleF(0, 0, 100, 100),
            Matrix4x4.Identity, Matrix4x4.Identity);
        UIPublication mapped = source.WithOutputMapping(Matrix4x4.Identity,
            Matrix4x4.Identity, ECanvasDrawSpace.Screen, source.CanvasBounds,
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);
        UICompositionSegmentSet set = source.CompositionStorage;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?> { [pass] = null });
        collector.CollectPublishedRuns(mapped, commands, _ => Assert.Fail("Unexpected unbatched draw."));
        collector.SwapBuffers();
        mapped.Release();
        source.Release();

        set.ResourcesRetired.ShouldBeFalse();
        collector.Clear();
        set.ResourcesRetired.ShouldBeTrue();
    }

    private static UIRenderRecord Quad(long identity, UICompositionOwner owner) => new()
    {
        RenderInfo = null!, MembershipId = identity, Identity = new(identity, 1),
        CompositionOwner = owner, WorldMatrix = Matrix4x4.Identity,
        Bounds = new Vector4(0, 0, 8, 8), RenderPass = (int)EDefaultRenderPass.TransparentForward,
        ZIndex = 0, Material = null, Visible = true, SupportsBatch = true, QuadColor = Vector4.One,
    };
}
