using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact per-frame view/pass scope for Vulkan forward-light binding capture.
/// Reference identity is intentional because render resources and cameras own
/// independent mutation and lifetime state.
/// </summary>
internal readonly struct ForwardLightingBindingSnapshotCacheKey :
    IEquatable<ForwardLightingBindingSnapshotCacheKey>
{
    private readonly object _lights;
    private readonly XRRenderPipelineInstance? _pipeline;
    private readonly XRCamera? _camera;
    private readonly XRCamera? _rightEyeCamera;
    private readonly object? _world;
    private readonly XRFrameBuffer? _target;
    private readonly int _passIndex;
    private readonly int _renderAreaX;
    private readonly int _renderAreaY;
    private readonly int _renderAreaWidth;
    private readonly int _renderAreaHeight;
    private readonly bool _stereo;
    private readonly bool _unjitteredProjection;

    public ForwardLightingBindingSnapshotCacheKey(
        object lights,
        XRRenderPipelineInstance? pipeline,
        XRCamera? camera,
        XRCamera? rightEyeCamera,
        object? world,
        XRFrameBuffer? target,
        int passIndex,
        int renderAreaX,
        int renderAreaY,
        int renderAreaWidth,
        int renderAreaHeight,
        bool stereo,
        bool unjitteredProjection)
    {
        _lights = lights;
        _pipeline = pipeline;
        _camera = camera;
        _rightEyeCamera = rightEyeCamera;
        _world = world;
        _target = target;
        _passIndex = passIndex;
        _renderAreaX = renderAreaX;
        _renderAreaY = renderAreaY;
        _renderAreaWidth = renderAreaWidth;
        _renderAreaHeight = renderAreaHeight;
        _stereo = stereo;
        _unjitteredProjection = unjitteredProjection;
    }

    public bool Equals(ForwardLightingBindingSnapshotCacheKey other)
        => ReferenceEquals(_lights, other._lights) &&
           ReferenceEquals(_pipeline, other._pipeline) &&
           ReferenceEquals(_camera, other._camera) &&
           ReferenceEquals(_rightEyeCamera, other._rightEyeCamera) &&
           ReferenceEquals(_world, other._world) &&
           ReferenceEquals(_target, other._target) &&
           _passIndex == other._passIndex &&
           _renderAreaX == other._renderAreaX &&
           _renderAreaY == other._renderAreaY &&
           _renderAreaWidth == other._renderAreaWidth &&
           _renderAreaHeight == other._renderAreaHeight &&
           _stereo == other._stereo &&
           _unjitteredProjection == other._unjitteredProjection;

    public override bool Equals(object? obj)
        => obj is ForwardLightingBindingSnapshotCacheKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RuntimeHelpers.GetHashCode(_lights));
        hash.Add(ReferenceHash(_pipeline));
        hash.Add(ReferenceHash(_camera));
        hash.Add(ReferenceHash(_rightEyeCamera));
        hash.Add(ReferenceHash(_world));
        hash.Add(ReferenceHash(_target));
        hash.Add(_passIndex);
        hash.Add(_renderAreaX);
        hash.Add(_renderAreaY);
        hash.Add(_renderAreaWidth);
        hash.Add(_renderAreaHeight);
        hash.Add(_stereo);
        hash.Add(_unjitteredProjection);
        return hash.ToHashCode();
    }

    private static int ReferenceHash(object? value)
        => value is null ? 0 : RuntimeHelpers.GetHashCode(value);
}
