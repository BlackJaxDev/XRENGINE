using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine;

public enum EVrOutputViewKind
{
    LeftEye,
    RightEye,
    DesktopEditor,
    CyclopeanDesktop,
    LeftWide,
    RightWide,
    LeftInset,
    RightInset,
    Debug,
}

public enum EFrameOutputKind
{
    DesktopScene,
    DesktopMirror,
    EditorScenePanel,
    OpenXREyeSubmit,
    OpenVRSubmit,
    ImGuiOverlay,
    DynamicTextOverlay,
    Present,
}

public enum EFrameOutputPhase
{
    Collect,
    Swap,
    Render,
    Submit,
    Overlay,
    Present,
}

public enum EFrameOutputSkipReason
{
    None,
    Cadence,
    Budget,
    MirrorOff,
    SurfaceUnavailable,
    VrGated,
    Disabled,
    HeldLastImage,
}

public enum EVrMirrorMode
{
    Off,
    BlitSubmittedEye,
    CyclopeanReconstruct,
    LowRatePreview,
    FullIndependentRender,
}

public enum EVrVisibilityPolicy
{
    IndependentDesktopAndVrEyes,
    CombinedRuntimeLeftRightCyclopean,
    PerView,
    SharedFrameViewSet,
}

public enum EVrFoveationGazeSource
{
    None,
    FixedCenter,
    EyeTracked,
    RuntimePreferred,
}

public enum EVrFoveationAttachmentKind
{
    None,
    VulkanFragmentShadingRate,
    VulkanFragmentDensityMap,
}

public readonly record struct VrFoveationRegionDefinition(
    float InnerRadius,
    float GuardRadius,
    float MidRadius,
    float OuterRadius)
{
    public static VrFoveationRegionDefinition FromPreset(EVrFoveationQualityPreset preset)
        => preset switch
        {
            EVrFoveationQualityPreset.Conservative => new(0.38f, 0.52f, 0.74f, 1.00f),
            EVrFoveationQualityPreset.Aggressive => new(0.22f, 0.38f, 0.62f, 1.00f),
            _ => new(0.30f, 0.46f, 0.68f, 1.00f),
        };
}

public readonly record struct ViewFoveationAttachmentContext(
    EVrFoveationAttachmentKind Kind,
    ulong ResourceKey,
    string? ResourceName,
    bool OwnedByResourcePlanner)
{
    public bool IsActive => Kind != EVrFoveationAttachmentKind.None && ResourceKey != 0UL;

    public static ViewFoveationAttachmentContext None => default;

    public static ViewFoveationAttachmentContext FromCapability(
        EVrFoveationCapabilityPath capabilityPath,
        ulong backendResourceKey)
    {
        EVrFoveationAttachmentKind kind = capabilityPath switch
        {
            EVrFoveationCapabilityPath.VulkanFragmentShadingRate => EVrFoveationAttachmentKind.VulkanFragmentShadingRate,
            EVrFoveationCapabilityPath.VulkanFragmentDensityMap => EVrFoveationAttachmentKind.VulkanFragmentDensityMap,
            _ => EVrFoveationAttachmentKind.None,
        };

        if (kind == EVrFoveationAttachmentKind.None || backendResourceKey == 0UL)
            return None;

        return new(
            kind,
            backendResourceKey,
            $"OpenXR.Foveation.{kind}.0x{backendResourceKey:X16}",
            OwnedByResourcePlanner: true);
    }
}

public readonly record struct ViewFoveationContext(
    EVrFoveationMode RequestedMode,
    EVrFoveationMode EffectiveMode,
    EVrFoveationQualityPreset QualityPreset,
    EVrFoveationCapabilityPath CapabilityPath,
    EVrFoveationGazeSource GazeSource,
    Vector2 ViewSpaceCenter,
    Vector2 ProjectionSpaceCenter,
    Vector2 RenderTargetUvCenter,
    VrFoveationRegionDefinition Regions,
    ulong BackendResourceKey,
    ViewFoveationAttachmentContext Attachment,
    string? FallbackReason)
{
    public bool IsEnabled => EffectiveMode != EVrFoveationMode.Off && CapabilityPath != EVrFoveationCapabilityPath.None;

    public static ViewFoveationContext Off(EVrFoveationQualityPreset qualityPreset = EVrFoveationQualityPreset.Balanced)
        => new(
            EVrFoveationMode.Off,
            EVrFoveationMode.Off,
            qualityPreset,
            EVrFoveationCapabilityPath.None,
            EVrFoveationGazeSource.None,
            Vector2.Zero,
            Vector2.Zero,
            new Vector2(0.5f, 0.5f),
            VrFoveationRegionDefinition.FromPreset(qualityPreset),
            0UL,
            ViewFoveationAttachmentContext.None,
            null);

    public static ViewFoveationContext FromResolution(
        in VrFoveationResolution resolution,
        Vector2 viewSpaceCenter,
        Vector2 projectionSpaceCenter,
        Vector2 renderTargetUvCenter,
        EVrFoveationGazeSource gazeSource,
        ulong backendResourceKey = 0UL)
        => new(
            resolution.RequestedMode,
            resolution.EffectiveMode,
            resolution.QualityPreset,
            resolution.CapabilityPath,
            gazeSource,
            viewSpaceCenter,
            projectionSpaceCenter,
            renderTargetUvCenter,
            VrFoveationRegionDefinition.FromPreset(resolution.QualityPreset),
            backendResourceKey,
            ViewFoveationAttachmentContext.FromCapability(resolution.CapabilityPath, backendResourceKey),
            resolution.Diagnostic);
}

