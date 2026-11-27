using System.Numerics;
using XREngine.Components.Lights;

namespace XREngine.Rendering;

/// <summary>
/// Deterministic, zero-allocation breadth-first scheduler that builds per-eye planar mirror
/// and portal sub-view trees into a caller-owned <see cref="AdvancedPortalFramePlan"/>.
/// </summary>
public sealed class AdvancedPortalViewScheduler
{
    private const int CandidateScratchCapacity = AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame * 8;
    private const float MinEyePlaneDistance = 0.001f;
    private const float MinClipW = 1.0e-5f;

    private readonly PortalCandidate[] _candidates = new PortalCandidate[CandidateScratchCapacity];
    private readonly bool[] _candidateConsumed = new bool[CandidateScratchCapacity];
    private readonly int[] _perEyeScheduledCount = new int[RenderFrameViewSet.MaxViewCount];
    private readonly uint[] _perEyeNextStencilNodeId = new uint[RenderFrameViewSet.MaxViewCount];

    private struct PortalCandidate
    {
        public uint PrimaryViewIndex;
        public uint ParentViewIndex;
        public uint ParentStencilNodeId;
        public ulong ParentProviderId;
        public int SurfaceIndex;
        public ulong ProviderId;
        public float ProjectedArea;
        public AdvancedPortalScissorRect ScissorRect;
        public bool ParentInvertedWinding;
        public AdvancedViewRecord ParentView;
    }

    /// <summary>
    /// Evaluates active portal and mirror surfaces against <paramref name="primaryViews"/> in
    /// breadth-first order and writes the scheduled sub-views into <paramref name="plan"/>.
    /// </summary>
    public void Schedule(
        ReadOnlySpan<AdvancedViewRecord> primaryViews,
        ReadOnlySpan<AdvancedPortalSurfaceDescriptor> surfaces,
        in AdvancedPortalSchedulerOptions rawOptions,
        AdvancedPortalFramePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Reset();

        int primaryCount = Math.Min(primaryViews.Length, RenderFrameViewSet.MaxViewCount);
        plan.PrimaryViewCount = primaryCount;

        AdvancedPortalSchedulerOptions options = rawOptions.ClampToHardLimits();
        if (primaryCount == 0 ||
            surfaces.IsEmpty ||
            options.MaxRecursionDepth <= 0 ||
            options.MaxPortalViewsPerEye <= 0 ||
            options.MaxTotalPortalViewsPerFrame <= 0)
        {
            return;
        }

        Array.Clear(_perEyeScheduledCount, 0, primaryCount);
        for (int eye = 0; eye < primaryCount; ++eye)
            _perEyeNextStencilNodeId[eye] = 1u;

        bool stereoPairMode = IsStereoPair(primaryViews[..primaryCount]);
        int depthStartNodeIndex = 0;
        int depthEndNodeIndex = 0;

        for (int depth = 1; depth <= options.MaxRecursionDepth; ++depth)
        {
            if (plan.Count >= options.MaxTotalPortalViewsPerFrame)
                break;

            int candidateCount = 0;
            if (depth == 1)
            {
                for (int primaryIndex = 0; primaryIndex < primaryCount; ++primaryIndex)
                {
                    ref readonly AdvancedViewRecord primaryView = ref primaryViews[primaryIndex];
                    AdvancedPortalScissorRect fullScissor = BuildFullRenderScissor(in primaryView);
                    if (!fullScissor.IsNonEmpty)
                        continue;

                    bool parentInverted = (primaryView.Flags & EAdvancedViewRecordFlags.InvertedWinding) != 0;
                    DiscoverCandidatesForParent(
                        primaryViewIndex: (uint)primaryIndex,
                        parentViewIndex: (uint)primaryIndex,
                        parentStencilNodeId: 0u,
                        parentProviderId: 0ul,
                        parentAllowsRecursion: true,
                        parentInvertedWinding: parentInverted,
                        in primaryView,
                        in fullScissor,
                        surfaces,
                        depth,
                        in options,
                        plan,
                        ref candidateCount);
                }
            }
            else
            {
                for (int nodeIndex = depthStartNodeIndex; nodeIndex < depthEndNodeIndex; ++nodeIndex)
                {
                    AdvancedScheduledPortalView parentNode = plan.ScheduledNodes[nodeIndex];
                    AdvancedViewRecord parentView = plan.PortalViewRecords[nodeIndex];
                    AdvancedPortalScissorRect parentScissor = parentNode.ScissorRect;
                    DiscoverCandidatesForParent(
                        primaryViewIndex: parentNode.PrimaryViewIndex,
                        parentViewIndex: parentNode.ViewIndex,
                        parentStencilNodeId: parentNode.StencilNodeId,
                        parentProviderId: parentNode.ProviderId,
                        parentAllowsRecursion: parentNode.Surface.AllowRecursion,
                        parentInvertedWinding: parentNode.InvertedWinding,
                        in parentView,
                        in parentScissor,
                        surfaces,
                        depth,
                        in options,
                        plan,
                        ref candidateCount);
                }
            }

            if (candidateCount == 0)
                break;

            SortCandidatesDeterministically(candidateCount);

            depthStartNodeIndex = plan.Count;
            if (stereoPairMode)
                AdmitStereoPairedCandidates(candidateCount, depth, primaryCount, surfaces, in options, plan);
            else
                AdmitMonoCandidates(candidateCount, depth, primaryCount, surfaces, in options, plan);
            depthEndNodeIndex = plan.Count;

            if (depthEndNodeIndex == depthStartNodeIndex)
                break;
        }
    }

