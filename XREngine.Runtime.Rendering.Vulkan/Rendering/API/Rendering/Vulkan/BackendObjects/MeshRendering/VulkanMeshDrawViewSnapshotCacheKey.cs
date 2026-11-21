namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the inputs of one <see cref="VulkanMeshDrawViewSnapshot"/>.
/// Two captures with an equal key in the same render frame produce the same
/// snapshot, so the later capture reuses the earlier one. The key holds only
/// what the snapshot reads. A matching frozen view and projection choice must
/// stay equal before a later draw can reuse the snapshot.
/// </summary>
internal readonly struct VulkanMeshDrawViewSnapshotCacheKey(
    ulong renderFrameId,
    ulong layeredShadowRevision,
    XRRenderPipelineInstance? pipeline,
    XRCamera? camera,
    XRCamera? rightEyeCamera,
    RenderFrameViewDescriptor? frozenLeftView,
    RenderFrameViewDescriptor? frozenRightView,
    int renderAreaWidth,
    int renderAreaHeight,
    int descriptorViewFamilyIdentity,
    bool isStereoPass,
    bool useUnjitteredProjection,
    bool useLeftUnjitteredProjection,
    bool useRightUnjitteredProjection,
    in LayeredShadowUniformState shadowUniformState)
{
    private readonly ulong _renderFrameId = renderFrameId;
    private readonly ulong _layeredShadowRevision = layeredShadowRevision;
    private readonly XRRenderPipelineInstance? _pipeline = pipeline;
    private readonly XRCamera? _camera = camera;
    private readonly XRCamera? _rightEyeCamera = rightEyeCamera;
    private readonly RenderFrameViewDescriptor? _frozenLeftView = frozenLeftView;
    private readonly RenderFrameViewDescriptor? _frozenRightView = frozenRightView;
    private readonly int _renderAreaWidth = renderAreaWidth;
    private readonly int _renderAreaHeight = renderAreaHeight;
    private readonly int _descriptorViewFamilyIdentity = descriptorViewFamilyIdentity;
    private readonly bool _isStereoPass = isStereoPass;
    private readonly bool _useUnjitteredProjection = useUnjitteredProjection;
    private readonly bool _useLeftUnjitteredProjection = useLeftUnjitteredProjection;
    private readonly bool _useRightUnjitteredProjection = useRightUnjitteredProjection;
    private readonly bool _isShadowPass = shadowUniformState.IsShadowPass;
    private readonly bool _directionalLayeredShadowPass = shadowUniformState.DirectionalCascadeLayeredShadowPass;
    private readonly bool _directionalInstancedLayeredShadowPass = shadowUniformState.DirectionalCascadeInstancedLayeredShadowPass;
    private readonly bool _directionalAtlasGroupedShadowPass = shadowUniformState.DirectionalCascadeAtlasGroupedShadowPass;
    private readonly int _directionalShadowLayerCount = shadowUniformState.DirectionalCascadeShadowLayerCount;
    private readonly bool _pointLayeredShadowPass = shadowUniformState.PointLightLayeredShadowPass;
    private readonly bool _pointInstancedLayeredShadowPass = shadowUniformState.PointLightInstancedLayeredShadowPass;
    private readonly bool _pointAtlasGroupedShadowPass = shadowUniformState.PointLightAtlasGroupedShadowPass;
    private readonly int _pointShadowFaceCount = shadowUniformState.PointLightShadowFaceCount;

    /// <summary>Compares every field; object fields compare by reference.</summary>
    public bool Matches(in VulkanMeshDrawViewSnapshotCacheKey other)
        => _renderFrameId == other._renderFrameId &&
           _layeredShadowRevision == other._layeredShadowRevision &&
           ReferenceEquals(_pipeline, other._pipeline) &&
           ReferenceEquals(_camera, other._camera) &&
           ReferenceEquals(_rightEyeCamera, other._rightEyeCamera) &&
           _renderAreaWidth == other._renderAreaWidth &&
           _renderAreaHeight == other._renderAreaHeight &&
           _descriptorViewFamilyIdentity == other._descriptorViewFamilyIdentity &&
           _isStereoPass == other._isStereoPass &&
           _useUnjitteredProjection == other._useUnjitteredProjection &&
           _isShadowPass == other._isShadowPass &&
           _directionalLayeredShadowPass == other._directionalLayeredShadowPass &&
           _directionalInstancedLayeredShadowPass == other._directionalInstancedLayeredShadowPass &&
           _directionalAtlasGroupedShadowPass == other._directionalAtlasGroupedShadowPass &&
           _directionalShadowLayerCount == other._directionalShadowLayerCount &&
           _pointLayeredShadowPass == other._pointLayeredShadowPass &&
           _pointInstancedLayeredShadowPass == other._pointInstancedLayeredShadowPass &&
           _pointAtlasGroupedShadowPass == other._pointAtlasGroupedShadowPass &&
           _pointShadowFaceCount == other._pointShadowFaceCount &&
           _useLeftUnjitteredProjection == other._useLeftUnjitteredProjection &&
           _useRightUnjitteredProjection == other._useRightUnjitteredProjection &&
           _frozenLeftView == other._frozenLeftView &&
           _frozenRightView == other._frozenRightView;

    public ulong RenderFrameId => _renderFrameId;
}