public readonly record struct ViewVisibilityFrustumContext(
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    bool IsConservative,
    bool IncludesFoveatedViews)
{
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;
}

public readonly record struct ViewVisibilitySetBinding(
    int VisibilityGroupIndex,
    int VisibilitySetIdentity,
    ulong Generation,
    bool IsImmutable,
    string? DebugName)
{
    public bool IsBound => VisibilitySetIdentity != 0;

    public static ViewVisibilitySetBinding Unbound(int visibilityGroupIndex)
        => new(visibilityGroupIndex, 0, 0UL, true, null);

    public static ViewVisibilitySetBinding Create(
        int visibilityGroupIndex,
        object visibleSet,
        ulong generation,
        string? debugName = null)
        => visibleSet is null
            ? throw new ArgumentNullException(nameof(visibleSet))
            : new(
                visibilityGroupIndex,
                RuntimeHelpers.GetHashCode(visibleSet),
                generation,
                IsImmutable: true,
                debugName);
}

public readonly record struct ViewRenderContext(
    EVrOutputViewKind Kind,
    uint ViewIndex,
    int VisibilityGroupIndex,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    ViewFoveationContext Foveation)
{
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

    public ViewRenderContext WithVisibilityGroup(int visibilityGroupIndex)
        => new(Kind, ViewIndex, visibilityGroupIndex, ViewMatrix, ProjectionMatrix, Foveation);

    public ViewRenderContext WithKindAndIndex(EVrOutputViewKind kind, uint viewIndex)
        => new(kind, viewIndex, VisibilityGroupIndex, ViewMatrix, ProjectionMatrix, Foveation);
}

public readonly record struct ViewRecordingWorkItem(
    EVrViewRenderMode RenderMode,
    int WorkerIndex,
    ViewRenderContext View,
    ViewFoveationContext Foveation)
{
    public bool HasImmutableFoveationInput => Foveation.Equals(View.Foveation);
}

public readonly record struct RenderFrameViewRect(
    uint X,
    uint Y,
    uint Width,
    uint Height)
{
    public bool IsValid => Width != 0u && Height != 0u;

    public static RenderFrameViewRect FromSize(uint width, uint height)
        => new(0u, 0u, width, height);
}

public readonly record struct RenderFrameViewDescriptor(
    uint ViewId,
    EVrOutputViewKind Kind,
    uint ParentViewId,
    int VisibilityGroupIndex,
    int OpenXrViewIndex,
    uint OutputLayer,
    RenderFrameViewRect ViewRect,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    Matrix4x4 PreviousViewProjectionMatrix,
    ViewFoveationContext Foveation,
    string? DebugName = null)
{
    public const uint InvalidViewId = uint.MaxValue;

    public bool HasParent => ParentViewId != InvalidViewId;
    public bool IsStereoEye => Kind is EVrOutputViewKind.LeftEye or EVrOutputViewKind.RightEye;
    public bool IsWideView => Kind is EVrOutputViewKind.LeftWide or EVrOutputViewKind.RightWide;
    public bool IsInsetView => Kind is EVrOutputViewKind.LeftInset or EVrOutputViewKind.RightInset;
    public bool IsQuadView => IsWideView || IsInsetView;
    public bool IsLeftEyeFamily => Kind is EVrOutputViewKind.LeftEye or EVrOutputViewKind.LeftWide or EVrOutputViewKind.LeftInset;
    public bool IsRightEyeFamily => Kind is EVrOutputViewKind.RightEye or EVrOutputViewKind.RightWide or EVrOutputViewKind.RightInset;
    public bool IsXrSubmittedView => IsStereoEye || IsQuadView;
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

    public RenderFrameViewDescriptor WithVisibilityGroup(int visibilityGroupIndex)
        => this with { VisibilityGroupIndex = visibilityGroupIndex };

    public RenderFrameViewDescriptor WithParent(uint parentViewId)
        => this with { ParentViewId = parentViewId };
}

