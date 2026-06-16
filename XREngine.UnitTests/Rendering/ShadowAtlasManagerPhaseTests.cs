using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Shadows;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class ShadowAtlasManagerPhaseTests
{
    private static readonly FieldInfo WorldField = typeof(RuntimeWorldObjectBase).GetField(
        "_world",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RuntimeWorldObjectBase._world field was not found.");
    private static readonly FieldInfo CurrentAllocationsField = typeof(ShadowAtlasManager).GetField(
        "_currentAllocations",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ShadowAtlasManager._currentAllocations field was not found.");
    private static readonly MethodInfo MarkTileRenderedMethod = typeof(ShadowAtlasManager).GetMethod(
        "MarkTileRendered",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ShadowAtlasManager.MarkTileRendered method was not found.");
    private static readonly MethodInfo ResolveShadowDirtyReasonMethod = typeof(Lights3DCollection).GetMethod(
        "ResolveShadowDirtyReason",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Lights3DCollection.ResolveShadowDirtyReason method was not found.");
    private static readonly FieldInfo LightLastMovedTicksField = typeof(LightComponent).GetField(
        "_lastMovedTicks",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LightComponent._lastMovedTicks field was not found.");
    private static readonly FieldInfo LightMovementVersionField = typeof(LightComponent).GetField(
        "_movementVersion",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LightComponent._movementVersion field was not found.");
    private static readonly IRuntimeWorldContext ActiveWorld = new TestWorldContext();

    private IRuntimeRenderingHostServices _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
    private IRuntimeShaderServices? _previousShaderServices;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeRenderingHostServices.Current = ShadowAtlasTestRenderingHostServices.Create(
            _previousRenderingHostServices,
            new ShadowAtlasTestRenderPipeline());
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        RuntimeRenderingHostServices.Current = _previousRenderingHostServices;
        RuntimeShaderServices.Current = _previousShaderServices;
    }

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
    public void DirectionalAtlas_AcceptsAllResolvedDirectionalEncodings()
    {
        bool previousUseDirectionalShadowAtlas = RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas;
        RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas = true;
        try
        {
            DirectionalLightComponent light = CreateDirectionalLight(512u);

            light.ShadowMapEncoding = EShadowMapEncoding.Depth;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeTrue();

            light.ShadowMapEncoding = EShadowMapEncoding.Variance2;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeTrue();

            light.ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance2;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeTrue();

            light.ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance4;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeTrue();
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas = previousUseDirectionalShadowAtlas;
        }
    }

    [Test]
    public void DirectionalAtlas_VulkanUsesDepthOnlyContractWithoutChangingOpenGl()
    {
        bool previousUseDirectionalShadowAtlas = RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas;
        IRuntimeRenderingHostServices previousHost = RuntimeRenderingHostServices.Current;
        RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas = true;
        try
        {
            DirectionalLightComponent light = CreateDirectionalLight(512u);

            RuntimeRenderingHostServices.Current = ShadowAtlasTestRenderingHostServices.Create(
                previousHost,
                new ShadowAtlasTestRenderPipeline(),
                RuntimeGraphicsApiKind.OpenGL);
            light.ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance4;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeTrue();
            light.CanUseDirectionalCascadeShadowAtlasForCurrentBackend(4).ShouldBeTrue();

            RuntimeRenderingHostServices.Current = ShadowAtlasTestRenderingHostServices.Create(
                previousHost,
                new ShadowAtlasTestRenderPipeline(),
                RuntimeGraphicsApiKind.Vulkan);
            light.ShadowMapEncoding = EShadowMapEncoding.Depth;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeTrue();
            light.CanUseDirectionalCascadeShadowAtlasForCurrentBackend(4).ShouldBeTrue();

            light.ShadowMapEncoding = EShadowMapEncoding.Variance2;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeFalse();

            light.ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance2;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeFalse();

            light.ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance4;
            light.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeFalse();
        }
        finally
        {
            RuntimeRenderingHostServices.Current = previousHost;
            RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas = previousUseDirectionalShadowAtlas;
        }
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
    public void SolveAllocations_BalancesDirectionalCascadesIntoOneAtlasPage()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 4096u, maxPages: 1);
        DirectionalLightComponent light = CreateDirectionalLight(4096u);
        ShadowMapRequest[] requests =
        [
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 0, 4096u, 128u, 10000.0f, 1u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 1, 4096u, 128u, 9900.0f, 2u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 2, 4096u, 128u, 9800.0f, 3u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 3, 4096u, 128u, 9700.0f, 4u),
        ];

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        frameData.Metrics.PageCount.ShouldBe(1);
        frameData.Metrics.ResidentTileCount.ShouldBe(4);
        frameData.Metrics.SkippedRequestCount.ShouldBe(0);
        AssertNoResidentOverlaps(frameData);
        frameData.DirectionalCascadeGroupCount.ShouldBe(1);
        frameData.TryGetDirectionalCascadeGroup(light.ID, out ShadowAtlasGroupedDirectionalCascadeAllocation group).ShouldBeTrue();
        group.CascadeCount.ShouldBe(4);
        group.Members.Length.ShouldBe(4);

        for (int i = 0; i < requests.Length; i++)
        {
            frameData.TryGetAllocation(requests[i].Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
            allocation.AtlasKind.ShouldBe(EShadowAtlasKind.Directional);
            allocation.PageIndex.ShouldBe(0);
            allocation.Resolution.ShouldBe(2048u);
            allocation.IsResident.ShouldBeTrue();
            allocation.SkipReason.ShouldBe(SkipReason.None);
        }

        group.Members[0].PixelRect.X.ShouldBe(0);
        group.Members[0].PixelRect.Y.ShouldBe(0);
        group.Members[1].PixelRect.X.ShouldBe(2048);
        group.Members[1].PixelRect.Y.ShouldBe(0);
        group.Members[2].PixelRect.X.ShouldBe(0);
        group.Members[2].PixelRect.Y.ShouldBe(2048);
        group.Members[3].PixelRect.X.ShouldBe(2048);
        group.Members[3].PixelRect.Y.ShouldBe(2048);
    }

    [Test]
    public void SolveAllocations_PublishesBackendAwareDirectionalAtlasUvBias()
    {
        ERenderClipSpaceYDirection previousClipY = RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;
        IRuntimeRenderingHostServices previousHost = RuntimeRenderingHostServices.Current;
        try
        {
            RuntimeEngine.Rendering.Settings.ClipSpaceYDirection = ERenderClipSpaceYDirection.YUp;

            ShadowAtlasAllocation glAllocation = AllocateSingleDirectionalCascade(RuntimeGraphicsApiKind.OpenGL);
            ShadowAtlasAllocation vkAllocation = AllocateSingleDirectionalCascade(RuntimeGraphicsApiKind.Vulkan);

            glAllocation.InnerPixelRect.ShouldBe(vkAllocation.InnerPixelRect);
            glAllocation.UvScaleBias.X.ShouldBe(vkAllocation.UvScaleBias.X, 0.000001f);
            glAllocation.UvScaleBias.Y.ShouldBe(vkAllocation.UvScaleBias.Y, 0.000001f);
            glAllocation.UvScaleBias.Z.ShouldBe(vkAllocation.UvScaleBias.Z, 0.000001f);

            float invPageSize = 1.0f / 1024.0f;
            float expectedGlBiasY = glAllocation.InnerPixelRect.Y * invPageSize;
            float expectedVkBiasY = 1.0f - ((vkAllocation.InnerPixelRect.Y + vkAllocation.InnerPixelRect.Height) * invPageSize);

            glAllocation.UvScaleBias.W.ShouldBe(expectedGlBiasY, 0.000001f);
            vkAllocation.UvScaleBias.W.ShouldBe(expectedVkBiasY, 0.000001f);
            vkAllocation.UvScaleBias.W.ShouldNotBe(glAllocation.UvScaleBias.W);
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.ClipSpaceYDirection = previousClipY;
            RuntimeRenderingHostServices.Current = previousHost;
        }
    }

    [Test]
    public void SolveAllocations_PublishesGroupedDirectionalCascadeRecord()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 2048u, maxPages: 1);
        DirectionalLightComponent light = CreateDirectionalLight(2048u);
        ShadowMapRequest[] requests =
        [
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 0, 1024u, 128u, 10000.0f, 1u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 1, 1024u, 128u, 9900.0f, 2u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 2, 1024u, 128u, 9800.0f, 3u),
        ];

        manager.BeginFrame(1u, activeCameraCount: 1);
        for (int i = 0; i < requests.Length; i++)
            manager.Submit(requests[i]).ShouldBeTrue();

        manager.SolveAllocations();
        manager.RenderScheduledTiles();
        MarkResidentRequestsRendered(manager, requests);
        manager.PublishFrameData();
        ShadowAtlasFrameData frameData = manager.PublishedFrameData;

        frameData.DirectionalCascadeGroupCount.ShouldBe(1);
        frameData.TryGetDirectionalCascadeGroup(light.ID, out ShadowAtlasGroupedDirectionalCascadeAllocation group).ShouldBeTrue();
        group.AtlasKind.ShouldBe(EShadowAtlasKind.Directional);
        group.PageIndex.ShouldBe(0);
        group.CascadeCount.ShouldBe(3);
        group.Members.Length.ShouldBe(3);

        for (int i = 0; i < group.CascadeCount; i++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[i];
            member.CascadeIndex.ShouldBe(i);
            member.ViewportScissorIndex.ShouldBe(i);
            member.RecordIndex.ShouldBeGreaterThanOrEqualTo(0);
            member.InnerPixelRect.Width.ShouldBeGreaterThan(0);
            member.InnerPixelRect.Height.ShouldBeGreaterThan(0);
            frameData.GetAllocation(member.RecordIndex).Key.ShouldBe(requests[i].Key);
        }
    }

    [Test]
    public void SolveAllocations_PublishesGroupedDirectionalCascadeRecordForResidentSubset()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 2048u, maxPages: 1);
        DirectionalLightComponent light = CreateDirectionalLight(2048u);
        ShadowMapRequest[] requests =
        [
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 0, 1024u, 128u, 10000.0f, 1u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 1, 1024u, 128u, 9900.0f, 2u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 2, 1024u, 128u, 9800.0f, 3u, SkipReason.NotRelevant),
        ];

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        frameData.DirectionalCascadeGroupCount.ShouldBe(1);
        frameData.TryGetDirectionalCascadeGroup(light.ID, out ShadowAtlasGroupedDirectionalCascadeAllocation group).ShouldBeTrue();
        group.CascadeCount.ShouldBe(2);
        group.Members[0].CascadeIndex.ShouldBe(0);
        group.Members[0].ViewportScissorIndex.ShouldBe(0);
        group.Members[1].CascadeIndex.ShouldBe(1);
        group.Members[1].ViewportScissorIndex.ShouldBe(1);
    }

    [Test]
    public void RenderScheduledTiles_RendersDirtyDirectionalCascadeGroupPastTileBudget()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 2048u, maxPages: 1, maxTiles: 1);
        DirectionalLightComponent light = CreateDirectionalLight(2048u);
        ShadowMapRequest[] requests =
        [
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 0, 1024u, 128u, 10000.0f, 1u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 1, 1024u, 128u, 9900.0f, 2u),
            CreateRequest(light, EShadowProjectionType.DirectionalCascade, 2, 1024u, 128u, 9800.0f, 3u),
        ];

        manager.BeginFrame(1u, activeCameraCount: 1);
        for (int i = 0; i < requests.Length; i++)
            manager.Submit(requests[i]).ShouldBeTrue();

        manager.SolveAllocations();
        manager.RenderScheduledTiles();
        MarkResidentRequestsRendered(manager, requests);
        manager.PublishFrameData();
        ShadowAtlasFrameData frameData = manager.PublishedFrameData;

        frameData.DirectionalCascadeGroupCount.ShouldBe(1);
        for (int i = 0; i < requests.Length; i++)
        {
            frameData.TryGetAllocation(requests[i].Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
            allocation.LastRenderedFrame.ShouldBe(1u);
            allocation.ActiveFallback.ShouldBe(ShadowFallbackMode.None);
        }
    }

    [Test]
    public void SolveAllocations_DirtyDirectionalRefreshDoesNotPublishStaleFallbackBeforeRender()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 1024u, maxPages: 1);
        DirectionalLightComponent light = CreateDirectionalLight(1024u);
        ShadowMapRequest firstRequest = CreateRequest(
            light,
            EShadowProjectionType.DirectionalCascade,
            0,
            512u,
            128u,
            10000.0f,
            1u);
        RunFrameAndMarkRendered(manager, 1u, firstRequest)
            .TryGetAllocation(firstRequest.Key, out ShadowAtlasAllocation firstAllocation)
            .ShouldBeTrue();
        firstAllocation.LastRenderedFrame.ShouldBe(1u);

        ShadowMapRequest movedRequest = CreateRequest(
            light,
            EShadowProjectionType.DirectionalCascade,
            0,
            512u,
            128u,
            10000.0f,
            2u);
        manager.BeginFrame(2u, activeCameraCount: 1);
        manager.Submit(movedRequest).ShouldBeTrue();
        manager.SolveAllocations();
        manager.PublishFrameData();

        ShadowAtlasFrameData frameData = manager.PublishedFrameData;
        frameData.TryGetAllocation(movedRequest.Key, out ShadowAtlasAllocation movedAllocation).ShouldBeTrue();
        movedAllocation.LastRenderedFrame.ShouldBe(1u);
        movedAllocation.ActiveFallback.ShouldBe(ShadowFallbackMode.Lit);
        movedAllocation.SkipReason.ShouldBe(SkipReason.None);
    }

    [Test]
    public void SolveAllocations_DirtyLocalProjectionRefreshDoesNotPublishStaleFallbackBeforeRender()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 1024u, maxPages: 1);
        SpotLightComponent light = CreateSpotLight(1024u);
        ShadowMapRequest firstRequest = CreateRequest(
            light,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            128u,
            10000.0f,
            1u);
        RunFrameAndMarkRendered(manager, 1u, firstRequest)
            .TryGetAllocation(firstRequest.Key, out ShadowAtlasAllocation firstAllocation)
            .ShouldBeTrue();
        firstAllocation.LastRenderedFrame.ShouldBe(1u);

        ShadowMapRequest movedRequest = CreateRequest(
            light,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            128u,
            10000.0f,
            2u,
            dirtyReason: ShadowDirtyReason.ContentChanged | ShadowDirtyReason.ProjectionOrCameraFitChanged);
        manager.BeginFrame(2u, activeCameraCount: 1);
        manager.Submit(movedRequest).ShouldBeTrue();
        manager.SolveAllocations();
        manager.PublishFrameData();

        ShadowAtlasFrameData frameData = manager.PublishedFrameData;
        frameData.TryGetAllocation(movedRequest.Key, out ShadowAtlasAllocation movedAllocation).ShouldBeTrue();
        movedAllocation.LastRenderedFrame.ShouldBe(1u);
        movedAllocation.ActiveFallback.ShouldBe(ShadowFallbackMode.Lit);
        movedAllocation.SkipReason.ShouldBe(SkipReason.None);
    }

    [Test]
    public void ResolveShadowDirtyReason_RecentLocalMovementUsesProjectionOrCameraFitReason()
    {
        SpotLightComponent movedSpot = CreateSpotLight(512u);
        MarkLightMovedNow(movedSpot);
        ShadowDirtyReason movedSpotReason = ResolveDirtyReasonForContentChange(
            movedSpot,
            EShadowProjectionType.SpotPrimary);

        (movedSpotReason & ShadowDirtyReason.ContentChanged).ShouldBe(ShadowDirtyReason.ContentChanged);
        (movedSpotReason & ShadowDirtyReason.ProjectionOrCameraFitChanged).ShouldBe(ShadowDirtyReason.ProjectionOrCameraFitChanged);
        (movedSpotReason & ShadowDirtyReason.LightOrSettingsChanged).ShouldBe(ShadowDirtyReason.None);

        PointLightComponent movedPoint = CreatePointLight(512u);
        MarkLightMovedNow(movedPoint);
        ShadowDirtyReason movedPointReason = ResolveDirtyReasonForContentChange(
            movedPoint,
            EShadowProjectionType.PointFace);

        (movedPointReason & ShadowDirtyReason.ContentChanged).ShouldBe(ShadowDirtyReason.ContentChanged);
        (movedPointReason & ShadowDirtyReason.ProjectionOrCameraFitChanged).ShouldBe(ShadowDirtyReason.ProjectionOrCameraFitChanged);
        (movedPointReason & ShadowDirtyReason.LightOrSettingsChanged).ShouldBe(ShadowDirtyReason.None);

        SpotLightComponent steadySpot = CreateSpotLight(512u);
        ClearLightMovement(steadySpot);
        ShadowDirtyReason steadySpotReason = ResolveDirtyReasonForContentChange(
            steadySpot,
            EShadowProjectionType.SpotPrimary);

        (steadySpotReason & ShadowDirtyReason.ProjectionOrCameraFitChanged).ShouldBe(ShadowDirtyReason.None);
        (steadySpotReason & ShadowDirtyReason.LightOrSettingsChanged).ShouldBe(ShadowDirtyReason.LightOrSettingsChanged);
    }

    [Test]
    public void SolveAllocations_UsesSeparateAtlasPagesPerLightFamily()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        DirectionalLightComponent directionalLight = CreateDirectionalLight(512u);
        PointLightComponent pointLight = CreatePointLight(512u);
        SpotLightComponent spotLight = CreateSpotLight(512u);
        ShadowMapRequest directional = CreateRequest(directionalLight, EShadowProjectionType.DirectionalCascade, 0, 512u, 128u, 100.0f, 1u);
        ShadowMapRequest point = CreateRequest(pointLight, EShadowProjectionType.PointFace, 0, 512u, 128u, 100.0f, 2u);
        ShadowMapRequest spot = CreateRequest(spotLight, EShadowProjectionType.SpotPrimary, 0, 512u, 128u, 100.0f, 3u);

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, directional, point, spot);

        frameData.Metrics.PageCount.ShouldBe(3);
        frameData.Metrics.ResidentTileCount.ShouldBe(3);
        frameData.TryGetAllocation(directional.Key, out ShadowAtlasAllocation directionalAllocation).ShouldBeTrue();
        frameData.TryGetAllocation(point.Key, out ShadowAtlasAllocation pointAllocation).ShouldBeTrue();
        frameData.TryGetAllocation(spot.Key, out ShadowAtlasAllocation spotAllocation).ShouldBeTrue();
        directionalAllocation.AtlasKind.ShouldBe(EShadowAtlasKind.Directional);
        pointAllocation.AtlasKind.ShouldBe(EShadowAtlasKind.Point);
        spotAllocation.AtlasKind.ShouldBe(EShadowAtlasKind.Spot);
        directionalAllocation.AtlasId.ShouldNotBe(pointAllocation.AtlasId);
        directionalAllocation.AtlasId.ShouldNotBe(spotAllocation.AtlasId);
        pointAllocation.AtlasId.ShouldNotBe(spotAllocation.AtlasId);
    }

    [Test]
    public void SolveAllocations_HonorsConfiguredMultiPageLimitPerFamily()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 2);
        ShadowMapRequest[] requests =
        [
            CreateRequest(CreateSpotLight(512u), EShadowProjectionType.SpotPrimary, 0, 512u, 512u, 10.0f, 1u),
            CreateRequest(CreateSpotLight(512u), EShadowProjectionType.SpotPrimary, 0, 512u, 512u, 9.0f, 2u),
            CreateRequest(CreateSpotLight(512u), EShadowProjectionType.SpotPrimary, 0, 512u, 512u, 8.0f, 3u),
        ];

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        frameData.Metrics.PageCount.ShouldBe(2);
        frameData.Metrics.ResidentTileCount.ShouldBe(2);
        frameData.Metrics.SkippedRequestCount.ShouldBe(1);
        frameData.TryGetAllocation(requests[0].Key, out ShadowAtlasAllocation first).ShouldBeTrue();
        frameData.TryGetAllocation(requests[1].Key, out ShadowAtlasAllocation second).ShouldBeTrue();
        first.PageIndex.ShouldBe(0);
        second.PageIndex.ShouldBe(1);
    }

    [Test]
    public void SolveAllocations_AllocatesSixPointFacesIndependently()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 1024u, maxPages: 1);
        PointLightComponent light = CreatePointLight(256u);
        ShadowMapRequest[] requests = new ShadowMapRequest[PointLightComponent.ShadowFaceCount];
        for (int faceIndex = 0; faceIndex < requests.Length; faceIndex++)
        {
            requests[faceIndex] = CreateRequest(
                light,
                EShadowProjectionType.PointFace,
                faceIndex,
                desiredResolution: 256u,
                minimumResolution: 256u,
                priority: 100.0f - faceIndex,
                contentHash: (ulong)(faceIndex + 1));
        }

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        frameData.Metrics.PageCount.ShouldBe(1);
        frameData.Metrics.ResidentTileCount.ShouldBe(PointLightComponent.ShadowFaceCount);
        frameData.Metrics.SkippedRequestCount.ShouldBe(0);
        AssertNoResidentOverlaps(frameData);
        for (int faceIndex = 0; faceIndex < requests.Length; faceIndex++)
        {
            frameData.TryGetAllocation(requests[faceIndex].Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
            allocation.AtlasKind.ShouldBe(EShadowAtlasKind.Point);
            allocation.PageIndex.ShouldBe(0);
            allocation.Resolution.ShouldBe(256u);
            allocation.IsResident.ShouldBeTrue();
            allocation.Key.FaceOrCascadeIndex.ShouldBe(faceIndex);
        }

        frameData.PointFaceGroupCount.ShouldBe(1);
        frameData.TryGetPointFaceGroup(light.ID, out ShadowAtlasGroupedPointFaceAllocation group).ShouldBeTrue();
        group.AtlasKind.ShouldBe(EShadowAtlasKind.Point);
        group.PageIndex.ShouldBe(0);
        group.FaceCount.ShouldBe(PointLightComponent.ShadowFaceCount);
        group.Members.Length.ShouldBe(PointLightComponent.ShadowFaceCount);
        for (int faceIndex = 0; faceIndex < group.FaceCount; faceIndex++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[faceIndex];
            member.CascadeIndex.ShouldBe(faceIndex);
            member.ViewportScissorIndex.ShouldBe(faceIndex);
            member.RecordIndex.ShouldBeGreaterThanOrEqualTo(0);
            frameData.GetAllocation(member.RecordIndex).Key.ShouldBe(requests[faceIndex].Key);
        }
    }

    [Test]
    public void SolveAllocations_DemotesOversizedPointFacesBeforeSkippingFaces()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 4096u, maxPages: 1);
        PointLightComponent light = CreatePointLight(4096u);
        ShadowMapRequest[] requests = new ShadowMapRequest[PointLightComponent.ShadowFaceCount];
        for (int faceIndex = 0; faceIndex < requests.Length; faceIndex++)
        {
            requests[faceIndex] = CreateRequest(
                light,
                EShadowProjectionType.PointFace,
                faceIndex,
                desiredResolution: 4096u,
                minimumResolution: 128u,
                priority: 1000.0f - faceIndex,
                contentHash: (ulong)(faceIndex + 1));
        }

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        frameData.Metrics.PageCount.ShouldBe(1);
        frameData.Metrics.ResidentTileCount.ShouldBe(PointLightComponent.ShadowFaceCount);
        frameData.Metrics.SkippedRequestCount.ShouldBe(0);
        AssertNoResidentOverlaps(frameData);
        for (int faceIndex = 0; faceIndex < requests.Length; faceIndex++)
        {
            frameData.TryGetAllocation(requests[faceIndex].Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
            allocation.IsResident.ShouldBeTrue();
            allocation.Resolution.ShouldBeLessThan(4096u);
            allocation.Resolution.ShouldBeGreaterThanOrEqualTo(128u);
        }
    }

    [Test]
    public void SolveAllocations_AllowsPartialPointFaceResidency()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        PointLightComponent light = CreatePointLight(256u);
        ShadowMapRequest[] requests = new ShadowMapRequest[PointLightComponent.ShadowFaceCount];
        for (int faceIndex = 0; faceIndex < requests.Length; faceIndex++)
        {
            requests[faceIndex] = CreateRequest(
                light,
                EShadowProjectionType.PointFace,
                faceIndex,
                desiredResolution: 256u,
                minimumResolution: 256u,
                priority: 100.0f - faceIndex,
                contentHash: (ulong)(faceIndex + 1));
        }

        ShadowAtlasFrameData frameData = RunFrame(manager, 1u, requests);

        frameData.Metrics.ResidentTileCount.ShouldBe(4);
        frameData.Metrics.SkippedRequestCount.ShouldBe(2);
        AssertNoResidentOverlaps(frameData);
        for (int faceIndex = 0; faceIndex < 4; faceIndex++)
        {
            frameData.TryGetAllocation(requests[faceIndex].Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
            allocation.IsResident.ShouldBeTrue();
            allocation.ActiveFallback.ShouldBe(ShadowFallbackMode.None);
        }

        for (int faceIndex = 4; faceIndex < requests.Length; faceIndex++)
        {
            frameData.TryGetAllocation(requests[faceIndex].Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
            allocation.IsResident.ShouldBeFalse();
            allocation.ActiveFallback.ShouldBe(ShadowFallbackMode.StaleTile);
        }
    }

    [Test]
    public void SolveAllocations_PublishesSolveDiagnosticsForCapacityDemotion()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        ShadowMapRequest highPriority = CreateRequest(
            CreateSpotLight(256u),
            EShadowProjectionType.SpotPrimary,
            0,
            desiredResolution: 256u,
            minimumResolution: 128u,
            priority: 100.0f,
            contentHash: 1u);
        ShadowMapRequest lowerPriority = CreateRequest(
            CreateSpotLight(512u),
            EShadowProjectionType.SpotPrimary,
            0,
            desiredResolution: 512u,
            minimumResolution: 128u,
            priority: 10.0f,
            contentHash: 2u);

        ShadowAtlasFrameData frameData = RunSolvedFrame(manager, 1u, highPriority, lowerPriority);
        ShadowAtlasSolveDiagnostics diagnostics = frameData.SolveDiagnostics;

        diagnostics.ClassifiedRequestCount.ShouldBe(2);
        diagnostics.SpotRequestCount.ShouldBe(2);
        diagnostics.DepthRequestCount.ShouldBe(2);
        diagnostics.BalancedSolveAttemptCount.ShouldBeGreaterThanOrEqualTo(1);
        diagnostics.FailedCandidateCount.ShouldBeGreaterThan(0);
        diagnostics.DemotionCount.ShouldBeGreaterThan(0);
        diagnostics.PageAllocationAttemptCount.ShouldBeGreaterThan(0);
        diagnostics.PageAllocationSuccessCount.ShouldBeGreaterThan(0);
        diagnostics.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(0.0);
    }

    [Test]
    public void SolveAllocations_PublishesManyPointFaceGroupsWithLinearWorkCounters()
    {
        const int lightCount = 8;
        ShadowAtlasManager manager = CreateManager(pageSize: 2048u, maxPages: 1, maxRequests: lightCount * PointLightComponent.ShadowFaceCount);
        ShadowMapRequest[] requests = new ShadowMapRequest[lightCount * PointLightComponent.ShadowFaceCount];
        int requestIndex = 0;
        for (int lightIndex = 0; lightIndex < lightCount; lightIndex++)
        {
            PointLightComponent light = CreatePointLight(128u);
            for (int faceIndex = 0; faceIndex < PointLightComponent.ShadowFaceCount; faceIndex++)
            {
                requests[requestIndex] = CreateRequest(
                    light,
                    EShadowProjectionType.PointFace,
                    faceIndex,
                    desiredResolution: 128u,
                    minimumResolution: 128u,
                    priority: 1000.0f - requestIndex,
                    contentHash: (ulong)(requestIndex + 1));
                requestIndex++;
            }
        }

        ShadowAtlasFrameData frameData = RunSolvedFrame(manager, 1u, requests);

        frameData.PointFaceGroupCount.ShouldBe(lightCount);
        frameData.SolveDiagnostics.PointGroupSeedCount.ShouldBe(lightCount);
        frameData.SolveDiagnostics.PointGroupMemberCount.ShouldBe(requests.Length);
        frameData.SolveDiagnostics.PointGroupCoLocationFailureCount.ShouldBe(0);
        AssertNoResidentOverlaps(frameData);
    }

    [Test]
    public void SolveAllocations_OverBudgetDemotionHasBoundedAttemptCount()
    {
        const int requestCount = 12;
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1, maxRequests: requestCount);
        ShadowMapRequest[] requests = new ShadowMapRequest[requestCount];
        for (int i = 0; i < requests.Length; i++)
        {
            requests[i] = CreateRequest(
                CreateSpotLight(512u),
                EShadowProjectionType.SpotPrimary,
                0,
                desiredResolution: 512u,
                minimumResolution: 128u,
                priority: 1000.0f - i,
                contentHash: (ulong)(i + 1));
        }

        ShadowAtlasFrameData frameData = RunSolvedFrame(manager, 1u, requests);

        frameData.SolveDiagnostics.FailedCandidateCount.ShouldBeGreaterThan(0);
        frameData.SolveDiagnostics.DemotionCount.ShouldBeGreaterThan(0);
        frameData.SolveDiagnostics.BalancedSolveAttemptCount.ShouldBeLessThanOrEqualTo((requestCount * 4) + 9);
        AssertNoResidentOverlaps(frameData);
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

    [Test]
    public void SolveAllocations_DownsizeReusesPriorSubRectAndPreservesRenderedFrame()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        SpotLightComponent light = CreateSpotLight(512u);
        ShadowMapRequest fullSize = CreateRequest(
            light,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            128u,
            10.0f,
            1u);
        ShadowAtlasFrameData firstFrame = RunFrameAndMarkRendered(manager, 1u, fullSize);
        firstFrame.TryGetAllocation(fullSize.Key, out ShadowAtlasAllocation first).ShouldBeTrue();
        first.LastRenderedFrame.ShouldBe(1u);

        ShadowMapRequest halfSize = CreateRequest(
            light,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            10.0f,
            2u);
        ShadowAtlasFrameData secondFrame = RunSolvedFrame(manager, 10u, halfSize);

        secondFrame.TryGetAllocation(halfSize.Key, out ShadowAtlasAllocation second).ShouldBeTrue();
        second.IsResident.ShouldBeTrue();
        second.Resolution.ShouldBe(256u);
        second.PageIndex.ShouldBe(first.PageIndex);
        second.PixelRect.X.ShouldBe(first.PixelRect.X);
        second.PixelRect.Y.ShouldBe(first.PixelRect.Y);
        second.LastRenderedFrame.ShouldBe(first.LastRenderedFrame);
        second.SkipReason.ShouldBe(SkipReason.StaleTileReused);
    }

    [Test]
    public void SolveAllocations_DeferredUpgradeKeepsPriorSlotWhenSurroundingBlockIsOccupied()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        SpotLightComponent growLight = CreateSpotLight(512u);
        SpotLightComponent blockerLight = CreateSpotLight(256u);
        ShadowMapRequest growInitial = CreateRequest(
            growLight,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            10.0f,
            1u);
        ShadowMapRequest blockerInitial = CreateRequest(
            blockerLight,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            9.0f,
            2u);
        ShadowAtlasFrameData firstFrame = RunFrame(manager, 1u, growInitial, blockerInitial);
        firstFrame.TryGetAllocation(growInitial.Key, out ShadowAtlasAllocation growFirst).ShouldBeTrue();
        firstFrame.TryGetAllocation(blockerInitial.Key, out ShadowAtlasAllocation blockerFirst).ShouldBeTrue();

        ShadowMapRequest blockerNext = CreateRequest(
            blockerLight,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            100.0f,
            3u);
        ShadowMapRequest growUpgrade = CreateRequest(
            growLight,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            128u,
            10.0f,
            4u);
        ShadowAtlasFrameData secondFrame = RunFrame(manager, 10u, blockerNext, growUpgrade);

        secondFrame.TryGetAllocation(growUpgrade.Key, out ShadowAtlasAllocation growSecond).ShouldBeTrue();
        secondFrame.TryGetAllocation(blockerNext.Key, out ShadowAtlasAllocation blockerSecond).ShouldBeTrue();
        blockerSecond.PageIndex.ShouldBe(blockerFirst.PageIndex);
        blockerSecond.PixelRect.X.ShouldBe(blockerFirst.PixelRect.X);
        blockerSecond.PixelRect.Y.ShouldBe(blockerFirst.PixelRect.Y);
        growSecond.Resolution.ShouldBe(256u);
        growSecond.PageIndex.ShouldBe(growFirst.PageIndex);
        growSecond.PixelRect.X.ShouldBe(growFirst.PixelRect.X);
        growSecond.PixelRect.Y.ShouldBe(growFirst.PixelRect.Y);
    }

    [Test]
    public void SolveAllocations_ReusesResidentTileAfterTransientMissingRequest()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        SpotLightComponent returningLight = CreateSpotLight(256u);
        SpotLightComponent steadyLight = CreateSpotLight(256u);
        ShadowMapRequest returning = CreateRequest(
            returningLight,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            10.0f,
            1u);
        ShadowMapRequest steady = CreateRequest(
            steadyLight,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            9.0f,
            2u);

        ShadowAtlasFrameData firstFrame = RunFrame(manager, 1u, returning, steady);
        firstFrame.TryGetAllocation(returning.Key, out ShadowAtlasAllocation first).ShouldBeTrue();
        first.IsResident.ShouldBeTrue();

        RunFrame(manager, 2u, steady);
        ShadowAtlasFrameData thirdFrame = RunFrame(manager, 3u, returning, steady);

        thirdFrame.TryGetAllocation(returning.Key, out ShadowAtlasAllocation third).ShouldBeTrue();
        third.IsResident.ShouldBeTrue();
        third.PageIndex.ShouldBe(first.PageIndex);
        third.PixelRect.X.ShouldBe(first.PixelRect.X);
        third.PixelRect.Y.ShouldBe(first.PixelRect.Y);
        third.Resolution.ShouldBe(first.Resolution);
    }

    [Test]
    public void SolveAllocations_ReusesNotRelevantStaleTileWhenRegionRemainsFree()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        SpotLightComponent light = CreateSpotLight(256u);
        ShadowMapRequest visible = CreateRequest(
            light,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            10.0f,
            1u);
        ShadowAtlasFrameData firstFrame = RunFrame(manager, 1u, visible);
        firstFrame.TryGetAllocation(visible.Key, out ShadowAtlasAllocation first).ShouldBeTrue();

        ShadowMapRequest notRelevant = CreateRequest(
            light,
            EShadowProjectionType.SpotPrimary,
            0,
            256u,
            128u,
            10.0f,
            2u,
            forcedSkipReason: SkipReason.NotRelevant);
        ShadowAtlasFrameData secondFrame = RunFrame(manager, 2u, notRelevant);

        secondFrame.TryGetAllocation(notRelevant.Key, out ShadowAtlasAllocation second).ShouldBeTrue();
        second.IsResident.ShouldBeTrue();
        second.SkipReason.ShouldBe(SkipReason.NotRelevant);
        second.PageIndex.ShouldBe(first.PageIndex);
        second.PixelRect.X.ShouldBe(first.PixelRect.X);
        second.PixelRect.Y.ShouldBe(first.PixelRect.Y);
    }

    [Test]
    public void SolveAllocations_DropsNotRelevantStaleTileWhenLiveTileClaimsRegion()
    {
        ShadowAtlasManager manager = CreateManager(pageSize: 512u, maxPages: 1);
        SpotLightComponent staleLight = CreateSpotLight(512u);
        SpotLightComponent liveLight = CreateSpotLight(512u);
        ShadowMapRequest staleVisible = CreateRequest(
            staleLight,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            512u,
            10.0f,
            1u);
        ShadowAtlasFrameData firstFrame = RunFrame(manager, 1u, staleVisible);
        firstFrame.TryGetAllocation(staleVisible.Key, out ShadowAtlasAllocation first).ShouldBeTrue();
        first.IsResident.ShouldBeTrue();

        ShadowMapRequest staleNotRelevant = CreateRequest(
            staleLight,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            512u,
            1.0f,
            2u,
            forcedSkipReason: SkipReason.NotRelevant);
        ShadowMapRequest live = CreateRequest(
            liveLight,
            EShadowProjectionType.SpotPrimary,
            0,
            512u,
            512u,
            100.0f,
            3u);
        ShadowAtlasFrameData secondFrame = RunFrame(manager, 2u, staleNotRelevant, live);

        secondFrame.TryGetAllocation(staleNotRelevant.Key, out ShadowAtlasAllocation stale).ShouldBeTrue();
        secondFrame.TryGetAllocation(live.Key, out ShadowAtlasAllocation liveAllocation).ShouldBeTrue();
        stale.IsResident.ShouldBeFalse();
        stale.SkipReason.ShouldBe(SkipReason.NotRelevant);
        liveAllocation.IsResident.ShouldBeTrue();
        AssertNoResidentOverlaps(secondFrame);
    }

    private static ShadowAtlasManager CreateManager(uint pageSize, int maxPages, int maxRequests = 32, int maxTiles = 16)
        => new(new ShadowAtlasManagerSettings(
            PageSize: pageSize,
            MaxPages: maxPages,
            MaxMemoryBytes: 0L,
            MaxTilesRenderedPerFrame: maxTiles,
            MaxRenderMilliseconds: 2.0f,
            MinTileResolution: 128u,
            MaxTileResolution: pageSize,
            MaxRequestsPerFrame: maxRequests));

    private static SpotLightComponent CreateSpotLight(uint resolution)
        => CreateActiveLight<SpotLightComponent>(resolution);

    private static PointLightComponent CreatePointLight(uint resolution)
        => CreateActiveLight<PointLightComponent>(resolution);

    private static DirectionalLightComponent CreateDirectionalLight(uint resolution)
        => CreateActiveLight<DirectionalLightComponent>(resolution);

    private ShadowAtlasAllocation AllocateSingleDirectionalCascade(RuntimeGraphicsApiKind backend)
    {
        RuntimeRenderingHostServices.Current = ShadowAtlasTestRenderingHostServices.Create(
            _previousRenderingHostServices,
            new ShadowAtlasTestRenderPipeline(),
            backend);

        ShadowAtlasManager manager = CreateManager(pageSize: 1024u, maxPages: 1);
        DirectionalLightComponent light = CreateDirectionalLight(1024u);
        ShadowMapRequest request = CreateRequest(
            light,
            EShadowProjectionType.DirectionalCascade,
            0,
            desiredResolution: 512u,
            minimumResolution: 128u,
            priority: 10000.0f,
            contentHash: 1u);

        ShadowAtlasFrameData frameData = RunSolvedFrame(manager, (ulong)backend + 1UL, request);
        frameData.TryGetAllocation(request.Key, out ShadowAtlasAllocation allocation).ShouldBeTrue();
        allocation.AtlasKind.ShouldBe(EShadowAtlasKind.Directional);
        allocation.IsResident.ShouldBeTrue();
        return allocation;
    }

    private static T CreateActiveLight<T>(uint resolution) where T : LightComponent
    {
        SceneNode node = new(typeof(T).Name);
        T light = node.AddComponent<T>()!;
        WorldField.SetValue(node, ActiveWorld);
        WorldField.SetValue(light, ActiveWorld);
        light.SetShadowMapResolution(resolution, resolution);
        return light;
    }

    private sealed class TestWorldContext : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => false;
        public void RegisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
    }

    private class ShadowAtlasTestRenderingHostServices : DispatchProxy
    {
        public IRuntimeRenderingHostServices Inner { get; set; } = null!;
        public IRuntimeRenderPipelineHost DefaultPipeline { get; set; } = null!;
        public RuntimeGraphicsApiKind? RenderBackendOverride { get; set; }

        public static IRuntimeRenderingHostServices Create(
            IRuntimeRenderingHostServices inner,
            IRuntimeRenderPipelineHost defaultPipeline,
            RuntimeGraphicsApiKind? renderBackendOverride = null)
        {
            IRuntimeRenderingHostServices proxy = Create<IRuntimeRenderingHostServices, ShadowAtlasTestRenderingHostServices>();
            ShadowAtlasTestRenderingHostServices state = (ShadowAtlasTestRenderingHostServices)(object)proxy;
            state.Inner = inner;
            state.DefaultPipeline = defaultPipeline;
            state.RenderBackendOverride = renderBackendOverride;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;

            if (targetMethod.Name == nameof(IRuntimeRenderingHostServices.CreateDefaultRenderPipeline))
                return DefaultPipeline;

            if (targetMethod.Name == $"get_{nameof(IRuntimeRenderingHostServices.CurrentRenderBackend)}" &&
                RenderBackendOverride is RuntimeGraphicsApiKind renderBackendOverride)
            {
                return renderBackendOverride;
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    private sealed class ShadowAtlasTestRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain()
            => new(this);

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => [];
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

    private static ShadowAtlasFrameData RunSolvedFrame(ShadowAtlasManager manager, ulong frameId, params ShadowMapRequest[] requests)
    {
        manager.BeginFrame(frameId, activeCameraCount: 1);
        for (int i = 0; i < requests.Length; i++)
            manager.Submit(requests[i]).ShouldBeTrue();

        manager.SolveAllocations();
        manager.PublishFrameData();
        return manager.PublishedFrameData;
    }

    private static ShadowAtlasFrameData RunFrameAndMarkRendered(ShadowAtlasManager manager, ulong frameId, params ShadowMapRequest[] requests)
    {
        manager.BeginFrame(frameId, activeCameraCount: 1);
        for (int i = 0; i < requests.Length; i++)
            manager.Submit(requests[i]).ShouldBeTrue();

        manager.SolveAllocations();
        MarkResidentRequestsRendered(manager, requests);
        manager.PublishFrameData();
        return manager.PublishedFrameData;
    }

    private static void MarkResidentRequestsRendered(ShadowAtlasManager manager, ReadOnlySpan<ShadowMapRequest> requests)
    {
        var currentAllocations = (Dictionary<ShadowRequestKey, ShadowAtlasAllocation>)CurrentAllocationsField.GetValue(manager)!;
        for (int i = 0; i < requests.Length; i++)
        {
            ShadowMapRequest request = requests[i];
            if (!currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation allocation) || !allocation.IsResident)
                continue;

            MarkTileRenderedMethod.Invoke(manager, new object[] { request, allocation });
        }
    }

    private static ShadowDirtyReason ResolveDirtyReasonForContentChange(
        LightComponent light,
        EShadowProjectionType projectionType)
    {
        const EShadowMapEncoding encoding = EShadowMapEncoding.Depth;
        ShadowAtlasAllocation previous = CreatePreviousRenderedAllocation(light, projectionType, encoding);
        object? result = ResolveShadowDirtyReasonMethod.Invoke(
            null,
            [
                light,
                projectionType,
                encoding,
                previous.ContentVersion + 1UL,
                true,
                true,
                previous,
            ]);

        return (ShadowDirtyReason)result!;
    }

    private static ShadowAtlasAllocation CreatePreviousRenderedAllocation(
        LightComponent light,
        EShadowProjectionType projectionType,
        EShadowMapEncoding encoding)
    {
        EShadowAtlasKind atlasKind = ResolveAtlasKind(projectionType);
        ShadowRequestKey key = light.CreateShadowRequestKey(projectionType, 0, encoding);
        return new ShadowAtlasAllocation(
            key,
            atlasKind,
            ((int)atlasKind << 8) | (int)encoding,
            0,
            default,
            default,
            Vector4.Zero,
            512u,
            0,
            1UL,
            1UL,
            IsResident: true,
            IsStaticCacheBacked: false,
            ShadowFallbackMode.None,
            SkipReason.None);
    }

    private static EShadowAtlasKind ResolveAtlasKind(EShadowProjectionType projectionType)
        => projectionType switch
        {
            EShadowProjectionType.DirectionalPrimary or EShadowProjectionType.DirectionalCascade => EShadowAtlasKind.Directional,
            EShadowProjectionType.PointFace => EShadowAtlasKind.Point,
            EShadowProjectionType.SpotPrimary => EShadowAtlasKind.Spot,
            _ => EShadowAtlasKind.Directional,
        };

    private static void MarkLightMovedNow(LightComponent light)
    {
        LightLastMovedTicksField.SetValue(light, RuntimeEngine.ElapsedTicks);
        LightMovementVersionField.SetValue(light, 1u);
    }

    private static void ClearLightMovement(LightComponent light)
    {
        LightLastMovedTicksField.SetValue(light, 0L);
        LightMovementVersionField.SetValue(light, 0u);
    }

    private static ShadowMapRequest CreateRequest(
        LightComponent light,
        EShadowProjectionType projectionType,
        int faceOrCascadeIndex,
        uint desiredResolution,
        uint minimumResolution,
        float priority,
        ulong contentHash,
        SkipReason forcedSkipReason = SkipReason.None,
        ShadowDirtyReason dirtyReason = ShadowDirtyReason.ContentChanged)
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
            DirtyReason: dirtyReason,
            CanReusePreviousFrame: true,
            EditorPinned: false,
            StereoVis: StereoVisibility.Mono,
            ForcedSkipReason: forcedSkipReason);
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