    private void DiscoverCandidatesForParent(
        uint primaryViewIndex,
        uint parentViewIndex,
        uint parentStencilNodeId,
        ulong parentProviderId,
        bool parentAllowsRecursion,
        bool parentInvertedWinding,
        in AdvancedViewRecord parentView,
        in AdvancedPortalScissorRect parentScissor,
        ReadOnlySpan<AdvancedPortalSurfaceDescriptor> surfaces,
        int depth,
        in AdvancedPortalSchedulerOptions options,
        AdvancedPortalFramePlan plan,
        ref int candidateCount)
    {
        Vector3 parentCameraPos = new(
            parentView.CameraPositionAndNear.X,
            parentView.CameraPositionAndNear.Y,
            parentView.CameraPositionAndNear.Z);

        for (int surfaceIndex = 0; surfaceIndex < surfaces.Length; ++surfaceIndex)
        {
            ref readonly AdvancedPortalSurfaceDescriptor surface = ref surfaces[surfaceIndex];
            if (depth > 1)
            {
                if (!parentAllowsRecursion || !surface.AllowRecursion || surface.ProviderId == parentProviderId)
                {
                    ++plan.RejectedCycleCount;
                    continue;
                }
            }

            float eyePlaneDistance = Vector3.Dot(parentCameraPos - surface.PlanePoint, surface.PlaneNormal);
            if (!float.IsFinite(eyePlaneDistance) || eyePlaneDistance <= MinEyePlaneDistance)
            {
                ++plan.RejectedCulledCount;
                continue;
            }

            if (!TryProjectClampedScissor(in surface, in parentView, in parentScissor, out AdvancedPortalScissorRect scissor))
            {
                ++plan.RejectedCulledCount;
                continue;
            }

            long area = scissor.Area;
            if (area < options.MinProjectedPixelArea)
            {
                ++plan.RejectedCulledCount;
                continue;
            }

            if (candidateCount >= _candidates.Length)
            {
                ++plan.RejectedBudgetCount;
                continue;
            }

            _candidates[candidateCount++] = new PortalCandidate
            {
                PrimaryViewIndex = primaryViewIndex,
                ParentViewIndex = parentViewIndex,
                ParentStencilNodeId = parentStencilNodeId,
                ParentProviderId = parentProviderId,
                SurfaceIndex = surfaceIndex,
                ProviderId = surface.ProviderId,
                ProjectedArea = area,
                ScissorRect = scissor,
                ParentInvertedWinding = parentInvertedWinding,
                ParentView = parentView,
            };
        }
    }