public readonly record struct RenderFrameViewSet
{
    public const int MaxViewCount = 8;

    private readonly RenderFrameViewDescriptor _view0;
    private readonly RenderFrameViewDescriptor _view1;
    private readonly RenderFrameViewDescriptor _view2;
    private readonly RenderFrameViewDescriptor _view3;
    private readonly RenderFrameViewDescriptor _view4;
    private readonly RenderFrameViewDescriptor _view5;
    private readonly RenderFrameViewDescriptor _view6;
    private readonly RenderFrameViewDescriptor _view7;

    private RenderFrameViewSet(
        EVrViewRenderMode renderMode,
        EVrVisibilityPolicy visibilityPolicy,
        int visibilityGroupCount,
        int viewCount,
        RenderFrameViewDescriptor view0,
        RenderFrameViewDescriptor view1,
        RenderFrameViewDescriptor view2,
        RenderFrameViewDescriptor view3,
        RenderFrameViewDescriptor view4,
        RenderFrameViewDescriptor view5,
        RenderFrameViewDescriptor view6,
        RenderFrameViewDescriptor view7,
        string? debugName)
    {
        RenderMode = renderMode;
        VisibilityPolicy = visibilityPolicy;
        VisibilityGroupCount = visibilityGroupCount;
        ViewCount = viewCount;
        _view0 = view0;
        _view1 = view1;
        _view2 = view2;
        _view3 = view3;
        _view4 = view4;
        _view5 = view5;
        _view6 = view6;
        _view7 = view7;
        DebugName = debugName;
    }

    public EVrViewRenderMode RenderMode { get; }
    public EVrVisibilityPolicy VisibilityPolicy { get; }
    public int VisibilityGroupCount { get; }
    public int ViewCount { get; }
    public string? DebugName { get; }
    public bool IsQuadViewSet => CountQuadViews() == 4;

    public static RenderFrameViewSet Create(
        EVrViewRenderMode renderMode,
        EVrVisibilityPolicy visibilityPolicy,
        int visibilityGroupCount,
        ReadOnlySpan<RenderFrameViewDescriptor> views,
        string? debugName = null)
    {
        if (views.Length < 1 || views.Length > MaxViewCount)
            throw new ArgumentOutOfRangeException(nameof(views), $"A frame view set supports 1 to {MaxViewCount} views.");
        if (visibilityGroupCount < 1)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupCount));

        RenderFrameViewDescriptor view0 = default;
        RenderFrameViewDescriptor view1 = default;
        RenderFrameViewDescriptor view2 = default;
        RenderFrameViewDescriptor view3 = default;
        RenderFrameViewDescriptor view4 = default;
        RenderFrameViewDescriptor view5 = default;
        RenderFrameViewDescriptor view6 = default;
        RenderFrameViewDescriptor view7 = default;

        for (int i = 0; i < views.Length; i++)
        {
            RenderFrameViewDescriptor view = views[i];
            ValidateView(i, view, views.Length, visibilityGroupCount);
            switch (i)
            {
                case 0: view0 = view; break;
                case 1: view1 = view; break;
                case 2: view2 = view; break;
                case 3: view3 = view; break;
                case 4: view4 = view; break;
                case 5: view5 = view; break;
                case 6: view6 = view; break;
                case 7: view7 = view; break;
            }
        }

        return new(
            renderMode,
            visibilityPolicy,
            visibilityGroupCount,
            views.Length,
            view0,
            view1,
            view2,
            view3,
            view4,
            view5,
            view6,
            view7,
            debugName);
    }

    private static void ValidateView(
        int index,
        in RenderFrameViewDescriptor view,
        int viewCount,
        int visibilityGroupCount)
    {
        if (view.ViewId != (uint)index)
            throw new ArgumentException("Frame view IDs must be dense and match their view-set slot.", nameof(view));
        if (!view.ViewRect.IsValid)
            throw new ArgumentException("Frame views require a non-zero render rectangle.", nameof(view));
        if (view.VisibilityGroupIndex < 0 || view.VisibilityGroupIndex >= visibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(view.VisibilityGroupIndex));
        if (view.HasParent && view.ParentViewId >= (uint)viewCount)
            throw new ArgumentOutOfRangeException(nameof(view.ParentViewId));
    }

    public RenderFrameViewDescriptor GetView(int index)
        => index switch
        {
            0 when ViewCount > 0 => _view0,
            1 when ViewCount > 1 => _view1,
            2 when ViewCount > 2 => _view2,
            3 when ViewCount > 3 => _view3,
            4 when ViewCount > 4 => _view4,
            5 when ViewCount > 5 => _view5,
            6 when ViewCount > 6 => _view6,
            7 when ViewCount > 7 => _view7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public int CountViewsInVisibilityGroup(int visibilityGroupIndex)
    {
        if (visibilityGroupIndex < 0 || visibilityGroupIndex >= VisibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupIndex));

        int count = 0;
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).VisibilityGroupIndex == visibilityGroupIndex)
                count++;
        }

        return count;
    }

    public int CountQuadViews()
    {
        int count = 0;
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).IsQuadView)
                count++;
        }

        return count;
    }

    public int FindFirstView(EVrOutputViewKind kind)
    {
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).Kind == kind)
                return i;
        }

        return -1;
    }
}

