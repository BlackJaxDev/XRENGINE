using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class PointShadowAtlasStabilityTests
{
    [Test]
    public void ContendedPointFaceSlotsStayStableAfterSettling()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        ShadowMapRequest[] requests = new ShadowMapRequest[6];
        for (int i = 0; i < requests.Length; i++)
        {
            PointLightComponent light = CreatePointLight(512u);
            requests[i] = CreatePointFaceRequest(
                light,
                faceIndex: 0,
                desiredResolution: 512u,
                minimumResolution: 128u,
                priority: 100.0f - i,
                contentHash: (ulong)(i + 1));
        }

        Dictionary<ShadowRequestKey, AllocationSignature> settledLayout = [];
        for (ulong frame = 1u; frame <= 60u; frame++)
        {
            ShadowAtlasFrameData frameData = RunFrame(manager, frame, requests);
            Dictionary<ShadowRequestKey, AllocationSignature> layout = CaptureResidentLayout(frameData);
            if (frame == 12u)
            {
                settledLayout = layout;
                continue;
            }

            if (frame > 12u)
                layout.ShouldBe(settledLayout);
        }
    }

    [Test]
    public void BackFacingPointFacesRemainConsistentDemotionVictims()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        PointLightComponent light = CreatePointLight(512u);
        ShadowMapRequest[] requests = new ShadowMapRequest[PointLightComponent.ShadowFaceCount];
        for (int faceIndex = 0; faceIndex < requests.Length; faceIndex++)
        {
            float relevance = faceIndex < 3 ? 100.0f : 10.0f;
            requests[faceIndex] = CreatePointFaceRequest(
                light,
                faceIndex,
                desiredResolution: 512u,
                minimumResolution: 128u,
                priority: relevance - faceIndex * 0.01f,
                contentHash: (ulong)(faceIndex + 1));
        }

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        for (int faceIndex = 0; faceIndex < 3; faceIndex++)
        {
            frameData.TryGetAllocation(requests[faceIndex].Key, out ShadowAtlasAllocation frontFace).ShouldBeTrue();
            frontFace.IsResident.ShouldBeTrue();
            frontFace.Resolution.ShouldBeGreaterThanOrEqualTo(256u);
        }

        for (int faceIndex = 3; faceIndex < requests.Length; faceIndex++)
        {
            frameData.TryGetAllocation(requests[faceIndex].Key, out ShadowAtlasAllocation backFace).ShouldBeTrue();
            if (backFace.IsResident)
                backFace.Resolution.ShouldBeLessThanOrEqualTo(256u);
        }
    }

    private static ShadowAtlasManager CreateManager(uint pageSize, int maxPages)
        => new(new ShadowAtlasManagerSettings(
            PageSize: pageSize,
            MaxPages: maxPages,
            MaxMemoryBytes: 0L,
            MaxTilesRenderedPerFrame: 64,
            MaxRenderMilliseconds: 2.0f,
            MinTileResolution: 128u,
            MaxTileResolution: pageSize,
            MaxRequestsPerFrame: 128));

    private static PointLightComponent CreatePointLight(uint resolution)
    {
        PointLightComponent light = new();
        light.SetShadowMapResolution(resolution, resolution);
        return light;
    }

    private static ShadowMapRequest CreatePointFaceRequest(
        PointLightComponent light,
        int faceIndex,
        uint desiredResolution,
        uint minimumResolution,
        float priority,
        ulong contentHash)
    {
        ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.PointFace, faceIndex);
        return new ShadowMapRequest(
            key,
            light,
            EShadowProjectionType.PointFace,
            EShadowMapEncoding.Depth,
            ShadowCasterFilterMode.Opaque,
            ShadowFallbackMode.StaleTile,
            faceIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            0.1f,
            100.0f,
            desiredResolution,
            minimumResolution,
            priority,
            contentHash,
            IsDirty: true,
            DirtyReason: ShadowDirtyReason.ContentChanged,
            CanReusePreviousFrame: true,
            EditorPinned: false,
            StereoVis: StereoVisibility.Mono);
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

    private static Dictionary<ShadowRequestKey, AllocationSignature> CaptureResidentLayout(ShadowAtlasFrameData frameData)
    {
        Dictionary<ShadowRequestKey, AllocationSignature> layout = [];
        for (int i = 0; i < frameData.AllocationCount; i++)
        {
            ShadowAtlasAllocation allocation = frameData.GetAllocation(i);
            if (!allocation.IsResident)
                continue;

            layout[allocation.Key] = new AllocationSignature(
                allocation.PageIndex,
                allocation.PixelRect.X,
                allocation.PixelRect.Y,
                allocation.Resolution);
        }

        return layout;
    }

    private readonly record struct AllocationSignature(int PageIndex, int X, int Y, uint Resolution);
}