    private void AdmitMonoCandidates(
        int candidateCount,
        int depth,
        int primaryCount,
        ReadOnlySpan<AdvancedPortalSurfaceDescriptor> surfaces,
        in AdvancedPortalSchedulerOptions options,
        AdvancedPortalFramePlan plan)
    {
        for (int i = 0; i < candidateCount; ++i)
        {
            ref readonly PortalCandidate candidate = ref _candidates[i];
            int eye = (int)candidate.PrimaryViewIndex;
            if (plan.Count >= options.MaxTotalPortalViewsPerFrame ||
                _perEyeScheduledCount[eye] >= options.MaxPortalViewsPerEye ||
                _perEyeNextStencilNodeId[eye] > AdvancedStencilBitLayout.MaxPortalNodeId)
            {
                ++plan.RejectedBudgetCount;
                continue;
            }

            AdmitSingleCandidate(in candidate, depth, primaryCount, in surfaces[candidate.SurfaceIndex], in options, plan);
        }
    }

    private void AdmitStereoPairedCandidates(
        int candidateCount,
        int depth,
        int primaryCount,
        ReadOnlySpan<AdvancedPortalSurfaceDescriptor> surfaces,
        in AdvancedPortalSchedulerOptions options,
        AdvancedPortalFramePlan plan)
    {
        Array.Clear(_candidateConsumed, 0, candidateCount);
        bool budgetEvictedAtDepth = false;
        for (int i = 0; i < candidateCount; ++i)
        {
            if (_candidateConsumed[i])
                continue;

            ref readonly PortalCandidate first = ref _candidates[i];
            int partnerIndex = FindStereoPartner(i, candidateCount, in first);
            if (partnerIndex >= 0)
            {
                _candidateConsumed[i] = true;
                _candidateConsumed[partnerIndex] = true;
                ref readonly PortalCandidate second = ref _candidates[partnerIndex];
                int eyeA = (int)first.PrimaryViewIndex;
                int eyeB = (int)second.PrimaryViewIndex;

                bool fitsTotal = !budgetEvictedAtDepth && plan.Count + 2 <= options.MaxTotalPortalViewsPerFrame;
                bool fitsEyeA = _perEyeScheduledCount[eyeA] < options.MaxPortalViewsPerEye &&
                    _perEyeNextStencilNodeId[eyeA] <= AdvancedStencilBitLayout.MaxPortalNodeId;
                bool fitsEyeB = _perEyeScheduledCount[eyeB] < options.MaxPortalViewsPerEye &&
                    _perEyeNextStencilNodeId[eyeB] <= AdvancedStencilBitLayout.MaxPortalNodeId;

                if (!fitsTotal || !fitsEyeA || !fitsEyeB)
                {
                    budgetEvictedAtDepth = true;
                    plan.RejectedBudgetCount += 2;
                    continue;
                }

                // Admit left eye before right eye for deterministic view and stencil-ID symmetry.
                if (first.PrimaryViewIndex <= second.PrimaryViewIndex)
                {
                    AdmitSingleCandidate(in first, depth, primaryCount, in surfaces[first.SurfaceIndex], in options, plan);
                    AdmitSingleCandidate(in second, depth, primaryCount, in surfaces[second.SurfaceIndex], in options, plan);
                }
                else
                {
                    AdmitSingleCandidate(in second, depth, primaryCount, in surfaces[second.SurfaceIndex], in options, plan);
                    AdmitSingleCandidate(in first, depth, primaryCount, in surfaces[first.SurfaceIndex], in options, plan);
                }
            }
            else
            {
                // Single-pass stereo drops unpaired single-eye candidates symmetrically when budget
                // eviction has started or when one eye lacks a partner, preventing binocular rivalry.
                _candidateConsumed[i] = true;
                if (budgetEvictedAtDepth)
                    ++plan.RejectedBudgetCount;
                else
                    ++plan.RejectedCulledCount;
            }
        }
    }