public enum ERenderFrameViewBatchKind
{
    SequentialView,
    LayeredStereoPair,
    LayeredViewSet,
    ParallelCommandBufferRecording,
}

public readonly record struct RenderFrameViewBatch(
    ERenderFrameViewBatchKind Kind,
    ulong ViewMask,
    int OutputLayerBase,
    string? DebugName)
{
    public int ViewCount => BitOperations.PopCount(ViewMask);
    public bool IsLayered => Kind is ERenderFrameViewBatchKind.LayeredStereoPair or ERenderFrameViewBatchKind.LayeredViewSet;

    public bool ContainsView(int viewIndex)
    {
        if ((uint)viewIndex >= 64u)
            throw new ArgumentOutOfRangeException(nameof(viewIndex));

        return (ViewMask & (1UL << viewIndex)) != 0UL;
    }
}

public readonly record struct RenderFrameViewBatchCapabilities(
    bool SupportsLayeredStereoPairs,
    bool SupportsLayeredQuadView,
    bool SupportsParallelCommandBufferRecording,
    bool SupportsMixedLayerExtents,
    int MaxLayerCount)
{
    public static RenderFrameViewBatchCapabilities None => new(false, false, false, false, 1);
    public static RenderFrameViewBatchCapabilities VulkanMultiviewStereoPairs => new(true, false, true, false, 2);
    public static RenderFrameViewBatchCapabilities VulkanMultiviewQuadView => new(true, true, true, false, 4);
}

