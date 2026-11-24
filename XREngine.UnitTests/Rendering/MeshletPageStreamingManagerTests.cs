using System;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The streaming manager loads and evicts geometry pages from the GPU feedback. These tests use a
/// small page graph with one root page and check the dependency chains, the request bands, the
/// eviction rules (cooldown, foreground floor, band order, root pages, parent recency), the upload
/// budget, failed pages, and the touch bits.
/// </summary>
/// <remarks>
/// The test page graph (geometry-local pages, largest group error in brackets):
/// page 0 is the root page (no simplification limit), page 1 [4] depends on page 0, page 2 [1]
/// depends on page 1, and page 3 [2] depends on page 0.
/// </remarks>
[TestFixture]
public sealed class MeshletPageStreamingManagerTests
{
    private const float Threshold = 3.0f;
    private static readonly AdvancedGpuHandle Geometry = new(7u, 1u);

    [Test]
    public void RegisterGeometry_RecordsTheRootPageAsResident()
    {
        Fixture fixture = new(slotCount: 4);

        fixture.Page(0).State.ShouldBe(EMeshletPageResidencyState.Resident);
        for (int page = 1; page < 4; ++page)
            fixture.Page(page).State.ShouldBe(EMeshletPageResidencyState.NotResident);
        fixture.Manager.Telemetry.ResidentPages.ShouldBe(1);
        fixture.Pool.UsedSlotCount.ShouldBe(1);
        fixture.Page(1).MaxGroupError.ShouldBe(4.0f);
    }