    private int FindStereoPartner(int startIndex, int candidateCount, in PortalCandidate first)
    {
        for (int j = startIndex + 1; j < candidateCount; ++j)
        {
            if (_candidateConsumed[j])
                continue;

            ref readonly PortalCandidate other = ref _candidates[j];
            if (other.ProviderId == first.ProviderId &&
                other.ParentProviderId == first.ParentProviderId &&
                other.ParentStencilNodeId == first.ParentStencilNodeId &&
                other.PrimaryViewIndex != first.PrimaryViewIndex)
            {
                return j;
            }
        }

        return -1;
    }

    private void AdmitSingleCandidate(
        in PortalCandidate candidate,
        int depth,
        int primaryCount,
        in AdvancedPortalSurfaceDescriptor surface,
        in AdvancedPortalSchedulerOptions options,
        AdvancedPortalFramePlan plan)
    {
        int eye = (int)candidate.PrimaryViewIndex;
        uint viewIndex = (uint)(primaryCount + plan.Count);
        uint stencilNodeId = _perEyeNextStencilNodeId[eye]++;
        ++_perEyeScheduledCount[eye];

        bool invertedWinding = candidate.ParentInvertedWinding ^ surface.IsMirror;
        uint excludedDrawIndex = surface.DrawHandle.IsValid ? surface.DrawHandle.Index : 0u;
        AdvancedViewRecord childView = BuildPortalViewRecord(
            viewIndex,
            candidate.PrimaryViewIndex,
            candidate.ParentViewIndex,
            stencilNodeId,
            (uint)depth,
            excludedDrawIndex,
            invertedWinding,
            in candidate.ParentView,
            in surface,
            options.ClipPlaneGuardBias);

        AdvancedScheduledPortalView scheduled = new(
            ViewIndex: viewIndex,
            PrimaryViewIndex: candidate.PrimaryViewIndex,
            ParentViewIndex: candidate.ParentViewIndex,
            StencilNodeId: stencilNodeId,
            ParentStencilNodeId: candidate.ParentStencilNodeId,
            RecursionDepth: (uint)depth,
            ProviderId: surface.ProviderId,
            ExcludedDrawIndex: excludedDrawIndex,
            ProjectedArea: candidate.ProjectedArea,
            ScissorRect: candidate.ScissorRect,
            InvertedWinding: invertedWinding,
            RenderTransparents: surface.RenderTransparents,
            MsaaShadingMode: surface.MsaaShadingMode,
            Surface: surface);

        plan.TryAdd(in scheduled, in childView);
    }

