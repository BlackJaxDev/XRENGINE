using System.Numerics;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable view/pass data shared by every mesh draw in one frozen render
/// scope. Keeping this payload behind one reference prevents frame-plan sealing
/// and refresh-cohort construction from copying more than two kilobytes of
/// identical camera and shadow matrices for every visible draw.
/// </summary>
internal sealed class VulkanMeshDrawViewSnapshot
{
    private static long s_nextPacketId;

    /// <summary>
    /// Number of recent snapshots kept per thread. One eye frame alternates
    /// between several passes and views, so one cached entry missed for about
    /// one draw in three and allocated a new snapshot each time.
    /// </summary>
    private const int CacheCapacity = 16;
    [ThreadStatic]
    private static VulkanMeshDrawViewSnapshotCacheKey[]? s_cacheKeys;
    [ThreadStatic]
    private static VulkanMeshDrawViewSnapshot?[]? s_cacheSnapshots;
    [ThreadStatic]
    private static int s_cacheNext;

    private VulkanMeshDrawViewSnapshot(
        ulong packetId,
        ulong renderFrameId,
        ulong scopedBindingRevision,
        XRCamera? camera,
        XRCamera? rightEyeCamera,
        bool isStereoPass,
        bool useUnjitteredProjection,
        Matrix4x4 viewMatrix,
        Matrix4x4 inverseViewMatrix,
        Matrix4x4 projectionMatrix,
        Matrix4x4 inverseProjectionMatrix,
        Matrix4x4 viewProjectionMatrix,
        Matrix4x4 viewProjectionMatrixUnjittered,
        Matrix4x4 previousViewMatrix,
        Matrix4x4 previousProjectionMatrix,
        Matrix4x4 previousViewProjectionMatrix,
        Matrix4x4 previousViewProjectionMatrixUnjittered,
        Matrix4x4 rightEyeViewMatrix,
        Matrix4x4 rightEyeInverseViewMatrix,
        Matrix4x4 rightEyeProjectionMatrix,
        Matrix4x4 rightEyeInverseProjectionMatrix,
        Matrix4x4 rightEyeViewProjectionMatrix,
        Matrix4x4 rightEyeViewProjectionMatrixUnjittered,
        Matrix4x4 previousRightEyeViewMatrix,
        Matrix4x4 previousRightEyeProjectionMatrix,
        Matrix4x4 previousRightEyeViewProjectionMatrix,
        Matrix4x4 previousRightEyeViewProjectionMatrixUnjittered,
        bool leftEyeHistoryReady,
        bool rightEyeHistoryReady,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraUp,
        Vector3 cameraRight,
        int renderAreaWidth,
        int renderAreaHeight,
        int descriptorViewFamilyIdentity,
        in LayeredShadowUniformState shadowUniformState)
    {
        PacketId = packetId;
        RenderFrameId = renderFrameId;
        ScopedBindingRevision = scopedBindingRevision;
        Camera = camera;
        RightEyeCamera = rightEyeCamera;
        IsStereoPass = isStereoPass;
        UseUnjitteredProjection = useUnjitteredProjection;
        ViewMatrix = viewMatrix;
        InverseViewMatrix = inverseViewMatrix;
        ProjectionMatrix = projectionMatrix;
        InverseProjectionMatrix = inverseProjectionMatrix;
        ViewProjectionMatrix = viewProjectionMatrix;
        ViewProjectionMatrixUnjittered = viewProjectionMatrixUnjittered;
        PreviousViewMatrix = previousViewMatrix;
        PreviousProjectionMatrix = previousProjectionMatrix;
        PreviousViewProjectionMatrix = previousViewProjectionMatrix;
        PreviousViewProjectionMatrixUnjittered =
            previousViewProjectionMatrixUnjittered;
        RightEyeViewMatrix = rightEyeViewMatrix;
        RightEyeInverseViewMatrix = rightEyeInverseViewMatrix;
        RightEyeProjectionMatrix = rightEyeProjectionMatrix;
        RightEyeInverseProjectionMatrix = rightEyeInverseProjectionMatrix;
        RightEyeViewProjectionMatrix = rightEyeViewProjectionMatrix;
        RightEyeViewProjectionMatrixUnjittered =
            rightEyeViewProjectionMatrixUnjittered;
        PreviousRightEyeViewMatrix = previousRightEyeViewMatrix;
        PreviousRightEyeProjectionMatrix = previousRightEyeProjectionMatrix;
        PreviousRightEyeViewProjectionMatrix =
            previousRightEyeViewProjectionMatrix;
        PreviousRightEyeViewProjectionMatrixUnjittered =
            previousRightEyeViewProjectionMatrixUnjittered;
        LeftEyeHistoryReady = leftEyeHistoryReady;
        RightEyeHistoryReady = rightEyeHistoryReady;
        CameraPosition = cameraPosition;
        CameraForward = cameraForward;
        CameraUp = cameraUp;
        CameraRight = cameraRight;
        RenderAreaWidth = renderAreaWidth;
        RenderAreaHeight = renderAreaHeight;
        DescriptorViewFamilyIdentity = descriptorViewFamilyIdentity;
        ShadowUniformState = shadowUniformState;
    }

