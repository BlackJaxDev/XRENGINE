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
    [ThreadStatic]
    private static VulkanMeshDrawViewSnapshot? s_cachedSnapshot;
    [ThreadStatic]
    private static ulong s_cachedRenderFrameId;
    [ThreadStatic]
    private static ulong s_cachedRecordingFingerprint;
    [ThreadStatic]
    private static ulong s_cachedScopedBindingRevision;
    [ThreadStatic]
    private static XRRenderPipelineInstance? s_cachedPipeline;
    [ThreadStatic]
    private static XRCamera? s_cachedCamera;
    [ThreadStatic]
    private static XRCamera? s_cachedRightEyeCamera;
    [ThreadStatic]
    private static XRFrameBuffer? s_cachedTarget;
    [ThreadStatic]
    private static int s_cachedPassIndex;
    [ThreadStatic]
    private static int s_cachedRenderAreaWidth;
    [ThreadStatic]
    private static int s_cachedRenderAreaHeight;
    [ThreadStatic]
    private static bool s_cachedIsStereoPass;
    [ThreadStatic]
    private static bool s_cachedUseUnjitteredProjection;
    [ThreadStatic]
    private static bool s_cachedIsShadowPass;
    [ThreadStatic]
    private static bool s_cachedDirectionalLayeredShadowPass;
    [ThreadStatic]
    private static int s_cachedDirectionalShadowLayerCount;
    [ThreadStatic]
    private static bool s_cachedPointLayeredShadowPass;
    [ThreadStatic]
    private static int s_cachedPointShadowFaceCount;

    private VulkanMeshDrawViewSnapshot(
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
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraUp,
        Vector3 cameraRight,
        int renderAreaWidth,
        int renderAreaHeight,
        in LayeredShadowUniformState shadowUniformState)
    {
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
        CameraPosition = cameraPosition;
        CameraForward = cameraForward;
        CameraUp = cameraUp;
        CameraRight = cameraRight;
        RenderAreaWidth = renderAreaWidth;
        RenderAreaHeight = renderAreaHeight;
        ShadowUniformState = shadowUniformState;
    }

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
    internal Vector3 CameraPosition { get; }
    internal Vector3 CameraForward { get; }
    internal Vector3 CameraUp { get; }
    internal Vector3 CameraRight { get; }
    internal int RenderAreaWidth { get; }
    internal int RenderAreaHeight { get; }
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
        if (renderAreaWidth <= 0 || renderAreaHeight <= 0)
        {
            if (target is not null)
            {
                renderAreaWidth = (int)target.Width;
                renderAreaHeight = (int)target.Height;
            }
            else
            {
                renderAreaWidth = (int)producer.TargetExtent.Width;
                renderAreaHeight = (int)producer.TargetExtent.Height;
            }
        }

        if (s_cachedSnapshot is not null &&
            s_cachedRenderFrameId == renderFrameId &&
            s_cachedRecordingFingerprint ==
                producer.Context.RecordingFingerprint &&
            s_cachedScopedBindingRevision == scopedBindingRevision &&
            ReferenceEquals(s_cachedPipeline, pipeline) &&
            ReferenceEquals(s_cachedCamera, camera) &&
            ReferenceEquals(s_cachedRightEyeCamera, rightEyeCamera) &&
            ReferenceEquals(s_cachedTarget, target) &&
            s_cachedPassIndex == passIndex &&
            s_cachedRenderAreaWidth == renderAreaWidth &&
            s_cachedRenderAreaHeight == renderAreaHeight &&
            s_cachedIsStereoPass == isStereoPass &&
            s_cachedUseUnjitteredProjection ==
                useUnjitteredProjection &&
            s_cachedIsShadowPass == shadowUniformState.IsShadowPass &&
            s_cachedDirectionalLayeredShadowPass ==
                shadowUniformState
                    .DirectionalCascadeInstancedLayeredShadowPass &&
            s_cachedDirectionalShadowLayerCount ==
                shadowUniformState.DirectionalCascadeShadowLayerCount &&
            s_cachedPointLayeredShadowPass ==
                shadowUniformState.PointLightInstancedLayeredShadowPass &&
            s_cachedPointShadowFaceCount ==
                shadowUniformState.PointLightShadowFaceCount)
        {
            return s_cachedSnapshot;
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
        if (pipeline is not null &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(
                pipeline,
                out var temporalData))
        {
            viewProjectionMatrixUnjittered =
                temporalData.CurrViewProjectionUnjittered;
            rightEyeViewProjectionMatrixUnjittered =
                temporalData.RightEyeCurrViewProjectionUnjittered;
            if (temporalData.HistoryReady)
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
        }

        // One immutable snapshot is allocated per distinct view/pass/frame and
        // shared by every draw, replacing hundreds of multi-kilobyte copies.
        // Frame-slot ownership or pooling can remove this final bounded allocation.
        VulkanMeshDrawViewSnapshot snapshot = new(
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
            camera?.Transform.RenderTranslation ?? Vector3.Zero,
            camera?.Transform.RenderForward ?? Vector3.UnitZ,
            camera?.Transform.RenderUp ?? Vector3.UnitY,
            camera?.Transform.RenderRight ?? Vector3.UnitX,
            renderAreaWidth,
            renderAreaHeight,
            shadowUniformState);
        s_cachedSnapshot = snapshot;
        s_cachedRenderFrameId = renderFrameId;
        s_cachedRecordingFingerprint =
            producer.Context.RecordingFingerprint;
        s_cachedScopedBindingRevision = scopedBindingRevision;
        s_cachedPipeline = pipeline;
        s_cachedCamera = camera;
        s_cachedRightEyeCamera = rightEyeCamera;
        s_cachedTarget = target;
        s_cachedPassIndex = passIndex;
        s_cachedRenderAreaWidth = renderAreaWidth;
        s_cachedRenderAreaHeight = renderAreaHeight;
        s_cachedIsStereoPass = isStereoPass;
        s_cachedUseUnjitteredProjection = useUnjitteredProjection;
        s_cachedIsShadowPass = shadowUniformState.IsShadowPass;
        s_cachedDirectionalLayeredShadowPass = shadowUniformState
            .DirectionalCascadeInstancedLayeredShadowPass;
        s_cachedDirectionalShadowLayerCount =
            shadowUniformState.DirectionalCascadeShadowLayerCount;
        s_cachedPointLayeredShadowPass =
            shadowUniformState.PointLightInstancedLayeredShadowPass;
        s_cachedPointShadowFaceCount =
            shadowUniformState.PointLightShadowFaceCount;
        return snapshot;
    }
}