public readonly record struct RenderFrameViewBatchPlan
{
    private readonly RenderFrameViewBatch _batch0;
    private readonly RenderFrameViewBatch _batch1;
    private readonly RenderFrameViewBatch _batch2;
    private readonly RenderFrameViewBatch _batch3;
    private readonly RenderFrameViewBatch _batch4;
    private readonly RenderFrameViewBatch _batch5;
    private readonly RenderFrameViewBatch _batch6;
    private readonly RenderFrameViewBatch _batch7;

    internal RenderFrameViewBatchPlan(
        int batchCount,
        RenderFrameViewBatch batch0,
        RenderFrameViewBatch batch1,
        RenderFrameViewBatch batch2,
        RenderFrameViewBatch batch3,
        RenderFrameViewBatch batch4,
        RenderFrameViewBatch batch5,
        RenderFrameViewBatch batch6,
        RenderFrameViewBatch batch7)
    {
        BatchCount = batchCount;
        _batch0 = batch0;
        _batch1 = batch1;
        _batch2 = batch2;
        _batch3 = batch3;
        _batch4 = batch4;
        _batch5 = batch5;
        _batch6 = batch6;
        _batch7 = batch7;
    }

    public int BatchCount { get; }

    public RenderFrameViewBatch GetBatch(int index)
        => index switch
        {
            0 when BatchCount > 0 => _batch0,
            1 when BatchCount > 1 => _batch1,
            2 when BatchCount > 2 => _batch2,
            3 when BatchCount > 3 => _batch3,
            4 when BatchCount > 4 => _batch4,
            5 when BatchCount > 5 => _batch5,
            6 when BatchCount > 6 => _batch6,
            7 when BatchCount > 7 => _batch7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public static RenderFrameViewBatchPlan Create(ReadOnlySpan<RenderFrameViewBatch> batches)
    {
        if (batches.Length < 1 || batches.Length > RenderFrameViewSet.MaxViewCount)
            throw new ArgumentOutOfRangeException(nameof(batches));

        RenderFrameViewBatch batch0 = default;
        RenderFrameViewBatch batch1 = default;
        RenderFrameViewBatch batch2 = default;
        RenderFrameViewBatch batch3 = default;
        RenderFrameViewBatch batch4 = default;
        RenderFrameViewBatch batch5 = default;
        RenderFrameViewBatch batch6 = default;
        RenderFrameViewBatch batch7 = default;

        for (int i = 0; i < batches.Length; i++)
        {
            switch (i)
            {
                case 0: batch0 = batches[i]; break;
                case 1: batch1 = batches[i]; break;
                case 2: batch2 = batches[i]; break;
                case 3: batch3 = batches[i]; break;
                case 4: batch4 = batches[i]; break;
                case 5: batch5 = batches[i]; break;
                case 6: batch6 = batches[i]; break;
                case 7: batch7 = batches[i]; break;
            }
        }

        return new(batches.Length, batch0, batch1, batch2, batch3, batch4, batch5, batch6, batch7);
    }
}

public static class RenderFrameViewBatchPlanner
{
    public static RenderFrameViewBatchPlan Plan(
        in RenderFrameViewSet viewSet,
        in RenderFrameViewBatchCapabilities capabilities)
        => viewSet.RenderMode switch
        {
            EVrViewRenderMode.SinglePassStereo => PlanLayered(viewSet, capabilities),
            EVrViewRenderMode.ParallelCommandBufferRecording when capabilities.SupportsParallelCommandBufferRecording =>
                PlanOneBatchPerView(viewSet, ERenderFrameViewBatchKind.ParallelCommandBufferRecording),
            _ => PlanOneBatchPerView(viewSet, ERenderFrameViewBatchKind.SequentialView),
        };

    private static RenderFrameViewBatchPlan PlanLayered(
        in RenderFrameViewSet viewSet,
        in RenderFrameViewBatchCapabilities capabilities)
    {
        RenderFrameViewBatchPlanBuilder batches = default;
        ulong plannedMask = 0UL;

        if (capabilities.SupportsLayeredQuadView &&
            viewSet.IsQuadViewSet &&
            capabilities.MaxLayerCount >= 4 &&
            TryBuildQuadMask(viewSet, capabilities.SupportsMixedLayerExtents, out ulong quadMask))
        {
            batches.Append(new(
                ERenderFrameViewBatchKind.LayeredViewSet,
                quadMask,
                OutputLayerBase: 0,
                "quad-view layered view set"));
            plannedMask |= quadMask;
        }

        if (plannedMask == 0UL && capabilities.SupportsLayeredStereoPairs && capabilities.MaxLayerCount >= 2)
        {
            TryAppendPair(
                viewSet,
                capabilities.SupportsMixedLayerExtents,
                EVrOutputViewKind.LeftWide,
                EVrOutputViewKind.RightWide,
                "quad wide stereo pair",
                ref batches,
                ref plannedMask);
            TryAppendPair(
                viewSet,
                capabilities.SupportsMixedLayerExtents,
                EVrOutputViewKind.LeftInset,
                EVrOutputViewKind.RightInset,
                "quad inset stereo pair",
                ref batches,
                ref plannedMask);
            TryAppendPair(
                viewSet,
                capabilities.SupportsMixedLayerExtents,
                EVrOutputViewKind.LeftEye,
                EVrOutputViewKind.RightEye,
                "stereo pair",
                ref batches,
                ref plannedMask);
        }

        AppendUnplannedSequentialViews(viewSet, ref batches, plannedMask);
        return batches.ToPlan();
    }

    private static RenderFrameViewBatchPlan PlanOneBatchPerView(
        in RenderFrameViewSet viewSet,
        ERenderFrameViewBatchKind kind)
    {
        RenderFrameViewBatchPlanBuilder batches = default;
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            batches.Append(new(
                kind,
                1UL << i,
                (int)view.OutputLayer,
                view.DebugName));
        }

        return batches.ToPlan();
    }

    private static void TryAppendPair(
        in RenderFrameViewSet viewSet,
        bool supportsMixedLayerExtents,
        EVrOutputViewKind leftKind,
        EVrOutputViewKind rightKind,
        string debugName,
        ref RenderFrameViewBatchPlanBuilder batches,
        ref ulong plannedMask)
    {
        int left = viewSet.FindFirstView(leftKind);
        int right = viewSet.FindFirstView(rightKind);
        if (left < 0 || right < 0)
            return;

        RenderFrameViewDescriptor leftView = viewSet.GetView(left);
        RenderFrameViewDescriptor rightView = viewSet.GetView(right);
        if (!supportsMixedLayerExtents && !SameExtent(leftView, rightView))
            return;

        ulong mask = (1UL << left) | (1UL << right);
        batches.Append(new(
            ERenderFrameViewBatchKind.LayeredStereoPair,
            mask,
            Math.Min((int)leftView.OutputLayer, (int)rightView.OutputLayer),
            debugName));
        plannedMask |= mask;
    }

    private static bool TryBuildQuadMask(
        in RenderFrameViewSet viewSet,
        bool supportsMixedLayerExtents,
        out ulong mask)
    {
        mask = 0UL;
        int leftWide = viewSet.FindFirstView(EVrOutputViewKind.LeftWide);
        int rightWide = viewSet.FindFirstView(EVrOutputViewKind.RightWide);
        int leftInset = viewSet.FindFirstView(EVrOutputViewKind.LeftInset);
        int rightInset = viewSet.FindFirstView(EVrOutputViewKind.RightInset);
        if (leftWide < 0 || rightWide < 0 || leftInset < 0 || rightInset < 0)
            return false;

        RenderFrameViewDescriptor first = viewSet.GetView(leftWide);
        if (!supportsMixedLayerExtents &&
            (!SameExtent(first, viewSet.GetView(rightWide)) ||
             !SameExtent(first, viewSet.GetView(leftInset)) ||
             !SameExtent(first, viewSet.GetView(rightInset))))
            return false;

        mask =
            (1UL << leftWide) |
            (1UL << rightWide) |
            (1UL << leftInset) |
            (1UL << rightInset);
        return true;
    }

    private static void AppendUnplannedSequentialViews(
        in RenderFrameViewSet viewSet,
        ref RenderFrameViewBatchPlanBuilder batches,
        ulong plannedMask)
    {
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            ulong mask = 1UL << i;
            if ((plannedMask & mask) != 0UL)
                continue;

            RenderFrameViewDescriptor view = viewSet.GetView(i);
            batches.Append(new(
                ERenderFrameViewBatchKind.SequentialView,
                mask,
                (int)view.OutputLayer,
                view.DebugName));
        }
    }

    private static bool SameExtent(
        in RenderFrameViewDescriptor first,
        in RenderFrameViewDescriptor second)
        => first.ViewRect.Width == second.ViewRect.Width &&
           first.ViewRect.Height == second.ViewRect.Height;

    private ref struct RenderFrameViewBatchPlanBuilder
    {
        private int _count;
        private RenderFrameViewBatch _batch0;
        private RenderFrameViewBatch _batch1;
        private RenderFrameViewBatch _batch2;
        private RenderFrameViewBatch _batch3;
        private RenderFrameViewBatch _batch4;
        private RenderFrameViewBatch _batch5;
        private RenderFrameViewBatch _batch6;
        private RenderFrameViewBatch _batch7;

        public void Append(in RenderFrameViewBatch batch)
        {
            switch (_count)
            {
                case 0: _batch0 = batch; break;
                case 1: _batch1 = batch; break;
                case 2: _batch2 = batch; break;
                case 3: _batch3 = batch; break;
                case 4: _batch4 = batch; break;
                case 5: _batch5 = batch; break;
                case 6: _batch6 = batch; break;
                case 7: _batch7 = batch; break;
                default: throw new InvalidOperationException("Frame view batch plan exceeded its maximum batch count.");
            }

            _count++;
        }

        public RenderFrameViewBatchPlan ToPlan()
        {
            if (_count == 0)
                throw new InvalidOperationException("Frame view batch plan must contain at least one batch.");

            return new(
                _count,
                _batch0,
                _batch1,
                _batch2,
                _batch3,
                _batch4,
                _batch5,
                _batch6,
                _batch7);
        }
    }
}