    /// <summary>
    /// Builds the GPU <see cref="AdvancedViewRecord"/> for a scheduled portal or mirror sub-view.
    /// Uses row-major, row-vector multiplication (<c>View_child = PortalTransform * View_parent</c>)
    /// so world-space geometry, normals, tangents, and light positions remain right-handed.
    /// </summary>
    internal static AdvancedViewRecord BuildPortalViewRecord(
        uint viewIndex,
        uint primaryViewIndex,
        uint parentViewIndex,
        uint stencilNodeId,
        uint recursionDepth,
        uint excludedDrawIndex,
        bool invertedWinding,
        in AdvancedViewRecord parentView,
        in AdvancedPortalSurfaceDescriptor surface,
        float clipPlaneGuardBias)
    {
        Matrix4x4 childView = surface.PortalTransform * parentView.View;
        Matrix4x4 childPreviousView = surface.PreviousPortalTransform * parentView.PreviousView;

        Matrix4x4 projJittered = parentView.ProjectionJittered;
        Matrix4x4 projUnjittered = parentView.ProjectionUnjittered;
        Matrix4x4 prevProjJittered = parentView.PreviousProjectionJittered;
        Matrix4x4 prevProjUnjittered = parentView.PreviousProjectionUnjittered;

        Matrix4x4 childViewProjJittered = childView * projJittered;
        Matrix4x4 childViewProjUnjittered = childView * projUnjittered;
        Matrix4x4 childPrevViewProjJittered;
        if (surface.PreviousPortalTransform == surface.PortalTransform &&
            parentView.PreviousViewProjectionJittered == parentView.ViewProjectionJittered)
        {
            // Keep stationary mirror+camera frames bit-identical so TSR receives exact zero velocity.
            childPrevViewProjJittered = childViewProjJittered;
        }
        else if (parentView.PreviousViewProjectionJittered == default ||
                 parentView.PreviousView * prevProjJittered == parentView.PreviousViewProjectionJittered)
        {
            childPrevViewProjJittered = childPreviousView * prevProjJittered;
        }
        else
        {
            childPrevViewProjJittered = surface.PreviousPortalTransform * parentView.PreviousViewProjectionJittered;
        }

        Matrix4x4 childPrevViewProjUnjittered;
        if (surface.PreviousPortalTransform == surface.PortalTransform &&
            parentView.PreviousViewProjectionUnjittered == parentView.ViewProjectionUnjittered)
        {
            childPrevViewProjUnjittered = childViewProjUnjittered;
        }
        else if (parentView.PreviousViewProjectionUnjittered == default ||
                 parentView.PreviousView * prevProjUnjittered == parentView.PreviousViewProjectionUnjittered)
        {
            childPrevViewProjUnjittered = childPreviousView * prevProjUnjittered;
        }
        else
        {
            childPrevViewProjUnjittered = surface.PreviousPortalTransform * parentView.PreviousViewProjectionUnjittered;
        }

        if (!Matrix4x4.Invert(childViewProjJittered, out Matrix4x4 invViewProjJittered))
            invViewProjJittered = Matrix4x4.Identity;
        if (!Matrix4x4.Invert(childViewProjUnjittered, out Matrix4x4 invViewProjUnjittered))
            invViewProjUnjittered = Matrix4x4.Identity;

        Vector3 parentCamPos = new(
            parentView.CameraPositionAndNear.X,
            parentView.CameraPositionAndNear.Y,
            parentView.CameraPositionAndNear.Z);
        Vector3 parentCamForward = new(
            parentView.CameraForwardAndFar.X,
            parentView.CameraForwardAndFar.Y,
            parentView.CameraForwardAndFar.Z);

        Vector3 childCamPos = Vector3.Transform(parentCamPos, surface.PortalTransform);
        Vector3 transformedForward = Vector3.TransformNormal(parentCamForward, surface.PortalTransform);
        float forwardLengthSq = transformedForward.LengthSquared();
        Vector3 childCamForward = forwardLengthSq > 1.0e-12f
            ? transformedForward / MathF.Sqrt(forwardLengthSq)
            : -surface.PlaneNormal;

        EAdvancedViewRecordFlags flags = parentView.Flags;
        flags &= ~(EAdvancedViewRecordFlags.Mirror |
                   EAdvancedViewRecordFlags.InvertedWinding |
                   EAdvancedViewRecordFlags.SingleSampleMsaaShading);
        flags |= EAdvancedViewRecordFlags.PortalSubView |
                 EAdvancedViewRecordFlags.ClipPlaneEnabled |
                 EAdvancedViewRecordFlags.SkipHiZAndHistory;
        if (surface.IsMirror)
            flags |= EAdvancedViewRecordFlags.Mirror;
        if (invertedWinding)
            flags |= EAdvancedViewRecordFlags.InvertedWinding;
        if (surface.MsaaShadingMode == EMirrorMsaaShadingMode.SingleSample)
            flags |= EAdvancedViewRecordFlags.SingleSampleMsaaShading;

        float planeOffset = -Vector3.Dot(surface.PlaneNormal, surface.PlanePoint) - clipPlaneGuardBias;
        Vector4 clipPlaneWorld = new(surface.PlaneNormal, planeOffset);
        uint depthAndExcludeDraw = (recursionDepth & 0xFu) | ((excludedDrawIndex & 0x0FFFFFFFu) << 4);

        return new AdvancedViewRecord
        {
            View = childView,
            ProjectionJittered = projJittered,
            ProjectionUnjittered = projUnjittered,
            ViewProjectionJittered = childViewProjJittered,
            ViewProjectionUnjittered = childViewProjUnjittered,
            PreviousView = childPreviousView,
            PreviousProjectionJittered = prevProjJittered,
            PreviousProjectionUnjittered = prevProjUnjittered,
            PreviousViewProjectionJittered = childPrevViewProjJittered,
            PreviousViewProjectionUnjittered = childPrevViewProjUnjittered,
            InverseViewProjectionJittered = invViewProjJittered,
            InverseViewProjectionUnjittered = invViewProjUnjittered,
            CameraPositionAndNear = new Vector4(childCamPos, parentView.CameraPositionAndNear.W),
            CameraForwardAndFar = new Vector4(childCamForward, parentView.CameraForwardAndFar.W),
            RenderSizeAndInverse = parentView.RenderSizeAndInverse,
            OutputSizeAndInverse = parentView.OutputSizeAndInverse,
            CurrentAndPreviousJitter = parentView.CurrentAndPreviousJitter,
            DepthParams = parentView.DepthParams,
            ViewId = viewIndex,
            OutputLayer = parentView.OutputLayer,
            Flags = flags,
            HistoryKeyLo = parentView.HistoryKeyLo,
            HistoryKeyHi = parentView.HistoryKeyHi,
            ViewGeneration = parentView.ViewGeneration,
            ViewMaskLo = parentView.ViewMaskLo,
            ViewMaskHi = parentView.ViewMaskHi,
            FoveationCenterAndBias = parentView.FoveationCenterAndBias,
            FoveationRadii = parentView.FoveationRadii,
            SourceCameraIdentityLo = parentView.SourceCameraIdentityLo,
            SourceCameraIdentityHi = parentView.SourceCameraIdentityHi,
            CullingLayerMask = surface.CullingLayerMask,
            RenderTimeBits = parentView.RenderTimeBits,
            ClipPlaneWorld = clipPlaneWorld,
            PrimaryViewIndex = primaryViewIndex,
            ParentViewIndex = parentViewIndex,
            StencilNodeId = stencilNodeId,
            DepthAndExcludeDraw = depthAndExcludeDraw,
        };
    }

