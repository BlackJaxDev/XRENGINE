using System;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// Holds the GPU sample buffer and source mesh references for one avatar submesh slice evaluated by
/// <c>GaussianLiveSurfaceSplats.comp</c>.
/// </summary>
internal sealed class GaussianAvatarSubmeshSlice : IDisposable
{
    private bool _disposed;
    private uint _pendingPushByteLength;

    public GaussianAvatarSubmeshSlice(
        RenderableComponent component,
        RenderableMesh renderableMesh,
        XRMeshRenderer sourceRenderer,
        XRMesh sourceMesh,
        XRDataBuffer samplesBuffer,
        uint sampleCount,
        uint baseSplatSlot,
        uint pendingPushByteLength)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(renderableMesh);
        ArgumentNullException.ThrowIfNull(sourceRenderer);
        ArgumentNullException.ThrowIfNull(sourceMesh);
        ArgumentNullException.ThrowIfNull(samplesBuffer);

        Component = component;
        RenderableMesh = renderableMesh;
        SourceRenderer = sourceRenderer;
        SourceMesh = sourceMesh;
        SamplesBuffer = samplesBuffer;
        SampleCount = sampleCount;
        BaseSplatSlot = baseSplatSlot;
        _pendingPushByteLength = pendingPushByteLength;
    }

    /// <summary>Component that owns <see cref="RenderableMesh"/> and supplies the world transform for unskinned meshes.</summary>
    public RenderableComponent Component { get; }

    /// <summary>Renderable submesh entry on <see cref="Component"/>.</summary>
    public RenderableMesh RenderableMesh { get; }

    /// <summary>Source LOD0 renderer that owns skinning and blendshape resources.</summary>
    public XRMeshRenderer SourceRenderer { get; }

    /// <summary>Source mesh geometry sampled by <see cref="SamplesBuffer"/>.</summary>
    public XRMesh SourceMesh { get; }

    /// <summary>GPU storage buffer of <see cref="SampleCount"/> <see cref="GaussianLiveSurfaceGpuSample"/> elements.</summary>
    public XRDataBuffer SamplesBuffer { get; }

    /// <summary>Number of splats emitted by this submesh slice.</summary>
    public uint SampleCount { get; }

    /// <summary>First destination slot in the shared splat buffer.</summary>
    public uint BaseSplatSlot { get; }

    /// <summary>Uploads the staged CPU sample bytes to the GPU storage buffer on the render thread.</summary>
    public void EnsureGpuUploaded()
    {
        if (_pendingPushByteLength == 0u || _disposed)
            return;

        SamplesBuffer.PushSubData(0, _pendingPushByteLength);
        _pendingPushByteLength = 0u;
    }

    /// <summary>Releases <see cref="SamplesBuffer"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SamplesBuffer.Dispose();
    }
}