public readonly record struct FrameOutputPacingDecision(
    EVrOutputViewKind ViewKind,
    EFrameOutputKind OutputKind,
    ulong FrameId,
    bool IsDue,
    bool CadenceSkipped,
    bool AutoSkipped,
    EFrameOutputSkipReason SkipReason,
    float ConfiguredTargetRateHz,
    float SourceRateHz,
    double AchievedRateHz,
    int TotalRenderCount,
    int TotalSkipCount)
{
    public bool Skipped => !IsDue;

    public static FrameOutputPacingDecision Due(
        EVrOutputViewKind viewKind,
        EFrameOutputKind outputKind,
        ulong frameId = 0UL,
        float configuredTargetRateHz = 0.0f,
        float sourceRateHz = 0.0f)
        => new(
            viewKind,
            outputKind,
            frameId,
            IsDue: true,
            CadenceSkipped: false,
            AutoSkipped: false,
            EFrameOutputSkipReason.None,
            configuredTargetRateHz,
            sourceRateHz,
            sourceRateHz > 0.0f ? sourceRateHz : 0.0,
            TotalRenderCount: 0,
            TotalSkipCount: 0);
}

public readonly record struct FrameOutputTelemetry(
    EFrameOutputKind OutputKind,
    EVrOutputViewKind ViewKind,
    EFrameOutputPhase Phase,
    FrameOutputPacingDecision Pacing,
    string? Name,
    string? PipelineName,
    bool Active,
    bool Rendered,
    bool SceneRendered,
    bool Mirror,
    bool SeparateSceneRender,
    bool SharedVisibility,
    int CommandCount,
    int DrawCalls,
    int MultiDrawCalls,
    int Triangles,
    double CpuMs,
    double GpuMs);