    private void SortCandidatesDeterministically(int count)
    {
        for (int i = 1; i < count; ++i)
        {
            PortalCandidate current = _candidates[i];
            int j = i - 1;
            while (j >= 0 && CompareCandidatePriority(in current, in _candidates[j]) < 0)
            {
                _candidates[j + 1] = _candidates[j];
                --j;
            }
            _candidates[j + 1] = current;
        }
    }

    private static int CompareCandidatePriority(in PortalCandidate left, in PortalCandidate right)
    {
        int areaCompare = right.ProjectedArea.CompareTo(left.ProjectedArea);
        if (areaCompare != 0)
            return areaCompare;
        int providerCompare = left.ProviderId.CompareTo(right.ProviderId);
        if (providerCompare != 0)
            return providerCompare;
        return left.PrimaryViewIndex.CompareTo(right.PrimaryViewIndex);
    }

    private static bool IsStereoPair(ReadOnlySpan<AdvancedViewRecord> primaryViews)
        => primaryViews.Length == 2 &&
           (primaryViews[0].Flags & EAdvancedViewRecordFlags.StereoLeft) != 0 &&
           (primaryViews[1].Flags & EAdvancedViewRecordFlags.StereoRight) != 0;

    private static AdvancedPortalScissorRect BuildFullRenderScissor(in AdvancedViewRecord view)
    {
        int width = Math.Max(0, (int)MathF.Round(view.RenderSizeAndInverse.X));
        int height = Math.Max(0, (int)MathF.Round(view.RenderSizeAndInverse.Y));
        return new AdvancedPortalScissorRect(0, 0, width, height);
    }

