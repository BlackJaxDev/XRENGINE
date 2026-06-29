using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine;

public enum EVrOutputViewKind
{
    LeftEye,
    RightEye,
    DesktopEditor,
    CyclopeanDesktop,
}

public enum EVrVisibilityPolicy
{
    IndependentDesktopAndVrEyes,
    CombinedRuntimeLeftRightCyclopean,
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
}