public readonly record struct ViewRenderGroupContext
{
    private readonly ViewRenderContext _view0;
    private readonly ViewRenderContext _view1;
    private readonly ViewRenderContext _view2;
    private readonly ViewVisibilitySetBinding _visibilitySet0;
    private readonly ViewVisibilitySetBinding _visibilitySet1;
    private readonly ViewVisibilitySetBinding _visibilitySet2;

    public ViewRenderGroupContext(
        EVrViewRenderMode renderMode,
        EVrVisibilityPolicy visibilityPolicy,
        int visibilityGroupCount,
        ViewVisibilityFrustumContext visibilityFrustum,
        ViewRenderContext view0,
        ViewRenderContext view1,
        ViewRenderContext view2,
        int viewCount,
        ViewVisibilitySetBinding visibilitySet0 = default,
        ViewVisibilitySetBinding visibilitySet1 = default,
        ViewVisibilitySetBinding visibilitySet2 = default)
    {
        if ((uint)viewCount > 3u)
            throw new ArgumentOutOfRangeException(nameof(viewCount), "A VR view group supports up to three output views.");
        if (visibilityGroupCount < 1 || visibilityGroupCount > viewCount)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupCount));

        RenderMode = renderMode;
        VisibilityPolicy = visibilityPolicy;
        VisibilityGroupCount = visibilityGroupCount;
        VisibilityFrustum = visibilityFrustum;
        _view0 = view0;
        _view1 = view1;
        _view2 = view2;
        ViewCount = viewCount;
        _visibilitySet0 = NormalizeVisibilitySetBinding(0, visibilitySet0);
        _visibilitySet1 = NormalizeVisibilitySetBinding(1, visibilitySet1);
        _visibilitySet2 = NormalizeVisibilitySetBinding(2, visibilitySet2);
    }

    public EVrViewRenderMode RenderMode { get; }
    public EVrVisibilityPolicy VisibilityPolicy { get; }
    public int VisibilityGroupCount { get; }
    public int ViewCount { get; }
    public ViewVisibilityFrustumContext VisibilityFrustum { get; }
    public bool AllViewsShareVisibility => VisibilityGroupCount == 1;

    private static ViewVisibilitySetBinding NormalizeVisibilitySetBinding(
        int visibilityGroupIndex,
        in ViewVisibilitySetBinding binding)
    {
        if (!binding.IsBound)
            return ViewVisibilitySetBinding.Unbound(visibilityGroupIndex);
        if (binding.VisibilityGroupIndex != visibilityGroupIndex)
            throw new ArgumentException("Visibility set binding group index does not match its storage slot.", nameof(binding));
        if (!binding.IsImmutable)
            throw new ArgumentException("Visibility set bindings must refer to immutable visible sets.", nameof(binding));
        return binding;
    }

    public ViewRenderContext GetView(int index)
        => index switch
        {
            0 when ViewCount > 0 => _view0,
            1 when ViewCount > 1 => _view1,
            2 when ViewCount > 2 => _view2,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public int CountViewsInVisibilityGroup(int visibilityGroupIndex)
    {
        if (visibilityGroupIndex < 0 || visibilityGroupIndex >= VisibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupIndex));

        int count = 0;
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).VisibilityGroupIndex == visibilityGroupIndex)
                count++;
        }

        return count;
    }

    public ViewVisibilitySetBinding GetVisibilitySetBinding(int visibilityGroupIndex)
    {
        if (visibilityGroupIndex < 0 || visibilityGroupIndex >= VisibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupIndex));

        return visibilityGroupIndex switch
        {
            0 => _visibilitySet0,
            1 => _visibilitySet1,
            2 => _visibilitySet2,
            _ => throw new ArgumentOutOfRangeException(nameof(visibilityGroupIndex)),
        };
    }

    public ViewVisibilitySetBinding GetVisibilitySetBindingForView(int viewIndex)
    {
        ViewRenderContext view = GetView(viewIndex);
        return GetVisibilitySetBinding(view.VisibilityGroupIndex);
    }

    public ViewRenderGroupContext BindVisibilitySet(
        int visibilityGroupIndex,
        object visibleSet,
        ulong generation,
        string? debugName = null)
    {
        ViewVisibilitySetBinding binding = ViewVisibilitySetBinding.Create(
            visibilityGroupIndex,
            visibleSet,
            generation,
            debugName);
        return BindVisibilitySet(binding);
    }

    public ViewRenderGroupContext BindVisibilitySet(in ViewVisibilitySetBinding binding)
    {
        if (binding.VisibilityGroupIndex < 0 || binding.VisibilityGroupIndex >= VisibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(binding.VisibilityGroupIndex));

        return binding.VisibilityGroupIndex switch
        {
            0 => new(
                RenderMode,
                VisibilityPolicy,
                VisibilityGroupCount,
                VisibilityFrustum,
                _view0,
                _view1,
                _view2,
                ViewCount,
                binding,
                _visibilitySet1,
                _visibilitySet2),
            1 => new(
                RenderMode,
                VisibilityPolicy,
                VisibilityGroupCount,
                VisibilityFrustum,
                _view0,
                _view1,
                _view2,
                ViewCount,
                _visibilitySet0,
                binding,
                _visibilitySet2),
            2 => new(
                RenderMode,
                VisibilityPolicy,
                VisibilityGroupCount,
                VisibilityFrustum,
                _view0,
                _view1,
                _view2,
                ViewCount,
                _visibilitySet0,
                _visibilitySet1,
                binding),
            _ => throw new ArgumentOutOfRangeException(nameof(binding.VisibilityGroupIndex)),
        };
    }

    public ViewRecordingWorkItem CreateRecordingWorkItem(int viewIndex, int workerIndex)
    {
        ViewRenderContext view = GetView(viewIndex);
        return new ViewRecordingWorkItem(RenderMode, workerIndex, view, view.Foveation);
    }

    public static ViewRenderGroupContext CreateDesktopEditingGroup(
        EVrViewRenderMode renderMode,
        ViewRenderContext desktopEditor,
        ViewRenderContext leftEye,
        ViewRenderContext rightEye,
        ViewVisibilityFrustumContext desktopVisibilityFrustum)
        => new(
            renderMode,
            EVrVisibilityPolicy.IndependentDesktopAndVrEyes,
            visibilityGroupCount: 2,
            desktopVisibilityFrustum,
            desktopEditor.WithKindAndIndex(EVrOutputViewKind.DesktopEditor, 0u).WithVisibilityGroup(0),
            leftEye.WithKindAndIndex(EVrOutputViewKind.LeftEye, 0u).WithVisibilityGroup(1),
            rightEye.WithKindAndIndex(EVrOutputViewKind.RightEye, 1u).WithVisibilityGroup(1),
            viewCount: 3);

    public static ViewRenderGroupContext CreateCombinedRuntimeGroup(
        EVrViewRenderMode renderMode,
        ViewRenderContext leftEye,
        ViewRenderContext rightEye,
        ViewRenderContext cyclopeanDesktop,
        ViewVisibilityFrustumContext combinedVisibilityFrustum)
        => new(
            renderMode,
            EVrVisibilityPolicy.CombinedRuntimeLeftRightCyclopean,
            visibilityGroupCount: 1,
            combinedVisibilityFrustum,
            leftEye.WithKindAndIndex(EVrOutputViewKind.LeftEye, 0u).WithVisibilityGroup(0),
            rightEye.WithKindAndIndex(EVrOutputViewKind.RightEye, 1u).WithVisibilityGroup(0),
            cyclopeanDesktop.WithKindAndIndex(EVrOutputViewKind.CyclopeanDesktop, 2u).WithVisibilityGroup(0),
            viewCount: 3);

    public static ViewVisibilityFrustumContext BuildCombinedRuntimeVisibilityFrustum(
        in ViewRenderContext leftEye,
        in ViewRenderContext rightEye,
        in ViewRenderContext cyclopeanDesktop,
        bool highSpeedMode = true)
    {
        ProjectionMatrixCombiner.FrustumSolveResult solve = ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
            [leftEye.ProjectionMatrix, rightEye.ProjectionMatrix, cyclopeanDesktop.ProjectionMatrix],
            [leftEye.ViewMatrix, rightEye.ViewMatrix, cyclopeanDesktop.ViewMatrix],
            farBoundsSourceCount: null,
            solveViewOrientation: true,
            refineViewOrientation: !highSpeedMode,
            highSpeedMode: highSpeedMode);

        bool includesFoveatedViews =
            leftEye.Foveation.IsEnabled ||
            rightEye.Foveation.IsEnabled ||
            cyclopeanDesktop.Foveation.IsEnabled;

        return new ViewVisibilityFrustumContext(
            solve.View,
            solve.Projection,
            IsConservative: true,
            IncludesFoveatedViews: includesFoveatedViews);
    }

    public static ViewVisibilityFrustumContext BuildCombinedRuntimeVisibilityFrustum(
        in ViewRenderContext leftEye,
        in ViewRenderContext rightEye,
        bool highSpeedMode = true)
    {
        ProjectionMatrixCombiner.FrustumSolveResult solve = ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
            [leftEye.ProjectionMatrix, rightEye.ProjectionMatrix],
            [leftEye.ViewMatrix, rightEye.ViewMatrix],
            farBoundsSourceCount: null,
            solveViewOrientation: true,
            refineViewOrientation: !highSpeedMode,
            highSpeedMode: highSpeedMode);

        bool includesFoveatedViews =
            leftEye.Foveation.IsEnabled ||
            rightEye.Foveation.IsEnabled;

        return new ViewVisibilityFrustumContext(
            solve.View,
            solve.Projection,
            IsConservative: true,
            IncludesFoveatedViews: includesFoveatedViews);
    }
}
