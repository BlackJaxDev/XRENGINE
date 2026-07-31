using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact render-scope identity for a shareable, immutable material binding
/// snapshot. Reference equality is intentional: two engine objects that happen
/// to compare by value do not share mutation or lifetime ownership.
/// </summary>
internal readonly struct MaterialBindingSnapshotCacheKey : IEquatable<MaterialBindingSnapshotCacheKey>
{
    private readonly XRMaterial _material;
    private readonly XRRenderPipelineInstance? _pipeline;
    private readonly XRCamera? _camera;
    private readonly XRCamera? _rightEyeCamera;
    private readonly object? _renderingWorld;
    private readonly XRFrameBuffer? _target;
    private readonly ulong _materialLayoutVersion;
    private readonly ulong _materialValueVersion;
    private readonly long _materialShaderRevision;
    private readonly long _materialUberRevision;
    private readonly ulong _programLinkGeneration;
    private readonly ulong _scopedBindingRevision;
    private readonly int _passIndex;
    private readonly int _renderAreaX;
    private readonly int _renderAreaY;
    private readonly int _renderAreaWidth;
    private readonly int _renderAreaHeight;
    private readonly bool _stereo;
    private readonly bool _unjitteredProjection;

    internal MaterialBindingSnapshotCacheKey(
        XRMaterial material,
        XRRenderPipelineInstance? pipeline,
        XRCamera? camera,
        XRCamera? rightEyeCamera,
        object? renderingWorld,
        XRFrameBuffer? target,
        ulong programLinkGeneration,
        ulong scopedBindingRevision,
        int passIndex,
        int renderAreaX,
        int renderAreaY,
        int renderAreaWidth,
        int renderAreaHeight,
        bool stereo,
        bool unjitteredProjection)
    {
        _material = material;
        _pipeline = pipeline;
        _camera = camera;
        _rightEyeCamera = rightEyeCamera;
        _renderingWorld = renderingWorld;
        _target = target;
        _materialLayoutVersion = material.BindingLayoutVersion;
        _materialValueVersion = material.BindingValueVersion;
        _materialShaderRevision = material.ShaderStateRevision;
        _materialUberRevision = material.UberStateRevision;
        _programLinkGeneration = programLinkGeneration;
        _scopedBindingRevision = scopedBindingRevision;
        _passIndex = passIndex;
        _renderAreaX = renderAreaX;
        _renderAreaY = renderAreaY;
        _renderAreaWidth = renderAreaWidth;
        _renderAreaHeight = renderAreaHeight;
        _stereo = stereo;
        _unjitteredProjection = unjitteredProjection;
    }

    public bool Equals(MaterialBindingSnapshotCacheKey other)
        => ReferenceEquals(_material, other._material) &&
           ReferenceEquals(_pipeline, other._pipeline) &&
           ReferenceEquals(_camera, other._camera) &&
           ReferenceEquals(_rightEyeCamera, other._rightEyeCamera) &&
           ReferenceEquals(_renderingWorld, other._renderingWorld) &&
           ReferenceEquals(_target, other._target) &&
           _materialLayoutVersion == other._materialLayoutVersion &&
           _materialValueVersion == other._materialValueVersion &&
           _materialShaderRevision == other._materialShaderRevision &&
           _materialUberRevision == other._materialUberRevision &&
           _programLinkGeneration == other._programLinkGeneration &&
           _scopedBindingRevision == other._scopedBindingRevision &&
           _passIndex == other._passIndex &&
           _renderAreaX == other._renderAreaX &&
           _renderAreaY == other._renderAreaY &&
           _renderAreaWidth == other._renderAreaWidth &&
           _renderAreaHeight == other._renderAreaHeight &&
           _stereo == other._stereo &&
           _unjitteredProjection == other._unjitteredProjection;

    public override bool Equals(object? obj)
        => obj is MaterialBindingSnapshotCacheKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RuntimeHelpers.GetHashCode(_material));
        hash.Add(ReferenceHash(_pipeline));
        hash.Add(ReferenceHash(_camera));
        hash.Add(ReferenceHash(_rightEyeCamera));
        hash.Add(ReferenceHash(_renderingWorld));
        hash.Add(ReferenceHash(_target));
        hash.Add(_materialLayoutVersion);
        hash.Add(_materialValueVersion);
        hash.Add(_materialShaderRevision);
        hash.Add(_materialUberRevision);
        hash.Add(_programLinkGeneration);
        hash.Add(_scopedBindingRevision);
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
