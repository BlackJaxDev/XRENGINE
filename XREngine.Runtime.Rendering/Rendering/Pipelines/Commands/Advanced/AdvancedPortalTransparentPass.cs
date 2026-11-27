using System;
using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Renders opt-in reflected transparent meshes for scheduled in-place stencil portal mirrors
/// before mirror glass compositing unwinds stencil bits 2..6. Uses preallocated camera and
/// transform instances so frames with active transparent reflections allocate no heap memory.
/// </summary>
internal sealed class AdvancedPortalTransparentPass
{
    private readonly DrivenWorldTransform _scratchTransform = new();
    private readonly DrivenWorldTransform _scratchRightTransform = new();
    private readonly Predicate<RenderCommand> _layerFilter;
    private XRCamera? _scratchCamera;
    private XRCamera? _scratchRightCamera;
    private uint _activeCullingLayerMask = uint.MaxValue;

    internal AdvancedPortalTransparentPass() => _layerFilter = MatchesActiveCullingLayerMask;

    /// <summary>
    /// Returns true when at least one scheduled portal sub-view opts into reflected transparents
    /// and the frame collected transparent forward commands.
    /// </summary>
    internal static bool HasReflectedTransparents(
        AdvancedPortalFramePlan portalPlan,
        RenderCommandCollection commands)
    {
        if (portalPlan.Count <= 0 ||
            !commands.HasRenderingCommands((int)EDefaultRenderPass.TransparentForward))
            return false;

        ReadOnlySpan<AdvancedScheduledPortalView> nodes = portalPlan.ScheduledNodes;
        for (int index = 0; index < nodes.Length; ++index)
        {
            if (nodes[index].RenderTransparents)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Executes deepest-out reflected transparent draws into <paramref name="forwardTarget"/>
    /// masked to each opting-in portal node's stencil ID, oblique clip plane, culling layer mask,
    /// and winding state.
    /// </summary>
    internal void Execute(
        XRRenderPipelineInstance instance,
        XRFrameBuffer forwardTarget,
        AdvancedPortalFramePlan portalPlan,
        string renderGraphPassName)
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        XRCamera? sourceCamera = instance.RenderState.SceneCamera
            ?? instance.RenderState.RenderingCamera
            ?? instance.LastSceneCamera
            ?? instance.LastRenderingCamera;
        if (sourceCamera is null)
            return;

        ReadOnlySpan<AdvancedScheduledPortalView> nodes = portalPlan.ScheduledNodes;
        ReadOnlySpan<AdvancedViewRecord> portalViews = portalPlan.PortalViewRecords;
        if (nodes.Length == 0 || portalViews.Length < nodes.Length)
            return;

        int passIndex = instance.TryGetActiveRenderPassIndex(renderGraphPassName, out int resolvedPassIndex)
            ? resolvedPassIndex
            : (int)EDefaultRenderPass.TransparentForward;

        XRCamera portalCamera = _scratchCamera ??= new XRCamera(_scratchTransform, sourceCamera.Parameters);
        portalCamera.DepthMode = sourceCamera.DepthMode;
        if (sourceCamera.Parameters is not null)
            portalCamera.Parameters = sourceCamera.Parameters;

        bool isStereoMultiview = instance.RenderState.StereoPass &&
            portalPlan.PrimaryViewCount == 2 &&
            instance.RenderState.StereoRightEyeCamera is not null;
        XRCamera? sourceRightCamera = isStereoMultiview ? instance.RenderState.StereoRightEyeCamera : null;
        XRCamera? portalRightCamera = null;
        if (isStereoMultiview && sourceRightCamera is not null)
        {
            portalRightCamera = _scratchRightCamera ??= new XRCamera(_scratchRightTransform, sourceRightCamera.Parameters);
            portalRightCamera.DepthMode = sourceRightCamera.DepthMode;
            if (sourceRightCamera.Parameters is not null)
                portalRightCamera.Parameters = sourceRightCamera.Parameters;
        }

        // Apply the active temporal projection jitter to reflected transparent cameras so
        // reflected transparent meshes align with jittered reflected opaque depth under TSR.
        using StateObject? leftJitterScope = sourceCamera.HasProjectionJitter
            ? portalCamera.PushProjectionJitter(ProjectionJitterRequest.ClipSpace(sourceCamera.ProjectionJitter))
            : null;
        using StateObject? rightJitterScope = portalRightCamera is not null && sourceRightCamera is { HasProjectionJitter: true }
            ? portalRightCamera.PushProjectionJitter(ProjectionJitterRequest.ClipSpace(sourceRightCamera.ProjectionJitter))
            : null;

        XRFrameBuffer? previousDrawFbo = XRFrameBuffer.BoundForWriting;
        bool previousReverseCulling = RuntimeEngine.Rendering.State.ReverseCulling;

        using IDisposable? passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(
            passIndex, (int)EDefaultRenderPass.TransparentForward);

        renderer.BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, forwardTarget);
        try
        {
            RuntimeEngine.Rendering.State.ColorMask(true, true, true, true);
            RuntimeEngine.Rendering.State.EnableDepthTest(true);
            RuntimeEngine.Rendering.State.AllowDepthWrite(false);
            RuntimeEngine.Rendering.State.DepthFunc(EComparison.Lequal);
            RuntimeEngine.Rendering.State.EnableStencilTest(true);
            RuntimeEngine.Rendering.State.StencilMask(0u);
            RuntimeEngine.Rendering.State.StencilOp(EStencilOp.Keep, EStencilOp.Keep, EStencilOp.Keep);

            for (int depth = portalPlan.MaxScheduledDepth; depth >= 1; --depth)
            {
                for (int nodeIndex = 0; nodeIndex < nodes.Length; ++nodeIndex)
                {
                    ref readonly AdvancedScheduledPortalView node = ref nodes[nodeIndex];
                    if (node.RecursionDepth != (uint)depth || !node.RenderTransparents)
                        continue;
                    if (node.ScissorRect.Width <= 0 || node.ScissorRect.Height <= 0)
                        continue;

                    if (isStereoMultiview)
                    {
                        // Stereo multiview draws both eyes in one pass; drive each mirror once
                        // from its left-eye node paired with its matching right-eye node.
                        if (node.PrimaryViewIndex != 0u)
                            continue;

                        int rightNodeIndex = FindStereoPartnerIndex(nodes, nodeIndex, in node);
                        if (rightNodeIndex < 0 || portalRightCamera is null)
                            continue;

                        ref readonly AdvancedViewRecord leftViewRecord = ref portalViews[nodeIndex];
                        ref readonly AdvancedViewRecord rightViewRecord = ref portalViews[rightNodeIndex];
                        if (!TryConfigurePortalCamera(portalCamera, _scratchTransform, in leftViewRecord) ||
                            !TryConfigurePortalCamera(portalRightCamera, _scratchRightTransform, in rightViewRecord))
                            continue;

                        ExecuteScopedTransparentDraw(
                            instance,
                            in node,
                            portalCamera,
                            portalRightCamera);
                        continue;
                    }

                    ref readonly AdvancedViewRecord viewRecord = ref portalViews[nodeIndex];
                    if (!TryConfigurePortalCamera(portalCamera, _scratchTransform, in viewRecord))
                        continue;

                    ExecuteScopedTransparentDraw(
                        instance,
                        in node,
                        portalCamera,
                        portalRightCamera: null);
                }
            }
        }
        finally
        {
            portalCamera.ClearObliqueClippingPlane();
            portalRightCamera?.ClearObliqueClippingPlane();
            _activeCullingLayerMask = uint.MaxValue;
            RuntimeEngine.Rendering.State.ReverseCulling = previousReverseCulling;
            RuntimeEngine.Rendering.State.EnableStencilTest(false);
            RuntimeEngine.Rendering.State.StencilMask(0xFFu);
            RuntimeEngine.Rendering.State.AllowDepthWrite(true);
            renderer.BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, previousDrawFbo);
        }
    }

    private void ExecuteScopedTransparentDraw(
        XRRenderPipelineInstance instance,
        in AdvancedScheduledPortalView node,
        XRCamera portalCamera,
        XRCamera? portalRightCamera)
    {
        RuntimeEngine.Rendering.State.StencilFunc(
            EComparison.Equal,
            (int)AdvancedStencilBitLayout.EncodePortalNode(node.StencilNodeId),
            AdvancedStencilBitLayout.PortalNodeMask);
        RuntimeEngine.Rendering.State.ReverseCulling = node.InvertedWinding;
        _activeCullingLayerMask = node.Surface.CullingLayerMask;

        // RenderingCameraOverride is a thread-local stack: every non-null push must be
        // paired with a null pop so later passes (such as stereo skybox) read the real camera.
        XRCamera? previousRightCamera = null;
        bool replacedRightCamera = false;
        RuntimeEngine.Rendering.State.RenderingCameraOverride = portalCamera;
        try
        {
            if (portalRightCamera is not null)
            {
                previousRightCamera = instance.RenderState.ReplaceStereoRightEyeCamera(portalRightCamera);
                replacedRightCamera = true;
            }

            instance.ActiveMeshRenderCommands.RenderCPU(
                (int)EDefaultRenderPass.TransparentForward,
                false,
                portalCamera,
                enforceAdvancedLatePassEligibility: true,
                commandFilter: _layerFilter);
        }
        finally
        {
            if (replacedRightCamera)
                _ = instance.RenderState.ReplaceStereoRightEyeCamera(previousRightCamera);
            RuntimeEngine.Rendering.State.RenderingCameraOverride = null;
        }
    }

    private bool MatchesActiveCullingLayerMask(RenderCommand command)
    {
        if (_activeCullingLayerMask == uint.MaxValue)
            return true;

        int layer = command.OwnerRenderInfo is RenderInfo3D info3D ? info3D.Layer : 0;
        return (_activeCullingLayerMask & (1u << Math.Clamp(layer, 0, 31))) != 0u;
    }

    private static bool TryConfigurePortalCamera(
        XRCamera portalCamera,
        DrivenWorldTransform scratchTransform,
        in AdvancedViewRecord viewRecord)
    {
        if (!Matrix4x4.Invert(viewRecord.View, out Matrix4x4 reflectedWorld))
            return false;

        scratchTransform.SetWorldMatrix(
            reflectedWorld,
            setRenderMatrixNow: true,
            childRecalcType: ELoopType.Sequential);

        // Clip reflected transparents behind the portal plane in world space.
        Vector4 clipPlane = viewRecord.ClipPlaneWorld;
        Vector3 clipNormal = new(clipPlane.X, clipPlane.Y, clipPlane.Z);
        if (clipNormal.LengthSquared() > 1.0e-6f)
        {
            try
            {
                portalCamera.SetObliqueClippingPlane(new Plane(clipNormal, clipPlane.W));
            }
            catch (ArgumentException)
            {
                portalCamera.ClearObliqueClippingPlane();
            }
        }
        else
        {
            portalCamera.ClearObliqueClippingPlane();
        }

        return true;
    }

    private static int FindStereoPartnerIndex(
        ReadOnlySpan<AdvancedScheduledPortalView> nodes,
        int leftIndex,
        in AdvancedScheduledPortalView leftNode)
    {
        for (int index = 0; index < nodes.Length; ++index)
        {
            if (index == leftIndex)
                continue;

            ref readonly AdvancedScheduledPortalView candidate = ref nodes[index];
            if (candidate.PrimaryViewIndex == 1u &&
                candidate.RecursionDepth == leftNode.RecursionDepth &&
                candidate.ProviderId == leftNode.ProviderId &&
                candidate.ParentStencilNodeId == leftNode.ParentStencilNodeId)
                return index;
        }
        return -1;
    }
}
