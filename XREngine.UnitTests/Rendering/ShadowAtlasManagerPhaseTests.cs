using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShadowAtlasManagerPhaseTests
{
    [Test]
    public void ShadowRequestKey_IncludesProjectionFaceAndEncoding()
    {
        SpotLightComponent light = new();

        ShadowRequestKey spot = light.CreateShadowRequestKey(EShadowProjectionType.SpotPrimary, 0);
        ShadowRequestKey pointFace = light.CreateShadowRequestKey(EShadowProjectionType.PointFace, 0);
        ShadowRequestKey cascadeOne = light.CreateShadowRequestKey(EShadowProjectionType.DirectionalCascade, 1);
        ShadowRequestKey cascadeOneEvsm = light.CreateShadowRequestKey(
            EShadowProjectionType.DirectionalCascade,
            1,
            EShadowMapEncoding.ExponentialVariance4);

        pointFace.ShouldNotBe(spot);
        cascadeOne.ShouldNotBe(spot);
        cascadeOneEvsm.ShouldNotBe(cascadeOne);
        spot.LightId.ShouldBe(light.ID);
    }

    [Test]
    public void NormalizeTileResolution_RoundsClampsAndHonorsPageSize()
    {
        ShadowAtlasManager.NormalizeTileResolution(300u, 128u, 1024u, 4096u).ShouldBe(512u);
        ShadowAtlasManager.NormalizeTileResolution(16u, 128u, 1024u, 4096u).ShouldBe(128u);
        ShadowAtlasManager.NormalizeTileResolution(8192u, 128u, 4096u, 4096u).ShouldBe(4096u);
        ShadowAtlasManager.NormalizeTileResolution(4097u, 64u, 8192u, 4096u).ShouldBe(4096u);
    }

    [Test]
    public void SolveAllocations_IsDeterministicAndNonOverlappingAcrossFrames()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 1024u, maxPages: 1);
        SpotLightComponent[] lights =
        [
            CreateSpotLight(512u),
            CreateSpotLight(512u),
            CreateSpotLight(512u),
            CreateSpotLight(512u),
        ];
        ShadowMapRequest[] requests =
        [
            CreateRequest(lights[2], EShadowProjectionType.SpotPrimary, 0, 512u, 128u, 10.0f, 3u),
            CreateRequest(lights[0], EShadowProjectionType.SpotPrimary, 0, 512u, 128u, 10.0f, 1u),
            CreateRequest(lights[3], EShadowProjectionType.SpotPrimary, 0, 512u, 128u, 10.0f, 4u),
            CreateRequest(lights[1], EShadowProjectionType.SpotPrimary, 0, 512u, 128u, 10.0f, 2u),
        ];

        ShadowAtlasFrameData first = RunFrame(manager, 1u, requests);
        first.Metrics.ResidentTileCount.ShouldBe(4);
        first.Metrics.SkippedRequestCount.ShouldBe(0);
        first.Generation.ShouldBe(1u);
        AssertNoResidentOverlaps(first);
        Dictionary<ShadowRequestKey, AllocationSignature> firstLayout = CaptureLayout(first);

        ShadowAtlasFrameData second = RunFrame(manager, 2u, requests);
        second.Metrics.ResidentTileCount.ShouldBe(4);
        second.Generation.ShouldBe(1u);
        CaptureLayout(second).ShouldBe(firstLayout);
    }

    [Test]
    public void SolveAllocations_DemotesLowerPriorityRequestWhenFullSizeDoesNotFit()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        SpotLightComponent highPriorityLight = CreateSpotLight(256u);
        SpotLightComponent lowPriorityLight = CreateSpotLight(512u);
        ShadowMapRequest highPriority = CreateRequest(
            highPriorityLight,
            EShadowProjectionType.SpotPrimary,
            0,
            desiredResolution: 256u,
            minimumResolution: 128u,
            priority: 100.0f,
            contentHash: 1u);
        ShadowMapRequest lowPriority = CreateRequest(
            lowPriorityLight,
            EShadowProjectionType.SpotPrimary,
            0,
            desiredResolution: 512u,
            minimumResolution: 128u,
            priority: 10.0f,
            contentHash: 2u);

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, highPriority, lowPriority);

        frameData.TryGetAllocation(highPriority.Key, out ShadowAtlasAllocation highAllocation).ShouldBeTrue();
        frameData.TryGetAllocation(lowPriority.Key, out ShadowAtlasAllocation lowAllocation).ShouldBeTrue();
        highAllocation.Resolution.ShouldBe(256u);
        lowAllocation.Resolution.ShouldBe(256u);
        lowAllocation.IsResident.ShouldBeTrue();
        lowAllocation.SkipReason.ShouldBe(SkipReason.None);
    }

    [Test]
    public void Submit_WhenQueueIsFullPublishesQueueOverflowDiagnostic()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1, maxRequests: 1);
        ShadowMapRequest first = CreateRequest(CreateSpotLight(256u), EShadowProjectionType.SpotPrimary, 0, 256u, 128u, 2.0f, 1u);
        ShadowMapRequest second = CreateRequest(CreateSpotLight(256u), EShadowProjectionType.SpotPrimary, 0, 256u, 128u, 1.0f, 2u);

        manager.BeginFrame(1u, activeCameraCount: 1);
        manager.Submit(first).ShouldBeTrue();
        manager.Submit(second).ShouldBeFalse();
        manager.SolveAllocations();
        manager.PublishFrameData();

        ShadowAtlasFrameData frameData = manager.PublishedFrameData;
        frameData.Metrics.RequestCount.ShouldBe(1);
        frameData.Metrics.QueueOverflowCount.ShouldBe(1);
        frameData.Metrics.SkippedRequestCount.ShouldBe(1);
        frameData.TryGetAllocation(second.Key, out ShadowAtlasAllocation overflow).ShouldBeTrue();
        overflow.SkipReason.ShouldBe(SkipReason.QueueOverflow);
        overflow.ActiveFallback.ShouldBe(ShadowFallbackMode.StaleTile);
    }

    private static ShadowAtlasManager CreateManager(uint pageSize, int maxPages, int maxRequests = 32)
        => new(new ShadowAtlasManagerSettings(
            PageSize: pageSize,
            MaxPages: maxPages,
            MaxMemoryBytes: 0L,
            MaxTilesRenderedPerFrame: 16,
            MaxRenderMilliseconds: 2.0f,
            MinTileResolution: 128u,
            MaxTileResolution: pageSize,
            MaxRequestsPerFrame: maxRequests));

    private static SpotLightComponent CreateSpotLight(uint resolution)
    {
        SpotLightComponent light = new();
        light.SetShadowMapResolution(resolution, resolution);
        return light;
    }

    private static ShadowAtlasFrameData RunFrame(ShadowAtlasManager manager, ulong frameId, params ShadowMapRequest[] requests)
    {
        manager.BeginFrame(frameId, activeCameraCount: 1);
        for (int i = 0; i < requests.Length; i++)
            manager.Submit(requests[i]).ShouldBeTrue();

        manager.SolveAllocations();
        manager.RenderScheduledTiles();
        manager.PublishFrameData();
        return manager.PublishedFrameData;
    }

    private static ShadowMapRequest CreateRequest(
        LightComponent light,
        EShadowProjectionType projectionType,
        int faceOrCascadeIndex,
        uint desiredResolution,
        uint minimumResolution,
        float priority,
        ulong contentHash)
    {
        ShadowRequestKey key = light.CreateShadowRequestKey(projectionType, faceOrCascadeIndex);
        return new ShadowMapRequest(
            key,
            light,
            projectionType,
            EShadowMapEncoding.Depth,
            ShadowCasterFilterMode.Opaque,
            ShadowFallbackMode.StaleTile,
            faceOrCascadeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            0.1f,
            100.0f,
            desiredResolution,
            minimumResolution,
            priority,
            contentHash,
            IsDirty: true,
            CanReusePreviousFrame: true,
            EditorPinned: false,
            StereoVisibility.Mono);
    }

    private static Dictionary<ShadowRequestKey, AllocationSignature> CaptureLayout(ShadowAtlasFrameData frameData)
    {
        Dictionary<ShadowRequestKey, AllocationSignature> layout = new();
        for (int i = 0; i < frameData.AllocationCount; i++)
        {
            ShadowAtlasAllocation allocation = frameData.GetAllocation(i);
            if (!allocation.IsResident)
                continue;

            layout.Add(
                allocation.Key,
                new AllocationSignature(
                    allocation.PageIndex,
                    allocation.PixelRect.X,
                    allocation.PixelRect.Y,
                    allocation.Resolution));
        }

        return layout;
    }

    private static void AssertNoResidentOverlaps(ShadowAtlasFrameData frameData)
    {
        for (int i = 0; i < frameData.AllocationCount; i++)
        {
            ShadowAtlasAllocation a = frameData.GetAllocation(i);
            if (!a.IsResident)
                continue;

            for (int j = i + 1; j < frameData.AllocationCount; j++)
            {
                ShadowAtlasAllocation b = frameData.GetAllocation(j);
                if (!b.IsResident || a.AtlasId != b.AtlasId || a.PageIndex != b.PageIndex)
                    continue;

                RectanglesOverlap(a.PixelRect, b.PixelRect).ShouldBeFalse(
                    $"Expected {a.Key} {a.PixelRect} and {b.Key} {b.PixelRect} to occupy disjoint atlas texels.");
            }
        }
    }

    private static bool RectanglesOverlap(XREngine.Data.Geometry.BoundingRectangle a, XREngine.Data.Geometry.BoundingRectangle b)
        => a.MinX < b.MaxX &&
           a.MaxX > b.MinX &&
           a.MinY < b.MaxY &&
           a.MaxY > b.MinY;

    private readonly record struct AllocationSignature(int PageIndex, int X, int Y, uint Resolution);
}