    internal ulong PacketId { get; }
    internal ulong RenderFrameId { get; }
    internal ulong ScopedBindingRevision { get; }
    internal XRCamera? Camera { get; }
    internal XRCamera? RightEyeCamera { get; }
    internal bool IsStereoPass { get; }
    internal bool UseUnjitteredProjection { get; }
    internal Matrix4x4 ViewMatrix { get; }
    internal Matrix4x4 InverseViewMatrix { get; }
    internal Matrix4x4 ProjectionMatrix { get; }
    internal Matrix4x4 InverseProjectionMatrix { get; }
    internal Matrix4x4 ViewProjectionMatrix { get; }
    internal Matrix4x4 ViewProjectionMatrixUnjittered { get; }
    internal Matrix4x4 PreviousViewMatrix { get; }
    internal Matrix4x4 PreviousProjectionMatrix { get; }
    internal Matrix4x4 PreviousViewProjectionMatrix { get; }
    internal Matrix4x4 PreviousViewProjectionMatrixUnjittered { get; }
    internal Matrix4x4 RightEyeViewMatrix { get; }
    internal Matrix4x4 RightEyeInverseViewMatrix { get; }
    internal Matrix4x4 RightEyeProjectionMatrix { get; }
    internal Matrix4x4 RightEyeInverseProjectionMatrix { get; }
    internal Matrix4x4 RightEyeViewProjectionMatrix { get; }
    internal Matrix4x4 RightEyeViewProjectionMatrixUnjittered { get; }
    internal Matrix4x4 PreviousRightEyeViewMatrix { get; }
    internal Matrix4x4 PreviousRightEyeProjectionMatrix { get; }
    internal Matrix4x4 PreviousRightEyeViewProjectionMatrix { get; }
    internal Matrix4x4 PreviousRightEyeViewProjectionMatrixUnjittered { get; }
    internal bool LeftEyeHistoryReady { get; }
    internal bool RightEyeHistoryReady { get; }
    internal Vector3 CameraPosition { get; }
    internal Vector3 CameraForward { get; }
    internal Vector3 CameraUp { get; }
    internal Vector3 CameraRight { get; }
    internal int RenderAreaWidth { get; }
    internal int RenderAreaHeight { get; }
    internal int DescriptorViewFamilyIdentity { get; }
    internal LayeredShadowUniformState ShadowUniformState { get; }

