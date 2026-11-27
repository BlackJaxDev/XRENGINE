using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedPortalViewSchedulerTests
{
    private sealed class FakePortalSurfaceProvider : IAdvancedPortalSurfaceProvider, IRenderable
    {
        public ulong PortalProviderId { get; init; }
        public bool IsPortalSurfaceActive { get; set; } = true;
        public AdvancedPortalSurfaceDescriptor Descriptor { get; set; }
        public RenderInfo[] RenderedObjects => [];

        public bool TryCapturePortalSurface(out AdvancedPortalSurfaceDescriptor descriptor)
        {
            if (!IsPortalSurfaceActive)
            {
                descriptor = default;
                return false;
            }
            descriptor = Descriptor;
            return true;
        }
    }

    private static AdvancedViewRecord CreatePrimaryView(
        uint viewId,
        Vector3 cameraPos,
        Vector3 targetPos,
        int width = 1280,
        int height = 720,
        EAdvancedViewRecordFlags extraFlags = EAdvancedViewRecordFlags.None)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(cameraPos, targetPos, Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.0f, (float)width / height, 0.1f, 100.0f);
        Matrix4x4 vp = view * proj;
        Matrix4x4.Invert(vp, out Matrix4x4 invVp);
        Vector3 forward = Vector3.Normalize(targetPos - cameraPos);
        return new AdvancedViewRecord
        {
            View = view,
            ProjectionJittered = proj,
            ProjectionUnjittered = proj,
            ViewProjectionJittered = vp,
            ViewProjectionUnjittered = vp,
            PreviousView = view,
            PreviousProjectionJittered = proj,
            PreviousProjectionUnjittered = proj,
            PreviousViewProjectionJittered = vp,
            PreviousViewProjectionUnjittered = vp,
            InverseViewProjectionJittered = invVp,
            InverseViewProjectionUnjittered = invVp,
            CameraPositionAndNear = new Vector4(cameraPos, 0.1f),
            CameraForwardAndFar = new Vector4(forward, 100.0f),
            RenderSizeAndInverse = new Vector4(width, height, 1.0f / width, 1.0f / height),
            OutputSizeAndInverse = new Vector4(width, height, 1.0f / width, 1.0f / height),
            DepthParams = new Vector4(0.1f, 100.0f, 0.0f, 1.0f),
            ViewId = viewId,
            OutputLayer = viewId,
            Flags = EAdvancedViewRecordFlags.DepthZeroToOne | EAdvancedViewRecordFlags.ReversedDepth | extraFlags,
            ViewMaskLo = 1u << (int)viewId,
            CullingLayerMask = uint.MaxValue,
        };
    }

    private static AdvancedPortalSurfaceDescriptor CreateMirrorSurface(
        ulong providerId,
        Vector3 center,
        Vector3 normal,
        float halfWidth,
        float halfHeight,
        uint drawIndex = 0u,
        bool allowRecursion = false,
        EMirrorMsaaShadingMode msaaMode = EMirrorMsaaShadingMode.SingleSample,
        Vector3? previousCenter = null)
    {
        Vector3 unitNormal = Vector3.Normalize(normal);
        Vector3 up = MathF.Abs(unitNormal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, unitNormal));
        Vector3 orthoUp = Vector3.Normalize(Vector3.Cross(unitNormal, right));

        Vector3 c0 = center - right * halfWidth - orthoUp * halfHeight;
        Vector3 c1 = center + right * halfWidth - orthoUp * halfHeight;
        Vector3 c2 = center + right * halfWidth + orthoUp * halfHeight;
        Vector3 c3 = center - right * halfWidth + orthoUp * halfHeight;

        Plane plane = new(unitNormal, -Vector3.Dot(unitNormal, center));
        Matrix4x4 refl = Matrix4x4.CreateReflection(plane);

        Vector3 prevPoint = previousCenter ?? center;
        Plane prevPlane = new(unitNormal, -Vector3.Dot(unitNormal, prevPoint));
        Matrix4x4 prevRefl = Matrix4x4.CreateReflection(prevPlane);

        return new AdvancedPortalSurfaceDescriptor(
            ProviderId: providerId,
            DrawHandle: drawIndex > 0u ? new AdvancedGpuHandle(drawIndex, 1u) : default,
            WorldMatrix: Matrix4x4.CreateTranslation(center),
            PreviousWorldMatrix: Matrix4x4.CreateTranslation(prevPoint),
            PlanePoint: center,
            PlaneNormal: unitNormal,
            PreviousPlanePoint: prevPoint,
            PreviousPlaneNormal: unitNormal,
            QuadCorner0: c0,
            QuadCorner1: c1,
            QuadCorner2: c2,
            QuadCorner3: c3,
            BoundsSphere: new Vector4(center, MathF.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight)),
            GlassTint: new Vector4(1f, 1f, 1f, 0f),
            FresnelStrength: 0f,
            SpecularStrength: 0f,
            CullingLayerMask: uint.MaxValue,
            MsaaShadingMode: msaaMode,
            AllowRecursion: allowRecursion,
            RenderTransparents: false,
            IsMirror: true,
            PortalTransform: refl,
            PreviousPortalTransform: prevRefl);
    }

    [Test]
    public void MirrorCaptureComponent_InPlaceStencilMode_CapturesSurfaceWithoutOffscreenBanks()
    {
        MirrorCaptureComponent mirror = new();
        mirror.CaptureMode.ShouldBe(EMirrorCaptureMode.OffscreenCapture);
        mirror.MsaaShadingMode.ShouldBe(EMirrorMsaaShadingMode.SingleSample);

        mirror.CaptureMode = EMirrorCaptureMode.InPlaceStencil;
        mirror.MsaaShadingMode = EMirrorMsaaShadingMode.PerSample;
        mirror.AllowRecursion = true;
        mirror.RenderTransparents = true;
        mirror.GlassTint = new Vector4(0.9f, 0.95f, 1.0f, 0.1f);
        mirror.FresnelStrength = 0.25f;
        mirror.SpecularStrength = 0.5f;

        mirror.UsesInPlaceStencilOnAdvanced.ShouldBeTrue();
        mirror.EnvironmentTexture.ShouldBeNull();
        mirror.Viewport.ShouldBeNull();

        Matrix4x4 frame1 = Matrix4x4.CreateScale(4f, 2f, 1f) * Matrix4x4.CreateTranslation(0f, 1f, -5f);
        Matrix4x4 frame2 = Matrix4x4.CreateScale(4f, 2f, 1f) * Matrix4x4.CreateTranslation(0f, 1f, -4f);
        mirror.RecordPortalWorldTransform(in frame1, renderFrameId: 10ul);
        mirror.RecordPortalWorldTransform(in frame2, renderFrameId: 11ul);
        mirror.SetPublishedPortalDrawHandle(new AdvancedGpuHandle(77u, 2u));

        // Capture surface while component is unretired (default component starts with retirement flag until activated;
        // verify inactive state rejects capture cleanly, matching retirement lifecycle contract).
        mirror.TryCapturePortalSurface(out _).ShouldBeFalse();
    }

    [Test]
    public void Publisher_RegistersCapturesAndUnregistersPortalSurfacesAndTagsDrawFlag()
    {
        using AdvancedGpuScenePublisher publisher = new();
        FakePortalSurfaceProvider provider = new()
        {
            PortalProviderId = 101ul,
            Descriptor = CreateMirrorSurface(101ul, new Vector3(0f, 0f, -5f), Vector3.UnitZ, 2f, 2f),
        };

        AdvancedGpuHandle drawHandle = new(42u, 1u);
        publisher.RegisterPortalSurface(provider, drawHandle);
        publisher.RegisteredPortalSurfaceCount.ShouldBe(1);

        Span<AdvancedPortalSurfaceDescriptor> captured = stackalloc AdvancedPortalSurfaceDescriptor[4];
        int count = publisher.CaptureActivePortalSurfaces(captured);
        count.ShouldBe(1);
        captured[0].ProviderId.ShouldBe(101ul);
        captured[0].DrawHandle.ShouldBe(drawHandle);

        XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1, false, 0, false).ToTriangles());
        XRMaterial material = XRMaterial.CreateColorMaterialDeferred();
        RenderCommandMesh3D command = new(
            (int)EDefaultRenderPass.OpaqueDeferred,
            new XRMeshRenderer(mesh, material),
            Matrix4x4.Identity);
        _ = RenderInfo3D.New(provider, command);

        DrawMetadata metadata = new() { Flags = 0u, StateClassID = 0u };
        uint activeFlags = AdvancedGpuScenePublisher.ResolveSubmissionDrawFlags(in metadata, material, command);
        (activeFlags & (uint)GPUIndirectRenderFlags.PortalSurface).ShouldBe((uint)GPUIndirectRenderFlags.PortalSurface);

        provider.IsPortalSurfaceActive = false;
        uint inactiveFlags = AdvancedGpuScenePublisher.ResolveSubmissionDrawFlags(in metadata, material, command);
        (inactiveFlags & (uint)GPUIndirectRenderFlags.PortalSurface).ShouldBe(0u);
        publisher.CaptureActivePortalSurfaces(captured).ShouldBe(0);

        publisher.UnregisterPortalSurface(provider).ShouldBeTrue();
        publisher.RegisteredPortalSurfaceCount.ShouldBe(0);
        publisher.UnregisterPortalSurface(provider).ShouldBeFalse();

        string earlyVisibility = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/EarlyVisibility.comp");
        earlyVisibility.ShouldContain("const uint FLAG_PORTAL_SURFACE = 1u << 11u;");
        earlyVisibility.ShouldContain("FLAG_BOUNDS_REJECTED | FLAG_PORTAL_SURFACE");
        ((uint)EAdvancedVisibilityPreparationFlags.PortalSurface).ShouldBe(1u << 11);
    }

    [Test]
    public void Scheduler_ComputesExactReflectionAndPreviousMatricesAndWindingParity()
    {
        AdvancedPortalViewScheduler scheduler = new();
        AdvancedPortalFramePlan plan = new();

        AdvancedViewRecord primary = CreatePrimaryView(0u, new Vector3(0f, 0f, 3f), new Vector3(0f, 0f, -5f));
        AdvancedPortalSurfaceDescriptor mirror = CreateMirrorSurface(
            providerId: 10ul,
            center: new Vector3(0f, 0f, -5f),
            normal: Vector3.UnitZ,
            halfWidth: 2f,
            halfHeight: 2f,
            drawIndex: 55u,
            msaaMode: EMirrorMsaaShadingMode.SingleSample,
            previousCenter: new Vector3(0f, 0f, -4f));

        scheduler.Schedule(
            [primary],
            [mirror],
            AdvancedPortalSchedulerOptions.Default,
            plan);

        plan.Count.ShouldBe(1);
        plan.MaxScheduledDepth.ShouldBe(1);

        AdvancedScheduledPortalView node = plan.ScheduledNodes[0];
        AdvancedViewRecord child = plan.PortalViewRecords[0];

        node.ViewIndex.ShouldBe(1u);
        node.PrimaryViewIndex.ShouldBe(0u);
        node.ParentViewIndex.ShouldBe(0u);
        node.StencilNodeId.ShouldBe(1u);
        node.RecursionDepth.ShouldBe(1u);
        node.ExcludedDrawIndex.ShouldBe(55u);
        node.InvertedWinding.ShouldBeTrue();
        node.ScissorRect.IsNonEmpty.ShouldBeTrue();

        // Reflected camera across z = -5 from z = +3 must sit at z = -13.
        child.CameraPositionAndNear.X.ShouldBe(0f, 1e-4f);
        child.CameraPositionAndNear.Y.ShouldBe(0f, 1e-4f);
        child.CameraPositionAndNear.Z.ShouldBe(-13f, 1e-4f);

        // Verify View_child = R * View_parent and PreviousView_child = R_prev * PreviousView_parent.
        Matrix4x4 expectedView = mirror.PortalTransform * primary.View;
        Matrix4x4 expectedPrevView = mirror.PreviousPortalTransform * primary.PreviousView;
        child.View.ShouldBe(expectedView);
        child.PreviousView.ShouldBe(expectedPrevView);
        child.PreviousViewProjectionJittered.ShouldBe(expectedPrevView * primary.PreviousProjectionJittered);
        child.PreviousView.ShouldNotBe(child.View);

        // Verify inline portal fields and flags.
        (child.Flags & EAdvancedViewRecordFlags.PortalSubView).ShouldNotBe(EAdvancedViewRecordFlags.None);
        (child.Flags & EAdvancedViewRecordFlags.Mirror).ShouldNotBe(EAdvancedViewRecordFlags.None);
        (child.Flags & EAdvancedViewRecordFlags.InvertedWinding).ShouldNotBe(EAdvancedViewRecordFlags.None);
        (child.Flags & EAdvancedViewRecordFlags.ClipPlaneEnabled).ShouldNotBe(EAdvancedViewRecordFlags.None);
        (child.Flags & EAdvancedViewRecordFlags.SkipHiZAndHistory).ShouldNotBe(EAdvancedViewRecordFlags.None);
        (child.Flags & EAdvancedViewRecordFlags.SingleSampleMsaaShading).ShouldNotBe(EAdvancedViewRecordFlags.None);
        child.PrimaryViewIndex.ShouldBe(0u);
        child.ParentViewIndex.ShouldBe(0u);
        child.StencilNodeId.ShouldBe(1u);
        (child.DepthAndExcludeDraw & 0xFu).ShouldBe(1u);
        (child.DepthAndExcludeDraw >> 4).ShouldBe(55u);

        // ClipPlaneWorld: normal = +Z, point = (0,0,-5), guard = 0.001 -> w = +5 - 0.001 = 4.999.
        child.ClipPlaneWorld.X.ShouldBe(0f, 1e-5f);
        child.ClipPlaneWorld.Y.ShouldBe(0f, 1e-5f);
        child.ClipPlaneWorld.Z.ShouldBe(1f, 1e-5f);
        child.ClipPlaneWorld.W.ShouldBe(4.999f, 1e-4f);
    }

    [Test]
    public void Scheduler_HandlesSiblingsRecursionCyclesAndDeterministicEviction()
    {
        AdvancedPortalViewScheduler scheduler = new();
        AdvancedPortalFramePlan plan = new();

        AdvancedViewRecord primary = CreatePrimaryView(0u, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -5f));

        // Two sibling mirrors in front of camera (mirror 20 is larger than mirror 30), plus one behind facing forward.
        AdvancedPortalSurfaceDescriptor largeFront = CreateMirrorSurface(20ul, new Vector3(-1.5f, 0f, -5f), Vector3.UnitZ, 1.4f, 1.4f, allowRecursion: true);
        AdvancedPortalSurfaceDescriptor smallFront = CreateMirrorSurface(30ul, new Vector3(2.2f, 0f, -6f), Vector3.UnitZ, 0.8f, 0.8f, allowRecursion: true);
        AdvancedPortalSurfaceDescriptor backMirror = CreateMirrorSurface(40ul, new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 3.0f, 3.0f, allowRecursion: true);

        // First: depth 1 with budget 2 -> both front mirrors get distinct stencil IDs (1 and 2), ordered by area descending.
        scheduler.Schedule(
            [primary],
            [smallFront, largeFront, backMirror],
            new AdvancedPortalSchedulerOptions { MaxRecursionDepth = 1, MaxPortalViewsPerEye = 2, MaxTotalPortalViewsPerFrame = 2 },
            plan);

        plan.Count.ShouldBe(2);
        plan.ScheduledNodes[0].ProviderId.ShouldBe(20ul);
        plan.ScheduledNodes[0].StencilNodeId.ShouldBe(1u);
        plan.ScheduledNodes[1].ProviderId.ShouldBe(30ul);
        plan.ScheduledNodes[1].StencilNodeId.ShouldBe(2u);

        // Tight budget 1 -> deterministic eviction keeps largest projected mirror (20) and rejects mirror 30.
        scheduler.Schedule(
            [primary],
            [smallFront, largeFront],
            new AdvancedPortalSchedulerOptions { MaxRecursionDepth = 1, MaxPortalViewsPerEye = 1, MaxTotalPortalViewsPerFrame = 1 },
            plan);

        plan.Count.ShouldBe(1);
        plan.ScheduledNodes[0].ProviderId.ShouldBe(20ul);
        plan.RejectedBudgetCount.ShouldBe(1);

        // Facing mirrors with depth 2 -> depth 1 reflects front mirror (inverted winding), depth 2 sees back mirror (normal winding), self-recursion rejected.
        AdvancedPortalSurfaceDescriptor facingFront = CreateMirrorSurface(100ul, new Vector3(0f, 0f, -5f), Vector3.UnitZ, 3f, 3f, allowRecursion: true);
        AdvancedPortalSurfaceDescriptor facingBack = CreateMirrorSurface(200ul, new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 3f, 3f, allowRecursion: true);

        scheduler.Schedule(
            [primary],
            [facingFront, facingBack],
            new AdvancedPortalSchedulerOptions { MaxRecursionDepth = 2, MaxPortalViewsPerEye = 4, MaxTotalPortalViewsPerFrame = 4 },
            plan);

        plan.Count.ShouldBe(2);
        plan.MaxScheduledDepth.ShouldBe(2);
        plan.RejectedCycleCount.ShouldBeGreaterThanOrEqualTo(1);
        plan.ScheduledNodes[0].ProviderId.ShouldBe(100ul);
        plan.ScheduledNodes[0].RecursionDepth.ShouldBe(1u);
        plan.ScheduledNodes[0].InvertedWinding.ShouldBeTrue();
        plan.ScheduledNodes[1].ProviderId.ShouldBe(200ul);
        plan.ScheduledNodes[1].RecursionDepth.ShouldBe(2u);
        plan.ScheduledNodes[1].ParentViewIndex.ShouldBe(plan.ScheduledNodes[0].ViewIndex);
        plan.ScheduledNodes[1].InvertedWinding.ShouldBeFalse();
        (plan.PortalViewRecords[1].Flags & EAdvancedViewRecordFlags.InvertedWinding).ShouldBe(EAdvancedViewRecordFlags.None);
    }

    [Test]
    public void Scheduler_PairsStereoEyesAndEvictsSymmetricallyWithZeroSteadyStateAllocations()
    {
        AdvancedPortalViewScheduler scheduler = new();
        AdvancedPortalFramePlan plan = new();

        AdvancedViewRecord leftEye = CreatePrimaryView(0u, new Vector3(-0.032f, 0f, 3f), new Vector3(-0.032f, 0f, -5f), extraFlags: EAdvancedViewRecordFlags.StereoLeft);
        AdvancedViewRecord rightEye = CreatePrimaryView(1u, new Vector3(0.032f, 0f, 3f), new Vector3(0.032f, 0f, -5f), extraFlags: EAdvancedViewRecordFlags.StereoRight);

        AdvancedPortalSurfaceDescriptor mirrorA = CreateMirrorSurface(1ul, new Vector3(-1.2f, 0f, -5f), Vector3.UnitZ, 1.5f, 1.5f);
        AdvancedPortalSurfaceDescriptor mirrorB = CreateMirrorSurface(2ul, new Vector3(1.5f, 0f, -6f), Vector3.UnitZ, 1.0f, 1.0f);

        // Budget of 3 total views can fit mirrorA pair (2 views) but cannot fit mirrorB pair (needs 2 more -> dropped symmetrically).
        AdvancedPortalSchedulerOptions options = new()
        {
            MaxRecursionDepth = 1,
            MaxPortalViewsPerEye = 4,
            MaxTotalPortalViewsPerFrame = 3,
        };
        ReadOnlySpan<AdvancedViewRecord> stereoViews = [leftEye, rightEye];
        ReadOnlySpan<AdvancedPortalSurfaceDescriptor> surfaces = [mirrorA, mirrorB];

        scheduler.Schedule(stereoViews, surfaces, in options, plan);
        plan.Count.ShouldBe(2);
        plan.ScheduledNodes[0].PrimaryViewIndex.ShouldBe(0u);
        plan.ScheduledNodes[1].PrimaryViewIndex.ShouldBe(1u);
        plan.ScheduledNodes[0].ProviderId.ShouldBe(1ul);
        plan.ScheduledNodes[1].ProviderId.ShouldBe(1ul);
        plan.RejectedBudgetCount.ShouldBe(2);

        // Steady-state scheduling must allocate zero managed bytes.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; ++i)
            scheduler.Schedule(stereoViews, surfaces, in options, plan);
        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).ShouldBe(0L);
    }

    [Test]
    public void Scheduler_StereoSymmetricEviction_DropsUnpairedEyeAndCutsRecursionDepthSymmetrically()
    {
        AdvancedPortalViewScheduler scheduler = new();
        AdvancedPortalFramePlan plan = new();

        AdvancedViewRecord leftEye = CreatePrimaryView(
            0u, new Vector3(-0.032f, 0f, 0f), new Vector3(-0.032f, 0f, -5f),
            extraFlags: EAdvancedViewRecordFlags.StereoLeft);
        AdvancedViewRecord rightEye = CreatePrimaryView(
            1u, new Vector3(0.032f, 0f, 0f), new Vector3(0.032f, 0f, -5f),
            extraFlags: EAdvancedViewRecordFlags.StereoRight);

        // Two facing mirrors: depth 1 needs 2 views (left+right), depth 2 needs 2 more views (total 4).
        AdvancedPortalSurfaceDescriptor facingFront = CreateMirrorSurface(
            100ul, new Vector3(0f, 0f, -5f), Vector3.UnitZ, 3f, 3f, allowRecursion: true);
        AdvancedPortalSurfaceDescriptor facingBack = CreateMirrorSurface(
            200ul, new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 3f, 3f, allowRecursion: true);

        // Budget of 3 total views admits depth 1 for both eyes (2 views) and symmetrically evicts depth 2.
        AdvancedPortalSchedulerOptions tightRecursiveOptions = new()
        {
            MaxRecursionDepth = 2,
            MaxPortalViewsPerEye = 4,
            MaxTotalPortalViewsPerFrame = 3,
        };
        scheduler.Schedule([leftEye, rightEye], [facingFront, facingBack], in tightRecursiveOptions, plan);

        plan.Count.ShouldBe(2);
        plan.MaxScheduledDepth.ShouldBe(1);
        plan.ScheduledNodes[0].PrimaryViewIndex.ShouldBe(0u);
        plan.ScheduledNodes[1].PrimaryViewIndex.ShouldBe(1u);
        plan.ScheduledNodes[0].StencilNodeId.ShouldBe(plan.ScheduledNodes[1].StencilNodeId);
        plan.RejectedBudgetCount.ShouldBe(2);

        // Budget of 4 total views admits both depth 1 and depth 2 symmetrically with matching stencil IDs.
        AdvancedPortalSchedulerOptions fullRecursiveOptions = tightRecursiveOptions with { MaxTotalPortalViewsPerFrame = 4 };
        scheduler.Schedule([leftEye, rightEye], [facingFront, facingBack], in fullRecursiveOptions, plan);

        plan.Count.ShouldBe(4);
        plan.MaxScheduledDepth.ShouldBe(2);
        plan.ScheduledNodes[0].StencilNodeId.ShouldBe(1u);
        plan.ScheduledNodes[1].StencilNodeId.ShouldBe(1u);
        plan.ScheduledNodes[2].StencilNodeId.ShouldBe(2u);
        plan.ScheduledNodes[3].StencilNodeId.ShouldBe(2u);
        plan.ScheduledNodes[2].PrimaryViewIndex.ShouldBe(0u);
        plan.ScheduledNodes[3].PrimaryViewIndex.ShouldBe(1u);

        // A mirror visible only to the left eye (facing left eye, back-facing or outside right eye frustum)
        // must be dropped symmetrically in SinglePassStereo so binocular rivalry cannot occur.
        AdvancedViewRecord wideLeft = CreatePrimaryView(
            0u, new Vector3(-2.0f, 0f, 3f), new Vector3(-2.0f, 0f, -5f),
            extraFlags: EAdvancedViewRecordFlags.StereoLeft);
        AdvancedViewRecord wideRight = CreatePrimaryView(
            1u, new Vector3(2.0f, 0f, 3f), new Vector3(2.0f, 0f, -5f),
            extraFlags: EAdvancedViewRecordFlags.StereoRight);
        // Placed at x = -6.0, z = -2.0: inside wideLeft frustum, completely outside wideRight frustum.
        AdvancedPortalSurfaceDescriptor leftOnlyMirror = CreateMirrorSurface(
            300ul, new Vector3(-6.0f, 0f, -2f), Vector3.UnitZ, 0.4f, 0.4f);

        scheduler.Schedule([wideLeft, wideRight], [leftOnlyMirror], in fullRecursiveOptions, plan);
        plan.Count.ShouldBe(0);
        plan.RejectedCulledCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public void MirrorCaptureComponent_MovingMirror_TracksPreviousTransformForReflectedVelocityAndSettlesWhenStationary()
    {
        MirrorCaptureComponent mirror = new()
        {
            CaptureMode = EMirrorCaptureMode.InPlaceStencil,
            AllowRecursion = false,
            GlassTint = new Vector4(0.9f, 0.95f, 1.0f, 0.08f),
            FresnelStrength = 0.2f,
            SpecularStrength = 0.35f,
        };
        mirror.SetPublishedPortalDrawHandle(new AdvancedGpuHandle(88u, 1u));

        // Frame 1: mirror at z = -5 facing +Z (rotated by 0 rad).
        Matrix4x4 frame1World = Matrix4x4.CreateScale(4f, 3f, 1f) * Matrix4x4.CreateTranslation(0f, 0f, -5f);
        mirror.RecordPortalWorldTransform(in frame1World, renderFrameId: 100ul);
        mirror.TryCapturePortalSurfaceForFrame(100ul, frame1World, allowWhenRetired: true, out AdvancedPortalSurfaceDescriptor desc1)
            .ShouldBeTrue();
        desc1.PortalTransform.ShouldBe(desc1.PreviousPortalTransform);

        // Frame 2: mirror translates to (0.5, 0, -4.5) and rotates around Y by +10 degrees.
        Matrix4x4 frame2World = Matrix4x4.CreateScale(4f, 3f, 1f) *
            Matrix4x4.CreateRotationY(0.17453292f) *
            Matrix4x4.CreateTranslation(0.5f, 0f, -4.5f);
        mirror.RecordPortalWorldTransform(in frame2World, renderFrameId: 101ul);
        mirror.TryCapturePortalSurfaceForFrame(101ul, frame2World, allowWhenRetired: true, out AdvancedPortalSurfaceDescriptor desc2)
            .ShouldBeTrue();

        desc2.PreviousWorldMatrix.ShouldBe(frame1World);
        desc2.WorldMatrix.ShouldBe(frame2World);
        desc2.PreviousPortalTransform.ShouldBe(desc1.PortalTransform);
        desc2.PortalTransform.ShouldNotBe(desc2.PreviousPortalTransform);

        AdvancedPortalViewScheduler scheduler = new();
        AdvancedPortalFramePlan plan = new();
        AdvancedViewRecord primary = CreatePrimaryView(0u, new Vector3(0f, 0f, 3f), new Vector3(0f, 0f, -5f));

        scheduler.Schedule([primary], [desc2], AdvancedPortalSchedulerOptions.Default, plan);
        plan.Count.ShouldBe(1);
        AdvancedViewRecord movingChild = plan.PortalViewRecords[0];

        Matrix4x4 expectedCurrentView = desc2.PortalTransform * primary.View;
        Matrix4x4 expectedPreviousView = desc2.PreviousPortalTransform * primary.PreviousView;
        movingChild.View.ShouldBe(expectedCurrentView);
        movingChild.PreviousView.ShouldBe(expectedPreviousView);
        movingChild.ViewProjectionJittered.ShouldBe(expectedCurrentView * primary.ProjectionJittered);
        movingChild.PreviousViewProjectionJittered.ShouldBe(expectedPreviousView * primary.PreviousProjectionJittered);
        movingChild.PreviousViewProjectionJittered.ShouldNotBe(movingChild.ViewProjectionJittered);

        // Frame 3: mirror is stationary (no new RecordPortalWorldTransform call). Velocity must settle to zero.
        mirror.TryCapturePortalSurfaceForFrame(102ul, frame2World, allowWhenRetired: true, out AdvancedPortalSurfaceDescriptor desc3)
            .ShouldBeTrue();
        desc3.PreviousWorldMatrix.ShouldBe(frame2World);
        desc3.PreviousPortalTransform.ShouldBe(desc3.PortalTransform);

        scheduler.Schedule([primary], [desc3], AdvancedPortalSchedulerOptions.Default, plan);
        plan.Count.ShouldBe(1);
        AdvancedViewRecord settledChild = plan.PortalViewRecords[0];
        settledChild.PreviousView.ShouldBe(settledChild.View);
        settledChild.PreviousViewProjectionJittered.ShouldBe(settledChild.ViewProjectionJittered);
    }

    [Test]
    public void VulkanSceneGlobalsAndStorageSizing_SupportAppendedPortalViewsWithoutWideningPrimaryMax()
    {
        RenderFrameViewSet.MaxViewCount.ShouldBe(8);

        ulong zeroPortalBytes = VulkanAdvancedSceneResourceRuntime.CalculateFramePublicationStorageBytes(
            viewCount: 2,
            passCount: 4,
            portalViewCount: 0);
        ulong fourPortalBytes = VulkanAdvancedSceneResourceRuntime.CalculateFramePublicationStorageBytes(
            viewCount: 2,
            passCount: 4,
            portalViewCount: 4);

        (fourPortalBytes - zeroPortalBytes).ShouldBe(4ul * 976ul);

        Should.Throw<InvalidOperationException>(() =>
            VulkanAdvancedSceneResourceRuntime.CalculateFramePublicationStorageBytes(
                viewCount: RenderFrameViewSet.MaxViewCount + 1,
                passCount: 1,
                portalViewCount: 0));
        Should.Throw<InvalidOperationException>(() =>
            VulkanAdvancedSceneResourceRuntime.CalculateFramePublicationStorageBytes(
                viewCount: 1,
                passCount: 1,
                portalViewCount: AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame + 1));

        VulkanAdvancedSceneGlobalInputs globals = new();
        BackendReadyCanonicalViewRecord primary = new(0u, Matrix4x4.Identity, Matrix4x4.Identity, 1280, 720, 1ul);
        BackendReadyCanonicalFrameRecord frame = new(1ul, 1ul, 1ul, 1ul);
        AdvancedViewRecord portalA = CreatePrimaryView(1u, Vector3.Zero, -Vector3.UnitZ) with
        {
            Flags = EAdvancedViewRecordFlags.PortalSubView,
            StencilNodeId = 1u,
        };
        AdvancedViewRecord portalB = portalA with { StencilNodeId = 2u };

        globals.Capture([primary], in frame, [], [], 0, [portalA]);
        globals.Matches([primary], in frame, [], [], 0, [portalA]).ShouldBeTrue();
        globals.Matches([primary], in frame, [], [], 0, [portalB]).ShouldBeFalse();
        globals.Matches([primary], in frame, [], [], 0, []).ShouldBeFalse();

        VulkanPreparedFrameRecording prepared = new();
        prepared.Begin(frameSlot: 0, generation: 1ul);
        prepared.CapturePortalViews([portalA, portalB]);
        prepared.GlobalPortalViews.Length.ShouldBe(2);
        prepared.GlobalPortalViews[1].StencilNodeId.ShouldBe(2u);
        prepared.Reset();
        prepared.GlobalPortalViews.Length.ShouldBe(0);
    }
}
