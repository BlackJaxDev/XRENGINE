using System.Collections;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks that local edits capture only changed subtrees and stay equal to a full capture.</summary>
[TestFixture, NonParallelizable]
public sealed class UISparsePublicationTests
{
    private const int LargeRowCount = 600;
    private const int SmallRowCount = 10;
    private UITestShaderScope _shaderScope = null!;
    private bool _countersWereEnabled;

    [SetUp]
    public void SetUp()
    {
        _shaderScope = new UITestShaderScope();
        _countersWereEnabled = UIRuntimeCounters.Enabled;
        UIRuntimeCounters.Enabled = true;
    }

    [TearDown]
    public void TearDown()
    {
        UIRuntimeCounters.Enabled = _countersWereEnabled;
        _shaderScope.Dispose();
    }

    [Test]
    public void LocalColorEditVisitsOneNodeAndSharesUnchangedPages()
    {
        using Fixture fixture = new();
        UIPublication baseline = fixture.Capture();
        baseline.RenderRecordCount.ShouldBe(LargeRowCount + SmallRowCount);
        UIMaterialComponent edited = fixture.LargeRows[300];
        baseline.TryGetRenderRecordIndex(edited.RenderInfo2D.MembershipId, out int editedIndex)
            .ShouldBeTrue();
        int editedPage = editedIndex / UIRecordStore<UIRenderRecord>.PageSize;

        long visits = UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited);
        long sparse = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures);
        long full = UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures);
        edited.SetBatchSolidColor(ColorF4.Red);
        UIPublication patched = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited) - visits).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures) - sparse).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures) - full).ShouldBe(0);
        patched.TryGetRenderRecord(edited.RenderInfo2D.MembershipId, out UIRenderRecord record)
            .ShouldBeTrue();
        record.QuadColor.ShouldBe((Vector4)ColorF4.Red);
        baseline.TryGetRenderRecord(edited.RenderInfo2D.MembershipId, out UIRenderRecord old)
            .ShouldBeTrue();
        old.QuadColor.ShouldNotBe(record.QuadColor);
        for (int page = 0; page < patched.RenderRecordStore.PageCount; page++)
            ReferenceEquals(patched.RenderRecordStore.GetLeaf(page),
                baseline.RenderRecordStore.GetLeaf(page)).ShouldBe(page != editedPage);
        // A visual edit keeps every hit record, so input keeps the same hit generation.
        patched.HitRecordStore.ShouldBeSameAs(baseline.HitRecordStore);
        patched.HitGeneration.ShouldBe(baseline.HitGeneration);

        fixture.AssertEqualToFullCapture(patched);
    }

    [Test]
    public void MovedPanelRecapturesOnlyItsSubtree()
    {
        using Fixture fixture = new();
        UIPublication baseline = fixture.Capture();
        long visits = UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited);
        long sparse = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures);

        fixture.SmallPanel.Translation = new Vector2(500, 40);
        UIPublication moved = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures) - sparse).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited) - visits)
            .ShouldBe(SmallRowCount + 1);
        moved.HitGeneration.ShouldNotBe(baseline.HitGeneration);
        UIMaterialComponent row = fixture.SmallRows[0];
        moved.TryGetRenderRecord(row.RenderInfo2D.MembershipId, out UIRenderRecord after)
            .ShouldBeTrue();
        baseline.TryGetRenderRecord(row.RenderInfo2D.MembershipId, out UIRenderRecord before)
            .ShouldBeTrue();
        after.WorldMatrix.ShouldNotBe(before.WorldMatrix);
        // The old publication is immutable; its readers keep the old placement.
        baseline.TryGetRenderRecord(row.RenderInfo2D.MembershipId, out UIRenderRecord retained)
            .ShouldBeTrue();
        retained.ShouldBe(before);

        fixture.AssertEqualToFullCapture(moved);
    }

    [Test]
    public void HiddenRowSplicesRecordsWithoutWalkingCleanRows()
    {
        using Fixture fixture = new();
        UIPublication baseline = fixture.Capture();
        long splices = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices);
        long full = UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures);
        long visits = UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited);

        fixture.LargeRows[10].BoundableTransform.Visibility = EVisibility.Hidden;
        UIPublication hidden = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices) - splices).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures) - full).ShouldBe(0);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited) - visits).ShouldBe(1);
        hidden.RenderRecordCount.ShouldBe(baseline.RenderRecordCount - 1);
        hidden.HitRecordCount.ShouldBe(baseline.HitRecordCount - 1);
        fixture.AssertEqualToFullCapture(hidden);
    }

    [Test]
    public void CountChangingEditsCopyOnlyTouchedRecordPages()
    {
        using Fixture fixture = new(largeRows: 2000);
        UIPublication baseline = fixture.Capture();
        UIMaterialComponent row = fixture.LargeRows[700];
        long copied = UIRuntimeCounters.Read(UIWorkCounter.PublicationRecordCellsCopied);
        long visits = UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited);
        long splices = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices);

        row.BoundableTransform.Visibility = EVisibility.Hidden;
        UIPublication hidden = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices) - splices).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited) - visits).ShouldBe(1);
        // One hit page and one render page lose a record; every other page is shared.
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationRecordCellsCopied) - copied)
            .ShouldBeLessThan(2 * UIRecordStore<UIRenderRecord>.PageSize);
        SharedLeafCount(hidden.RenderRecordStore, baseline.RenderRecordStore)
            .ShouldBe(baseline.RenderRecordStore.PageCount - 1);
        SharedLeafCount(hidden.HitRecordStore, baseline.HitRecordStore)
            .ShouldBe(baseline.HitRecordStore.PageCount - 1);
        hidden.TryGetRenderRecord(row.RenderInfo2D.MembershipId, out _).ShouldBeFalse();
        UIMaterialComponent later = fixture.LargeRows[701];
        hidden.TryGetRenderRecordIndex(later.RenderInfo2D.MembershipId, out int laterIndex)
            .ShouldBeTrue();
        hidden.RenderRecordStore[laterIndex].MembershipId.ShouldBe(later.RenderInfo2D.MembershipId);
        fixture.AssertEqualToFullCapture(hidden);

        copied = UIRuntimeCounters.Read(UIWorkCounter.PublicationRecordCellsCopied);
        row.BoundableTransform.Visibility = EVisibility.Visible;
        UIPublication shown = fixture.Capture();
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationRecordCellsCopied) - copied)
            .ShouldBeLessThanOrEqualTo(2 * UIRecordStore<UIRenderRecord>.PageSize + 2);
        shown.RenderRecordCount.ShouldBe(baseline.RenderRecordCount);
        fixture.AssertEqualToFullCapture(shown);
    }

    [Test]
    public void RecapturedPanelKeepsSurvivingRecordsInPlace()
    {
        using Fixture fixture = new();
        UIPublication baseline = fixture.Capture();
        long splices = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices);
        long visits = UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited);

        // The panel move recaptures every row in it; one hidden row changes the counts.
        fixture.SmallRows[4].BoundableTransform.Visibility = EVisibility.Hidden;
        fixture.SmallPanel.Translation = new Vector2(500, 40);
        UIPublication edited = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices) - splices).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited) - visits)
            .ShouldBe(SmallRowCount + 1);
        edited.RenderRecordCount.ShouldBe(baseline.RenderRecordCount - 1);
        fixture.AssertEqualToFullCapture(edited);
    }

    private static int SharedLeafCount<T>(UIRecordStore<T> current, UIRecordStore<T> previous)
        where T : struct
    {
        HashSet<UIRecordStoreNode<T>> leaves = new(ReferenceEqualityComparer.Instance);
        for (int page = 0; page < previous.PageCount; page++)
            leaves.Add(previous.GetLeaf(page));
        int shared = 0;
        for (int page = 0; page < current.PageCount; page++)
            if (leaves.Contains(current.GetLeaf(page)))
                shared++;
        return shared;
    }

    [Test]
    public void SplicedCaptureIsBasisForLaterEdits()
    {
        using Fixture fixture = new();
        fixture.Capture();
        fixture.LargeRows[10].BoundableTransform.Visibility = EVisibility.Hidden;
        fixture.SmallRows[2].BoundableTransform.Visibility = EVisibility.Hidden;
        fixture.Capture();

        // Records after both spliced ranges moved. A later edit must patch the moved slots.
        long sparse = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures);
        fixture.LargeRows[400].SetBatchSolidColor(ColorF4.Green);
        fixture.SmallRows[5].SetBatchSolidColor(ColorF4.Red);
        UIPublication edited = fixture.Capture();
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures) - sparse).ShouldBe(1);
        fixture.AssertEqualToFullCapture(edited);

        fixture.LargeRows[10].BoundableTransform.Visibility = EVisibility.Visible;
        fixture.SmallRows[2].BoundableTransform.Visibility = EVisibility.Visible;
        long splices = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices);
        UIPublication shown = fixture.Capture();
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseSplices) - splices).ShouldBe(1);
        fixture.AssertEqualToFullCapture(shown);
    }

    [Test]
    public void EditsInsideHiddenSubtreeDoNotWalkIt()
    {
        using Fixture fixture = new();
        fixture.SmallPanel.Visibility = EVisibility.Hidden;
        UIPublication baseline = fixture.Capture();
        baseline.RenderRecordCount.ShouldBe(LargeRowCount);
        long visits = UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited);
        long sparse = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures);

        fixture.SmallRows[3].SetBatchSolidColor(ColorF4.Red);
        UIPublication published = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures) - sparse).ShouldBe(1);
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationCaptureNodesVisited) - visits).ShouldBe(0);
        published.RenderRecordStore.ShouldBeSameAs(baseline.RenderRecordStore);

        fixture.SmallPanel.Visibility = EVisibility.Visible;
        UIPublication shown = fixture.Capture();
        shown.RenderRecordCount.ShouldBe(LargeRowCount + SmallRowCount);
        shown.TryGetRenderRecord(fixture.SmallRows[3].RenderInfo2D.MembershipId,
            out UIRenderRecord record).ShouldBeTrue();
        record.QuadColor.ShouldBe((Vector4)ColorF4.Red);
        fixture.AssertEqualToFullCapture(shown);
    }

    [Test]
    public void RejectedCaptureClearsSparseBasis()
    {
        using Fixture fixture = new();
        UIPublicationReentryProbe probe = new SceneNode(fixture.SmallPanel.SceneNode!) { Name = "Probe" }
            .AddComponent<UIPublicationReentryProbe>()!;
        probe.BoundableTransform.Width = 10;
        probe.BoundableTransform.Height = 10;
        UIPublication baseline = fixture.Capture();
        SparseBasis(fixture.Canvas).ShouldBeSameAs(baseline);

        probe.BoundableTransform.RecordUIChange(UIChangeDomain.Visual);
        probe.OnCapture = () => Invoke(fixture.Canvas, "CapturePublicationCandidate");
        Invoke(fixture.Canvas, "CapturePublicationCandidate");
        Candidate(fixture.Canvas).ShouldBeSameAs(baseline);
        SparseBasis(fixture.Canvas).ShouldBeNull();

        probe.OnCapture = null;
        long full = UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures);
        UIPublication accepted = fixture.Capture();
        (UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures) - full).ShouldBe(1);
        SparseBasis(fixture.Canvas).ShouldBeSameAs(accepted);
    }

    [Test]
    public void ScrollTargetChangeRecapturesDependentScrollbar()
    {
        using Fixture fixture = new(largeRows: 40, smallRows: 2);
        SceneNode scrollNode = new(fixture.Transform.SceneNode!) { Name = "Scroll" };
        UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
        scroll.MinAnchor = Vector2.Zero;
        scroll.MaxAnchor = Vector2.Zero;
        scroll.Translation = new Vector2(0, 1100);
        scroll.Width = 100;
        scroll.Height = 80;
        scrollNode.AddComponent<UIScrollRegionComponent>();
        SceneNode contentNode = new(scrollNode) { Name = "Content" };
        UIBoundableTransform content = contentNode.SetTransform<UIBoundableTransform>();
        content.MinAnchor = Vector2.Zero;
        content.MaxAnchor = Vector2.Zero;
        content.Width = 240;
        content.Height = 80;
        SceneNode trackNode = new(fixture.Transform.SceneNode!) { Name = "Track" };
        UIBoundableTransform track = trackNode.SetTransform<UIBoundableTransform>();
        track.MinAnchor = Vector2.Zero;
        track.MaxAnchor = Vector2.Zero;
        track.Translation = new Vector2(200, 1100);
        track.Width = 100;
        track.Height = 10;
        UIScrollbarComponent scrollbar = trackNode.AddComponent<UIScrollbarComponent>()!;
        scrollbar.Horizontal = true;
        scrollbar.ScrollTarget = scroll;
        UIPublication baseline = fixture.Capture(() => ScrollbarGeometryReady(fixture, scrollbar));
        UIHitRecord before = FindHit(baseline, track.ElementIdentity);
        before.PointerGeometry.Scrollbar.ShouldNotBeNull();
        long sparse = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures);

        scroll.ScrollOffset = new Vector2(60, 0);
        UIPublication scrolled = fixture.Capture(() => ScrollbarGeometryReady(fixture, scrollbar));

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures) - sparse)
            .ShouldBeGreaterThan(0);
        UIHitRecord after = FindHit(scrolled, track.ElementIdentity);
        after.PointerGeometry.Scrollbar.ShouldNotBeNull();
        after.PointerGeometry.ShouldNotBe(before.PointerGeometry);
        fixture.AssertEqualToFullCapture(scrolled);
    }

    [Test]
    public void VirtualRowRebindMatchesCompleteCapture()
    {
        using Fixture fixture = new(largeRows: 40, smallRows: 2);
        SceneNode listNode = new(fixture.Transform.SceneNode!) { Name = "Virtual rows" };
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.MinAnchor = Vector2.Zero;
        list.MaxAnchor = Vector2.Zero;
        list.Translation = new Vector2(600, 600);
        list.Width = 300;
        list.Height = 60;
        list.ItemSize = 20;
        list.VirtualOverscan = 0;
        HitRows provider = new(listNode, 100);
        list.VirtualProvider = provider;
        list.SynchronizeVirtualRows(60);
        UIPublication baseline = fixture.Capture();
        list.TryGetRealizedVirtualRow(1, out _, out UIVirtualRowLease firstLease).ShouldBeTrue();
        long sparse = UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures);

        list.ContentScrollOffset = 80;
        list.SynchronizeVirtualRows(60);
        UIPublication scrolled = fixture.Capture();

        (UIRuntimeCounters.Read(UIWorkCounter.PublicationSparseCaptures) - sparse)
            .ShouldBeGreaterThan(0);
        list.TryGetRealizedVirtualRow(5, out _, out UIVirtualRowLease lease).ShouldBeTrue();
        lease.ShouldNotBe(firstLease);
        bool found = false;
        for (int index = 0; index < scrolled.HitRecordCount; index++)
            found |= scrolled.GetHitRecord(index).VirtualRowLease == lease;
        found.ShouldBeTrue();
        fixture.AssertEqualToFullCapture(scrolled);
    }

    private static bool ScrollbarGeometryReady(Fixture fixture, UIScrollbarComponent scrollbar)
    {
        // The thumb follows its target from a late tick, which headless tests do not run.
        typeof(UIScrollbarComponent).GetMethod("UpdateThumb",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scrollbar, null);
        UIPublication? candidate = Candidate(fixture.Canvas);
        if (candidate is null)
            return false;
        UIElementIdentity identity = scrollbar.BoundableTransform.ElementIdentity;
        for (int index = 0; index < candidate.HitRecordCount; index++)
            if (candidate.GetHitRecord(index) is { } record && record.Identity == identity)
                return record.PointerGeometry.Scrollbar is not null;
        return false;
    }

    private static UIHitRecord FindHit(UIPublication publication, UIElementIdentity identity)
    {
        for (int index = 0; index < publication.HitRecordCount; index++)
            if (publication.GetHitRecord(index).Identity == identity)
                return publication.GetHitRecord(index);
        throw new AssertionException("The expected hit record was not captured.");
    }

    [Test]
    public void BatchPageReplacementSharesUnchangedLeaves()
    {
        UIRecordStore<int> store = UIRecordStore<int>.Create(Enumerable.Range(0, 1000).ToArray());
        int[] first = store.GetPage(0).ToArray();
        int[] last = store.GetPage(3).ToArray();
        first[5] = -5;
        last[^1] = -999;
        UIRecordStore<int> patched = store.WithReplacedPages([0, 3], [first, last]);
        try
        {
            patched.Length.ShouldBe(1000);
            patched[5].ShouldBe(-5);
            patched[999].ShouldBe(-999);
            store[5].ShouldBe(5);
            ReferenceEquals(patched.GetLeaf(1), store.GetLeaf(1)).ShouldBeTrue();
            ReferenceEquals(patched.GetLeaf(2), store.GetLeaf(2)).ShouldBeTrue();
            ReferenceEquals(patched.GetLeaf(0), store.GetLeaf(0)).ShouldBeFalse();
            Should.Throw<ArgumentOutOfRangeException>(() =>
                store.WithReplacedPages([3, 0], [last, first]));
            Should.Throw<ArgumentException>(() =>
                store.WithReplacedPages([1], [new int[3]]));
        }
        finally
        {
            patched.Release();
            store.Release();
        }
    }

    [Test]
    public void BatchCollectorVisitsOnlyChangedRecordPages()
    {
        const int pageSize = UIRecordStore<UIRenderRecord>.PageSize;
        using Fixture fixture = new(largeRows: 2000);
        UIPublication baseline = fixture.Capture();
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = CreateCollection(baseline);
        CollectAndSwap(collector, commands, baseline);
        collector.LastCollectedPageCount.ShouldBe(baseline.RenderRecordStore.PageCount);
        collector.LastCollectedPageRecordCount.ShouldBe(baseline.RenderRecordCount);

        // A count-preserving edit replaces one page; the collector visits only that page.
        fixture.LargeRows[300].SetBatchSolidColor(ColorF4.Red);
        UIPublication patched = fixture.Capture();
        CollectAndSwap(collector, commands, patched);
        collector.LastCollectedPageCount.ShouldBe(1);
        collector.LastReusedPageCount.ShouldBe(patched.RenderRecordStore.PageCount - 1);
        collector.LastCollectedPageRecordCount.ShouldBeLessThanOrEqualTo(pageSize);
        ReadRenderRuns(collector).ShouldBe(CollectFresh(patched));

        // A count-changing edit splices one page. Later pages move but keep their runs.
        UIMaterialComponent row = fixture.LargeRows[700];
        collector.GetRetainedPrimitiveHandle(row.RenderInfo2D.MembershipId).ShouldNotBe(0ul);
        row.BoundableTransform.Visibility = EVisibility.Hidden;
        UIPublication hidden = fixture.Capture();
        CollectAndSwap(collector, commands, hidden);
        collector.LastCollectedPageCount.ShouldBe(1);
        collector.LastCollectedPageRecordCount.ShouldBeLessThanOrEqualTo(pageSize);
        collector.LastRunReconcileVisitCount.ShouldBeLessThanOrEqualTo(2 * pageSize);
        collector.GetRetainedPrimitiveHandle(row.RenderInfo2D.MembershipId).ShouldBe(0ul);
        collector.ExpectedPublishedDrawIntentCount.ShouldBe(CountRenderingCommands(commands, hidden));
        ReadRenderRuns(collector).ShouldBe(CollectFresh(hidden));
    }

    [Test]
    public void DiscardedCollectionKeepsCommittedRunsUntilSwap()
    {
        using Fixture fixture = new(largeRows: 600);
        UIPublication baseline = fixture.Capture();
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = CreateCollection(baseline);
        CollectAndSwap(collector, commands, baseline);
        string[] committed = ReadRenderRuns(collector);

        fixture.LargeRows[10].SetBatchSolidColor(ColorF4.Red);
        UIPublication patched = fixture.Capture();
        commands.DiscardUpdatingCommands();
        collector.CollectPublishedRuns(patched, commands, _ => { });
        commands.DiscardUpdatingCommands();
        collector.DiscardCollectedCandidate();
        // The render side still draws the committed runs after the discard.
        ReadRenderRuns(collector).ShouldBe(committed);

        // The latest collection is the basis, so the same edit reuses every page.
        CollectAndSwap(collector, commands, patched);
        collector.LastCollectedPageCount.ShouldBe(0);
        ReadRenderRuns(collector).ShouldBe(CollectFresh(patched));

        // The committed packet comes back: its changed page is collected again.
        CollectAndSwap(collector, commands, baseline);
        collector.LastCollectedPageCount.ShouldBe(1);
        ReadRenderRuns(collector).ShouldBe(committed);
    }

    private static RenderCommandCollection CreateCollection(UIPublication publication)
    {
        Dictionary<int, IComparer<RenderCommand>?> passes = [];
        for (int index = 0; index < publication.RenderRecordCount; index++)
            passes.TryAdd(publication.RenderRecordStore[index].RenderPass, new NearToFarRenderCommandSorter());
        return new RenderCommandCollection(passes);
    }

    private static void CollectAndSwap(UIBatchCollector collector, RenderCommandCollection commands,
        UIPublication publication)
    {
        commands.DiscardUpdatingCommands();
        collector.CollectPublishedRuns(publication, commands, _ => { });
        collector.SwapBuffers();
        commands.SwapBuffers();
    }

    private static int CountRenderingCommands(RenderCommandCollection commands, UIPublication publication)
    {
        HashSet<int> passes = [];
        for (int index = 0; index < publication.RenderRecordCount; index++)
            passes.Add(publication.RenderRecordStore[index].RenderPass);
        int count = 0;
        foreach (int pass in passes)
            count += commands.GetRenderingPassCommandCount(pass);
        return count;
    }

    private static string[] CollectFresh(UIPublication publication)
    {
        using UIBatchCollector fresh = new();
        fresh.CollectPublishedRuns(publication, CreateCollection(publication), _ => { });
        fresh.SwapBuffers();
        return ReadRenderRuns(fresh);
    }

    /// <summary>Describes every committed run in pass and painter order.</summary>
    private static string[] ReadRenderRuns(UIBatchCollector collector)
    {
        List<string> runs = [];
        foreach (string field in new[] { "_renderMaterialGroups", "_renderTextGroups" })
        {
            var pools = (IDictionary)typeof(UIBatchCollector)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(collector)!;
            foreach (int pass in pools.Keys.Cast<int>().Order())
            {
                object pool = pools[pass]!;
                int count = (int)pool.GetType().GetField("Count")!.GetValue(pool)!;
                var items = (IList)pool.GetType().GetField("Items")!.GetValue(pool)!;
                for (int index = 0; index < count; index++)
                {
                    var entries = (IList)items[index]!.GetType().GetField("Entries")!.GetValue(items[index])!;
                    string description = $"{field}:{pass}:{entries.Count}";
                    foreach (object entry in entries)
                        if (entry is UIBatchCollector.MaterialQuadEntry quad)
                            description += $"|{quad.Color}{quad.UIXYWH}{quad.WorldMatrix.Translation}";
                    runs.Add(description);
                }
            }
        }
        return [.. runs];
    }

    private static object? Invoke(UICanvasComponent canvas, string method)
        => typeof(UICanvasComponent).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);

    private static UIPublication? Candidate(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);

    private static UIPublication? SparseBasis(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_sparseBasis",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);

    /// <summary>A canvas with one large panel of painted rows and one small movable panel.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly SceneNode _root;
        private readonly List<UIPublication> _held = [];

        public Fixture(int largeRows = LargeRowCount, int smallRows = SmallRowCount)
        {
            Transform = new UICanvasTransform { Width = 1200, Height = 1200 };
            _root = new SceneNode("Sparse publication", Transform);
            _root.SetWorldContext(new TestWorldContext());
            Canvas = _root.AddComponent<UICanvasComponent>()!;
            LargePanel = AddPanel("Large panel", Vector2.Zero);
            SmallPanel = AddPanel("Small panel", new Vector2(600, 0));
            for (int index = 0; index < largeRows; index++)
                LargeRows.Add(AddRow(LargePanel, index));
            for (int index = 0; index < smallRows; index++)
                SmallRows.Add(AddRow(SmallPanel, index));
        }

        public UICanvasTransform Transform { get; }
        public UICanvasComponent Canvas { get; }
        public UIBoundableTransform LargePanel { get; }
        public UIBoundableTransform SmallPanel { get; }
        public List<UIMaterialComponent> LargeRows { get; } = [];
        public List<UIMaterialComponent> SmallRows { get; } = [];

        public UIPublication Capture(Func<bool>? ready = null)
        {
            Transform.UpdateLayoutAsync();
            UIPublication? publication = null;
            bool captured = SpinWait.SpinUntil(() =>
            {
                Transform.PumpLayoutResults();
                if (Transform.IsLayoutInvalidated && !Transform.IsUpdatingLayout)
                    Transform.UpdateLayoutAsync();
                if (Transform.CurrentLayoutGeneration == 0 || Transform.IsLayoutInvalidated ||
                    Transform.IsUpdatingLayout)
                    return false;
                Invoke(Canvas, "CapturePublicationCandidate");
                publication = Candidate(Canvas);
                return publication is not null &&
                    publication.LayoutGeneration == Transform.CurrentLayoutGeneration &&
                    publication.ChangeRevision == Transform.ChangeJournal.Revision &&
                    (ready?.Invoke() ?? true);
            }, TimeSpan.FromSeconds(10));
            if (!captured || publication is null)
                throw new AssertionException("No current publication was captured.");
            publication.Retain();
            _held.Add(publication);
            return publication;
        }

        /// <summary>Forces a complete capture of the same state and compares every record.</summary>
        public void AssertEqualToFullCapture(UIPublication sparse)
        {
            // A change on the canvas root covers every node, so the next capture is complete.
            long full = UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures);
            Transform.RecordUIChange(UIChangeDomain.Visual);
            UIPublication complete = Capture();
            complete.ShouldNotBeSameAs(sparse);
            (UIRuntimeCounters.Read(UIWorkCounter.PublicationFullCaptures) - full).ShouldBe(1);
            complete.HitRecordCount.ShouldBe(sparse.HitRecordCount);
            complete.RenderRecordCount.ShouldBe(sparse.RenderRecordCount);
            for (int index = 0; index < complete.HitRecordCount; index++)
                complete.GetHitRecord(index).ShouldBe(sparse.GetHitRecord(index));
            for (int index = 0; index < complete.RenderRecordCount; index++)
                complete.RenderRecordStore[index].ShouldBe(sparse.RenderRecordStore[index]);
            complete.ScrollReceivers.SequenceEqual(sparse.ScrollReceivers).ShouldBeTrue();
            complete.FocusScopes.SequenceEqual(sparse.FocusScopes).ShouldBeTrue();
            complete.ActiveFocusScopeIdentity.ShouldBe(sparse.ActiveFocusScopeIdentity);
        }

        public void Dispose()
        {
            foreach (UIPublication publication in _held)
                publication.Release();
            _root.Destroy(true);
        }

        private UIBoundableTransform AddPanel(string name, Vector2 translation)
        {
            SceneNode node = new(_root) { Name = name };
            UIBoundableTransform bounds = node.SetTransform<UIBoundableTransform>();
            bounds.Translation = translation;
            bounds.Width = 560;
            bounds.Height = 1200;
            return bounds;
        }

        private static UIMaterialComponent AddRow(UIBoundableTransform panel, int index)
        {
            SceneNode node = new(panel.SceneNode!) { Name = $"Row {index}" };
            UIBoundableTransform bounds = node.SetTransform<UIBoundableTransform>();
            bounds.Translation = new Vector2(index % 20 * 28, index / 20 * 28);
            bounds.Width = 24;
            bounds.Height = 24;
            bounds.BlocksInputBehind = true;
            UIMaterialComponent paint = node.AddComponent<UIMaterialComponent>()!;
            paint.SetBatchSolidColor(ColorF4.Gray);
            return paint;
        }
    }

    /// <summary>Virtual rows that block input, so each realized row has a hit record.</summary>
    private sealed class HitRows(SceneNode parent, int count) : IVirtualListProvider
    {
        public int Count => count;
        public ulong Revision => 1;
        public ulong GetItemId(int index) => (ulong)index + 1;

        public UIBoundableTransform CreateRow()
        {
            UIBoundableTransform bounds = new() { BlocksInputBehind = true };
            SceneNode node = new(parent, "Hit row", bounds);
            node.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(ColorF4.Gray);
            return bounds;
        }

        public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
        {
            row.Width = 300;
            row.Height = 20;
        }

        public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation) { }
    }
}