    private static bool TryProjectClampedScissor(
        in AdvancedPortalSurfaceDescriptor surface,
        in AdvancedViewRecord view,
        in AdvancedPortalScissorRect parentScissor,
        out AdvancedPortalScissorRect clampedScissor)
    {
        clampedScissor = default;
        float renderWidth = view.RenderSizeAndInverse.X;
        float renderHeight = view.RenderSizeAndInverse.Y;
        if (!float.IsFinite(renderWidth) || !float.IsFinite(renderHeight) || renderWidth <= 0f || renderHeight <= 0f)
            return false;

        bool yDown = (view.Flags & EAdvancedViewRecordFlags.FramebufferTextureYDown) != 0;
        Matrix4x4 vp = view.ViewProjectionUnjittered;

        Vector4 c0 = Vector4.Transform(new Vector4(surface.QuadCorner0, 1.0f), vp);
        Vector4 c1 = Vector4.Transform(new Vector4(surface.QuadCorner1, 1.0f), vp);
        Vector4 c2 = Vector4.Transform(new Vector4(surface.QuadCorner2, 1.0f), vp);
        Vector4 c3 = Vector4.Transform(new Vector4(surface.QuadCorner3, 1.0f), vp);

        int behindNearCount = 0;
        if (c0.W <= MinClipW) ++behindNearCount;
        if (c1.W <= MinClipW) ++behindNearCount;
        if (c2.W <= MinClipW) ++behindNearCount;
        if (c3.W <= MinClipW) ++behindNearCount;

        if (behindNearCount == 4)
            return false;

        if (behindNearCount > 0)
        {
            // Quad straddles the camera near plane; conservatively preserve the parent scissor rect.
            clampedScissor = parentScissor;
            return clampedScissor.IsNonEmpty;
        }

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        AccumulateCornerPixel(c0, renderWidth, renderHeight, yDown, ref minX, ref minY, ref maxX, ref maxY);
        AccumulateCornerPixel(c1, renderWidth, renderHeight, yDown, ref minX, ref minY, ref maxX, ref maxY);
        AccumulateCornerPixel(c2, renderWidth, renderHeight, yDown, ref minX, ref minY, ref maxX, ref maxY);
        AccumulateCornerPixel(c3, renderWidth, renderHeight, yDown, ref minX, ref minY, ref maxX, ref maxY);

        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
            return false;

        int x0 = (int)MathF.Floor(minX);
        int y0 = (int)MathF.Floor(minY);
        int x1 = (int)MathF.Ceiling(maxX);
        int y1 = (int)MathF.Ceiling(maxY);

        AdvancedPortalScissorRect projected = new(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        clampedScissor = projected.Intersect(in parentScissor);
        return clampedScissor.IsNonEmpty;
    }

    private static void AccumulateCornerPixel(
        Vector4 clip,
        float renderWidth,
        float renderHeight,
        bool yDown,
        ref float minX,
        ref float minY,
        ref float maxX,
        ref float maxY)
    {
        float invW = 1.0f / clip.W;
        float u = clip.X * invW * 0.5f + 0.5f;
        float v = clip.Y * invW * 0.5f + 0.5f;
        if (yDown)
            v = 1.0f - v;

        float px = u * renderWidth;
        float py = v * renderHeight;
        if (px < minX) minX = px;
        if (px > maxX) maxX = px;
        if (py < minY) minY = py;
        if (py > maxY) maxY = py;
    }
}
