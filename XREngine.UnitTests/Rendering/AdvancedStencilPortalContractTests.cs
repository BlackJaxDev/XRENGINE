using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedStencilPortalContractTests
{
    [Test]
    public void StencilBitLayout_MasksAreDisjointAndMatchPipelineConstants()
    {
        (AdvancedStencilBitLayout.HoverBit & AdvancedStencilBitLayout.SelectionBit).ShouldBe(0u);
        (AdvancedStencilBitLayout.EditorHighlightMask & AdvancedStencilBitLayout.PortalNodeMask).ShouldBe(0u);
        (AdvancedStencilBitLayout.EditorHighlightMask & AdvancedStencilBitLayout.GizmoBypassBit).ShouldBe(0u);
        (AdvancedStencilBitLayout.PortalNodeMask & AdvancedStencilBitLayout.GizmoBypassBit).ShouldBe(0u);

        AdvancedStencilBitLayout.EditorHighlightMask.ShouldBe(0x03u);
        AdvancedStencilBitLayout.PortalNodeMask.ShouldBe(0x7Cu);
        AdvancedStencilBitLayout.MaxPortalNodeId.ShouldBe(31u);
        AdvancedStencilBitLayout.GizmoBypassBit.ShouldBe(0x80u);
        AdvancedStencilBitLayout.AllUsedBitsMask.ShouldBe(0xFFu);

        AdvancedRenderPipeline.StencilRefHover.ShouldBe((int)AdvancedStencilBitLayout.HoverBit);
        AdvancedRenderPipeline.StencilRefSelection.ShouldBe((int)AdvancedStencilBitLayout.SelectionBit);
    }

    [Test]
    public void StencilBitLayout_EncodeAndDecodePortalNode_RoundTripAllValidIdsAndRejectOverflow()
    {
        for (uint nodeId = 0u; nodeId <= AdvancedStencilBitLayout.MaxPortalNodeId; ++nodeId)
        {
            uint encoded = AdvancedStencilBitLayout.EncodePortalNode(nodeId);
            (encoded & ~AdvancedStencilBitLayout.PortalNodeMask).ShouldBe(0u);
            AdvancedStencilBitLayout.DecodePortalNode(encoded).ShouldBe(nodeId);

            // Preserve unrelated highlight and gizmo bits during decoding.
            uint combined = encoded | AdvancedStencilBitLayout.EditorHighlightMask | AdvancedStencilBitLayout.GizmoBypassBit;
            AdvancedStencilBitLayout.DecodePortalNode(combined).ShouldBe(nodeId);
        }

        Should.Throw<ArgumentOutOfRangeException>(() =>
            AdvancedStencilBitLayout.EncodePortalNode(AdvancedStencilBitLayout.MaxPortalNodeId + 1u));
    }

    [Test]
    public void ViewRecordPortalFlags_MatchGlslConstantsAndAccessorHelper()
    {
        ((uint)EAdvancedViewRecordFlags.Mirror).ShouldBe(1u << 5);
        ((uint)EAdvancedViewRecordFlags.PortalSubView).ShouldBe(1u << 14);
        ((uint)EAdvancedViewRecordFlags.InvertedWinding).ShouldBe(1u << 15);
        ((uint)EAdvancedViewRecordFlags.ClipPlaneEnabled).ShouldBe(1u << 16);
        ((uint)EAdvancedViewRecordFlags.SkipHiZAndHistory).ShouldBe(1u << 17);
        ((uint)EAdvancedViewRecordFlags.SingleSampleMsaaShading).ShouldBe(1u << 18);

        string viewAccess = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Access/AdvancedViewAccess.glslinc");
        viewAccess.ShouldContain("const uint XR_ADV_VIEW_MIRROR = 1u << 5u;");
        viewAccess.ShouldContain("const uint XR_ADV_VIEW_PORTAL_SUBVIEW = 1u << 14u;");
        viewAccess.ShouldContain("const uint XR_ADV_VIEW_INVERTED_WINDING = 1u << 15u;");
        viewAccess.ShouldContain("const uint XR_ADV_VIEW_CLIP_PLANE_ENABLED = 1u << 16u;");
        viewAccess.ShouldContain("const uint XR_ADV_VIEW_SKIP_HIZ_AND_HISTORY = 1u << 17u;");
        viewAccess.ShouldContain("const uint XR_ADV_VIEW_SINGLE_SAMPLE_MSAA_SHADING = 1u << 18u;");
        viewAccess.ShouldContain("result.clipPlaneWorld = XR_ADV_Views.records[denseIndex].clipPlaneWorld;");
        viewAccess.ShouldContain("result.primaryViewIndex = XR_ADV_Views.records[denseIndex].primaryViewIndex;");
        viewAccess.ShouldContain("result.parentViewIndex = XR_ADV_Views.records[denseIndex].parentViewIndex;");
        viewAccess.ShouldContain("result.stencilNodeId = XR_ADV_Views.records[denseIndex].stencilNodeId;");
        viewAccess.ShouldContain("result.depthAndExcludeDraw = XR_ADV_Views.records[denseIndex].depthAndExcludeDraw;");
        viewAccess.ShouldContain("bool XR_ADV_ViewBelongsToPrimary(uint pixelView, uint primaryView)");
    }

    [Test]
    public void AdvancedViewRecordFactory_ZeroInitializesPortalFieldsForPrimaryViews()
    {
        BackendReadyCanonicalViewRecord canonical = new(
            ViewId: 0u,
            View: Matrix4x4.Identity,
            Projection: Matrix4x4.Identity,
            ViewportWidth: 1280,
            ViewportHeight: 720,
            ViewGeneration: 1ul)
        {
            OutputLayer = 0u,
            HistoryKey = 1ul,
            ProjectionUnjittered = Matrix4x4.Identity,
            ViewProjectionJittered = Matrix4x4.Identity,
            ViewProjectionUnjittered = Matrix4x4.Identity,
            PreviousViewProjectionJittered = Matrix4x4.Identity,
            PreviousViewProjectionUnjittered = Matrix4x4.Identity,
            CameraPositionAndNear = new Vector4(0f, 1.5f, 3f, 0.1f),
            CameraForwardAndFar = new Vector4(0f, 0f, -1f, 100f),
            CurrentAndPreviousJitter = Vector4.Zero,
            DepthParams = new Vector4(0.1f, 100f, 0f, 1f),
            Flags = EAdvancedViewRecordFlags.ReversedDepth,
            ViewMaskLo = 1u,
            ViewMaskHi = 0u,
            FoveationCenterAndBias = Vector4.Zero,
            FoveationRadii = Vector4.Zero,
            SourceCameraIdentity = 42ul,
            CullingLayerMask = uint.MaxValue,
            RenderTimeSeconds = 1.0f,
        };

        AdvancedViewRecord record = AdvancedViewRecordFactory.Create(in canonical);

        Marshal.SizeOf<AdvancedViewRecord>().ShouldBe(976);
        record.ClipPlaneWorld.ShouldBe(Vector4.Zero);
        record.PrimaryViewIndex.ShouldBe(0u);
        record.ParentViewIndex.ShouldBe(0u);
        record.StencilNodeId.ShouldBe(0u);
        record.DepthAndExcludeDraw.ShouldBe(0u);
        (record.Flags & EAdvancedViewRecordFlags.PortalSubView).ShouldBe(EAdvancedViewRecordFlags.None);
    }

    [Test]
    public void VulkanDeviceContext_ShaderClipDistanceValidation_GatesPortalMirrorsDiagnostically()
    {
        VulkanDeviceContext context = new();

        context.ShaderClipDistanceEnabled.ShouldBeFalse();
        context.TryValidateShaderClipDistanceForPortalMirrors(out string? reason).ShouldBeFalse();
        reason.ShouldNotBeNullOrWhiteSpace();
        reason.ShouldContain("shaderClipDistance");

        // Second check still rejects cleanly without throwing.
        context.TryValidateShaderClipDistanceForPortalMirrors(out string? repeatReason).ShouldBeFalse();
        repeatReason.ShouldBe(reason);

        context.ShaderClipDistanceEnabled = true;
        context.TryValidateShaderClipDistanceForPortalMirrors(out string? okReason).ShouldBeTrue();
        okReason.ShouldBeNull();
    }
}