    /// <summary>
    /// Captures one immutable render-scope publication, or returns the current
    /// thread's publication when consecutive draws share its exact authority.
    /// Render-frame and scoped-binding generations make camera and layered
    /// shadow changes start a new publication.
    /// </summary>
    internal static VulkanMeshDrawViewSnapshot Capture(
        XRRenderPipelineInstance? pipeline,
        XRCamera? camera,
        XRCamera? rightEyeCamera,
        bool isStereoPass,
        bool useUnjitteredProjection,
        int passIndex,
        XRFrameBuffer? target,
        in VulkanMeshProducerSnapshot producer,
        in LayeredShadowUniformState shadowUniformState)
    {
        ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        ulong scopedBindingRevision = RuntimeEngine.Rendering.State
            .RenderingPipelineState?.ScopedBindingRevision ?? 0UL;
        var renderArea = RuntimeEngine.Rendering.State.RenderArea;
        int renderAreaWidth = renderArea.Width;
        int renderAreaHeight = renderArea.Height;
        int descriptorViewFamilyIdentity =
            producer.Context.OutputTargetIdentity != 0
                ? producer.Context.OutputTargetIdentity
                : producer.Context.ViewportIdentity;
        if (renderAreaWidth <= 0 || renderAreaHeight <= 0)
        {
            renderAreaWidth = (int)producer.TargetExtent.Width;
            renderAreaHeight = (int)producer.TargetExtent.Height;
        }

        // The snapshot reads camera, render-area and layered shadow state only.
        // Per-draw scoped bindings change ScopedBindingRevision for almost every
        // draw, so the key uses the layered shadow revision.
        ulong layeredShadowRevision = RuntimeEngine.Rendering.State
            .RenderingPipelineState?.LayeredShadowRevision ?? 0UL;
        VulkanMeshDrawViewSnapshotCacheKey cacheKey = new(
            renderFrameId,
            layeredShadowRevision,
            pipeline,
            camera,
            rightEyeCamera,
            renderAreaWidth,
            renderAreaHeight,
            descriptorViewFamilyIdentity,
            isStereoPass,
            useUnjitteredProjection,
            in shadowUniformState);
        VulkanMeshDrawViewSnapshotCacheKey[] cacheKeys = s_cacheKeys ??= new VulkanMeshDrawViewSnapshotCacheKey[CacheCapacity];
        VulkanMeshDrawViewSnapshot?[] cacheSnapshots = s_cacheSnapshots ??= new VulkanMeshDrawViewSnapshot?[CacheCapacity];
        for (int index = 0; index < CacheCapacity; index++)
        {
            if (cacheSnapshots[index] is { } cachedSnapshot &&
                cacheKeys[index].Matches(in cacheKey))
            {
                return cachedSnapshot;
            }
        }

        Matrix4x4 viewMatrix =
            camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseViewMatrix =
            camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 projectionMatrix =
            useUnjitteredProjection && camera is not null
                ? camera.ProjectionMatrixUnjittered
                : camera?.ProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseProjectionMatrix =
            useUnjitteredProjection && camera is not null
                ? camera.InverseProjectionMatrixUnjittered
                : camera?.InverseProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrix =
            useUnjitteredProjection && camera is not null
                ? camera.ViewProjectionMatrixUnjittered
                : camera?.ViewProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrixUnjittered =
            camera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrix;
        Matrix4x4 rightEyeViewMatrix =
            rightEyeCamera?.Transform.InverseRenderMatrix ?? viewMatrix;
        Matrix4x4 rightEyeInverseViewMatrix =
            rightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrix;
        Matrix4x4 rightEyeProjectionMatrix =
            useUnjitteredProjection && rightEyeCamera is not null
                ? rightEyeCamera.ProjectionMatrixUnjittered
                : rightEyeCamera?.ProjectionMatrix ?? projectionMatrix;
        Matrix4x4 rightEyeInverseProjectionMatrix =
            useUnjitteredProjection && rightEyeCamera is not null
                ? rightEyeCamera.InverseProjectionMatrixUnjittered
                : rightEyeCamera?.InverseProjectionMatrix ??
                  inverseProjectionMatrix;
        Matrix4x4 rightEyeViewProjectionMatrix =
            useUnjitteredProjection && rightEyeCamera is not null
                ? rightEyeCamera.ViewProjectionMatrixUnjittered
                : rightEyeCamera?.ViewProjectionMatrix ??
                  viewProjectionMatrix;
        Matrix4x4 rightEyeViewProjectionMatrixUnjittered =
            rightEyeCamera?.ViewProjectionMatrixUnjittered ??
            viewProjectionMatrixUnjittered;
        Matrix4x4 previousViewMatrix = viewMatrix;
        Matrix4x4 previousProjectionMatrix = projectionMatrix;
        Matrix4x4 previousViewProjectionMatrix = viewProjectionMatrix;
        Matrix4x4 previousViewProjectionMatrixUnjittered =
            camera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrix;
        Matrix4x4 previousRightEyeViewMatrix = rightEyeViewMatrix;
        Matrix4x4 previousRightEyeProjectionMatrix =
            rightEyeProjectionMatrix;
        Matrix4x4 previousRightEyeViewProjectionMatrix =
            rightEyeViewProjectionMatrix;
        Matrix4x4 previousRightEyeViewProjectionMatrixUnjittered =
            rightEyeCamera?.ViewProjectionMatrixUnjittered ??
            rightEyeViewProjectionMatrix;
        bool leftEyeHistoryReady = camera?.HasPreviousViewProjectionMatrix == true;
        bool rightEyeHistoryReady = rightEyeCamera?.HasPreviousViewProjectionMatrix == true;
        if (leftEyeHistoryReady)
            previousViewProjectionMatrixUnjittered = camera!.PreviousViewProjectionMatrixUnjittered;
        if (rightEyeHistoryReady)
            previousRightEyeViewProjectionMatrixUnjittered = rightEyeCamera!.PreviousViewProjectionMatrixUnjittered;
        if (pipeline?.RenderState.FrameViewSet is { } frameViews)
        {
            leftEyeHistoryReady = false;
            rightEyeHistoryReady = false;
            previousViewMatrix = viewMatrix;
            previousProjectionMatrix = projectionMatrix;
            previousViewProjectionMatrix = viewProjectionMatrix;
            previousViewProjectionMatrixUnjittered = viewProjectionMatrixUnjittered;
            previousRightEyeViewMatrix = rightEyeViewMatrix;
            previousRightEyeProjectionMatrix = rightEyeProjectionMatrix;
            previousRightEyeViewProjectionMatrix = rightEyeViewProjectionMatrix;
            previousRightEyeViewProjectionMatrixUnjittered = rightEyeViewProjectionMatrixUnjittered;
            if (RenderFrameViewMatch.TryGetExactEyeView(in frameViews, camera, rightEye: false, out RenderFrameViewDescriptor leftView))
            {
                Matrix4x4 unjitteredProjection = leftView.ProjectionMatrixUnjittered == default
                    ? leftView.ProjectionMatrix : leftView.ProjectionMatrixUnjittered;
                viewProjectionMatrixUnjittered = leftView.ViewMatrix * unjitteredProjection;
                leftEyeHistoryReady = leftView.HasValidTemporalHistory &&
                    leftView.PreviousViewProjectionMatrixUnjittered != default;
                previousViewProjectionMatrixUnjittered = leftEyeHistoryReady
                    ? leftView.PreviousViewProjectionMatrixUnjittered : viewProjectionMatrixUnjittered;
            }
            if (RenderFrameViewMatch.TryGetExactEyeView(in frameViews, rightEyeCamera, rightEye: true, out RenderFrameViewDescriptor rightView))
            {
                Matrix4x4 unjitteredProjection = rightView.ProjectionMatrixUnjittered == default
                    ? rightView.ProjectionMatrix : rightView.ProjectionMatrixUnjittered;
                rightEyeViewProjectionMatrixUnjittered = rightView.ViewMatrix * unjitteredProjection;
                rightEyeHistoryReady = rightView.HasValidTemporalHistory &&
                    rightView.PreviousViewProjectionMatrixUnjittered != default;
                previousRightEyeViewProjectionMatrixUnjittered = rightEyeHistoryReady
                    ? rightView.PreviousViewProjectionMatrixUnjittered : rightEyeViewProjectionMatrixUnjittered;
            }
        }
        if (pipeline is not null &&
            (pipeline.RenderState.FrameViewSet is null ||
             pipeline.RenderState.TemporalAuthoringViewSet is not null) &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(
                pipeline,
                out var temporalData))
        {
            viewProjectionMatrixUnjittered =
                temporalData.CurrViewProjectionUnjittered;
            rightEyeViewProjectionMatrixUnjittered =
                temporalData.RightEyeCurrViewProjectionUnjittered;
            leftEyeHistoryReady = temporalData.LeftEyeHistoryReady;
            rightEyeHistoryReady = temporalData.RightEyeHistoryReady;
            if (leftEyeHistoryReady || rightEyeHistoryReady)
            {
                previousViewMatrix = temporalData.PrevViewMatrix;
                previousProjectionMatrix = temporalData.PrevProjection;
                previousViewProjectionMatrix =
                    temporalData.PrevViewProjection;
                previousViewProjectionMatrixUnjittered =
                    temporalData.PrevViewProjectionUnjittered;
                previousRightEyeViewMatrix =
                    temporalData.RightEyePrevViewMatrix;
                previousRightEyeProjectionMatrix =
                    temporalData.RightEyePrevProjection;
                previousRightEyeViewProjectionMatrix =
                    temporalData.RightEyePrevViewProjection;
                previousRightEyeViewProjectionMatrixUnjittered =
                    temporalData.RightEyePrevViewProjectionUnjittered;
            }
            if (!leftEyeHistoryReady)
                previousViewProjectionMatrixUnjittered = viewProjectionMatrixUnjittered;
            if (!rightEyeHistoryReady)
                previousRightEyeViewProjectionMatrixUnjittered = rightEyeViewProjectionMatrixUnjittered;
        }

        // One immutable snapshot is allocated per distinct view/pass/frame and
        // shared by every draw, replacing hundreds of multi-kilobyte copies.
        // Frame-slot ownership or pooling can remove this final bounded allocation.
        VulkanMeshDrawViewSnapshot snapshot = new(
            NextPacketId(),
            renderFrameId,
            scopedBindingRevision,
            camera,
            rightEyeCamera,
            isStereoPass,
            useUnjitteredProjection,
            viewMatrix,
            inverseViewMatrix,
            projectionMatrix,
            inverseProjectionMatrix,
            viewProjectionMatrix,
            viewProjectionMatrixUnjittered,
            previousViewMatrix,
            previousProjectionMatrix,
            previousViewProjectionMatrix,
            previousViewProjectionMatrixUnjittered,
            rightEyeViewMatrix,
            rightEyeInverseViewMatrix,
            rightEyeProjectionMatrix,
            rightEyeInverseProjectionMatrix,
            rightEyeViewProjectionMatrix,
            rightEyeViewProjectionMatrixUnjittered,
            previousRightEyeViewMatrix,
            previousRightEyeProjectionMatrix,
            previousRightEyeViewProjectionMatrix,
            previousRightEyeViewProjectionMatrixUnjittered,
            leftEyeHistoryReady,
            rightEyeHistoryReady,
            camera?.Transform.RenderTranslation ?? Vector3.Zero,
            camera?.Transform.RenderForward ?? Vector3.UnitZ,
            camera?.Transform.RenderUp ?? Vector3.UnitY,
            camera?.Transform.RenderRight ?? Vector3.UnitX,
            renderAreaWidth,
            renderAreaHeight,
            descriptorViewFamilyIdentity,
            shadowUniformState);
        // Replace the oldest entry. Entries from earlier render frames never
        // match, because the key holds the render-frame id.
        int slot = s_cacheNext;
        s_cacheNext = slot + 1 == CacheCapacity ? 0 : slot + 1;
        cacheKeys[slot] = cacheKey;
        cacheSnapshots[slot] = snapshot;
        return snapshot;
    }

    private static ulong NextPacketId()
    {
        long next = Interlocked.Increment(ref s_nextPacketId);
        if (next > 0)
            return unchecked((ulong)next);

        Interlocked.Exchange(ref s_nextPacketId, 1);
        return 1UL;
    }
}