    [Test]
    public void Update_LoadsTheWholeDependencyChainInOneUpdate()
    {
        Fixture fixture = new(slotCount: 4);

        fixture.Request(1000, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);

        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(2).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.NotResident);
        fixture.Page(0).ResidentChildCount.ShouldBe(1);
        fixture.Page(1).ResidentChildCount.ShouldBe(1);
        fixture.Manager.Telemetry.LastLoads.ShouldBe(2);
        fixture.Pool.GetSlot(fixture.Global(2)).ShouldNotBe(AdvancedGeometryPagePool.NotResident);
    }

    [Test]
    public void Update_GivesADependencyTheForegroundBandWhenItsErrorShows()
    {
        Fixture fixture = new(slotCount: 4);

        // Page 1 has error 4: at pixel scale 1 it projects above the threshold of 3.
        fixture.Request(1000, 2, EMeshletPageRequestBand.Prefetch, pixelScale: 1.0f);
        fixture.Update(1000);

        fixture.Page(1).ResidentBand.ShouldBe(EMeshletPageRequestBand.Foreground);
        fixture.Page(2).ResidentBand.ShouldBe(EMeshletPageRequestBand.Prefetch);
    }

    [Test]
    public void SubmitFeedback_MergesDuplicateRequestsAndIgnoresResidentPages()
    {
        Fixture fixture = new(slotCount: 4);
        MeshletPageRequest[] requests =
        [
            fixture.MakeRequest(3, EMeshletPageRequestBand.Occluded, 10),
            fixture.MakeRequest(3, EMeshletPageRequestBand.Foreground, 5),
            fixture.MakeRequest(0, EMeshletPageRequestBand.Foreground, 5),
        ];

        fixture.Manager.SubmitFeedback(requests, overflowCount: 2, [], 1000);

        fixture.Manager.Telemetry.LastRequestedPages.ShouldBe(1);
        fixture.Manager.Telemetry.LastRequestOverflows.ShouldBe(2);
        fixture.Page(3).RequestBand.ShouldBe(EMeshletPageRequestBand.Foreground);
        fixture.Page(3).RequestPriority.ShouldBe((ushort)10);
    }

    [Test]
    public void Update_MarksAPageWithoutAValidImageFailedAndStopsItsChain()
    {
        Fixture fixture = new(slotCount: 4, missingImagePage: 1);

        fixture.Request(1000, 2, EMeshletPageRequestBand.Foreground);
        fixture.Update(1000);

        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.Failed);
        fixture.Page(2).State.ShouldBe(EMeshletPageResidencyState.NotResident);
        fixture.Manager.Telemetry.FailedPages.ShouldBe(1);

        fixture.Request(1001, 2, EMeshletPageRequestBand.Foreground);
        fixture.Update(1001);
        fixture.Manager.Telemetry.FailedPages.ShouldBe(1);
        fixture.Manager.Telemetry.LastLoads.ShouldBe(0);
    }

    [Test]
    public void Update_EvictsAnOldLeafAndLoadsAfterTheSlotIsReclaimed()
    {
        Fixture fixture = new(slotCount: 3);
        fixture.Request(1000, 1, EMeshletPageRequestBand.Prefetch);
        fixture.Request(1000, 3, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);
        fixture.Pool.FreeSlotCount.ShouldBe(0);

        // Page 1 is a resident dependency of the request, so page 3 is the only candidate.
        fixture.Request(1200, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1200, publicationGeneration: 50u);

        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.NotResident);
        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(2).State.ShouldBe(EMeshletPageResidencyState.NotResident);
        fixture.Manager.Telemetry.LastEvictions.ShouldBe(1);
        fixture.Manager.Telemetry.LastLoadsWithoutSlot.ShouldBe(1);
        fixture.Pool.RetiringSlotCount.ShouldBe(1);

        fixture.Pool.ReclaimAcknowledged(50u);
        fixture.Request(1201, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1201);

        fixture.Page(2).State.ShouldBe(EMeshletPageResidencyState.Resident);
    }

    [Test]
    public void Update_PrefetchKeepsTheCooldownAndForegroundUsesTheFloor()
    {
        Fixture fixture = new(slotCount: 3);
        fixture.Request(1000, 1, EMeshletPageRequestBand.Prefetch);
        fixture.Request(1000, 3, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);

        fixture.Request(1050, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1050);
        fixture.Manager.Telemetry.LastEvictions.ShouldBe(0);

        fixture.Request(1051, 2, EMeshletPageRequestBand.Foreground);
        fixture.Update(1051);
        fixture.Manager.Telemetry.LastEvictions.ShouldBe(1);
        fixture.Manager.Telemetry.LastForegroundEvictions.ShouldBe(1);
        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.NotResident);
    }

    [Test]
    public void Update_NeverEvictsAPageOfAHigherBand()
    {
        Fixture fixture = new(slotCount: 3);
        fixture.Request(1000, 1, EMeshletPageRequestBand.Foreground);
        fixture.Request(1000, 3, EMeshletPageRequestBand.Foreground);
        fixture.Update(1000);

        fixture.Request(1500, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1500);

        fixture.Manager.Telemetry.LastEvictions.ShouldBe(0);
        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.Resident);
    }

    [Test]
    public void SubmitFeedback_TouchBitsKeepADrawnPageResident()
    {
        Fixture fixture = new(slotCount: 3);
        fixture.Request(1000, 1, EMeshletPageRequestBand.Prefetch);
        fixture.Request(1000, 3, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);

        int slot = fixture.Page(3).Slot;
        uint[] touchWords = new uint[1];
        touchWords[slot / 32] |= 1u << (slot % 32);
        fixture.Manager.SubmitFeedback([], 0, touchWords, 1190);
        fixture.Page(3).LastTouchFrame.ShouldBe(1190);

        fixture.Request(1200, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1200);

        fixture.Manager.Telemetry.LastEvictions.ShouldBe(0);
        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.Resident);
    }

    [Test]
    public void Evict_GivesTheParentTheChildsLastTouchFrame()
    {
        Fixture fixture = new(slotCount: 3);
        fixture.Request(1000, 2, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);

        int slot = fixture.Page(2).Slot;
        uint[] touchWords = [1u << slot];
        fixture.Manager.SubmitFeedback([], 0, touchWords, 1010);

        // Page 1 has a resident child, so page 2 is the only candidate.
        fixture.Request(1200, 3, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1200);

        fixture.Page(2).State.ShouldBe(EMeshletPageResidencyState.NotResident);
        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(1).ResidentChildCount.ShouldBe(0);
        fixture.Page(1).LastTouchFrame.ShouldBe(1010);
    }

    [Test]
    public void Update_NeverEvictsTheRootPage()
    {
        Fixture fixture = new(slotCount: 1);

        fixture.Request(5000, 1, EMeshletPageRequestBand.Foreground);
        fixture.Update(5000);

        fixture.Manager.Telemetry.LastEvictions.ShouldBe(0);
        fixture.Page(0).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.NotResident);
    }

    [Test]
    public void Update_StopsAtTheUploadBudget()
    {
        Fixture fixture = new(slotCount: 4, policy: new MeshletPageResidencyPolicy { UploadBytesPerUpdate = 100 });

        fixture.Request(1000, 1, EMeshletPageRequestBand.Foreground);
        fixture.Request(1000, 3, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);

        // The first load is always allowed. The second 64-byte image does not fit in the 36 bytes left.
        fixture.Manager.Telemetry.LastLoads.ShouldBe(1);
        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.NotResident);
    }

    [Test]
    public void UnregisterGeometry_ClearsTheRecordsAndTheSlotMap()
    {
        Fixture fixture = new(slotCount: 4);
        fixture.Request(1000, 3, EMeshletPageRequestBand.Prefetch);
        fixture.Update(1000);
        int slot = fixture.Page(3).Slot;

        fixture.Pool.RetireGeometry(Geometry, 9u);
        fixture.Manager.UnregisterGeometry(Geometry).ShouldBeTrue();

        fixture.Manager.Registry.IsRegistered(fixture.Global(3)).ShouldBeFalse();
        fixture.Manager.Registry.GetSlotPage(slot).ShouldBe(MeshletPageRegistry.NoPage);
        fixture.Manager.Telemetry.ResidentPages.ShouldBe(0);
        fixture.Manager.UnregisterGeometry(Geometry).ShouldBeFalse();
    }

    /// <summary>A pool, a manager, and one registered geometry with the test page graph.</summary>
    internal sealed class Fixture
    {
        private static readonly uint[][] PageDependencies = [[], [0u], [1u], [0u]];
        private static readonly float[] PageErrors = [float.MaxValue, 4.0f, 1.0f, 2.0f];

        private readonly AdvancedGeometryPagePool.PageRange _range;

        public Fixture(int slotCount, int missingImagePage = -1, MeshletPageResidencyPolicy? policy = null)
        {
            Pool = new AdvancedGeometryPagePool();
            Pool.EnsureConfigured(slotCount);
            Manager = new MeshletPageStreamingManager(Pool, policy);
            MeshletPageSet pages = CreatePages(missingImagePage);
            Pool.TryReserve(pages, pages.Table.RootPageCount, out _range).ShouldBeTrue();
            Pool.Bind(Geometry, _range);
            Manager.RegisterGeometry(Geometry, _range, pages, frame: 0);
        }

        public AdvancedGeometryPagePool Pool { get; }

        public MeshletPageStreamingManager Manager { get; }

        public uint Global(int localPage)
            => _range.FirstPage + (uint)localPage;

        public ref MeshletPageRegistry.Page Page(int localPage)
            => ref Manager.Registry.GetPage(Global(localPage));

        public MeshletPageRequest MakeRequest(int localPage, EMeshletPageRequestBand band, ushort priority = 100, float pixelScale = 0.5f)
            => new(Global(localPage), MeshletPageRequest.PackBandAndPriority(band, priority), pixelScale);

        public void Request(long frame, int localPage, EMeshletPageRequestBand band, float pixelScale = 0.5f)
            => Manager.SubmitFeedback([MakeRequest(localPage, band, 100, pixelScale)], 0, [], frame);

        public void Update(long frame, ulong publicationGeneration = 1u)
            => Manager.Update(frame, Threshold, publicationGeneration);

        private static MeshletPageSet CreatePages(int missingImagePage)
        {
            int pageCount = PageDependencies.Length;
            MeshletPageRecord[] records = new MeshletPageRecord[pageCount];
            MeshletLodGroupRecord[] groups = new MeshletLodGroupRecord[pageCount];
            uint[] dependencies = new uint[3];
            int dependencyCount = 0;
            byte[][] images = new byte[pageCount][];
            for (int page = 0; page < pageCount; ++page)
            {
                records[page] = new MeshletPageRecord((uint)page, 1u, (uint)dependencyCount, (uint)PageDependencies[page].Length);
                foreach (uint dependency in PageDependencies[page])
                    dependencies[dependencyCount++] = dependency;
                groups[page] = new MeshletLodGroupRecord(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), PageErrors[page], pageCount - 1 - page, (uint)page);
                images[page] = page == missingImagePage ? null! : new byte[64];
            }

            MeshletPageTable table = new()
            {
                Kind = EMeshletPageFlags.None,
                RootPageCount = 1,
                ClusterOrder = [0u, 1u, 2u, 3u],
                ClusterRecords = [new(0, -1), new(1, -1), new(2, -1), new(3, -1)],
                Groups = groups,
                Pages = records,
                Dependencies = dependencies,
            };
            return new MeshletPageSet(table, images, default);
        }
    }
}
