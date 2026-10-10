using XREngine.Data.Core;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns exposure history for exactly one physical output texture generation.</summary>
internal sealed class WebGpuAutoExposureHistory : IDisposable
{
    private readonly ObjectCacheOwnership _ownership;
    private readonly WebGpuTexture2D _target;
    private readonly int _targetHandle;
    private readonly string _artifactIdentity;

    internal WebGpuAutoExposureHistory(WebGpuRendererHost renderer, WebGpuTexture2D target, ShaderProgramArtifact artifact)
    {
        WebGpuAutoExposureProgramContract.Validate(artifact);
        _target = target;
        _targetHandle = target.ResourceHandle;
        _artifactIdentity = artifact.Identity;
        using ObjectCachePublicationScope ownership = XRObjectBase.BeginIndependentObjectCachePublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        Program = new XRRenderProgram { Name = artifact.Name, CookedArtifact = artifact };
        Storage = new WebGpuOwnedStorageBuffer(renderer, "GPU exposure history");
        try
        {
            // WebGPU initializes new buffers to zero. The shader owns both the
            // current value and initialized marker; no host exposure upload/readback.
            Storage.EnsureCapacity(16);
            _ownership = ownership.CompleteWithOwnership();
        }
        catch
        {
            Storage.Dispose();
            throw;
        }
    }

    internal XRRenderProgram Program { get; }
    internal WebGpuOwnedStorageBuffer Storage { get; }
    internal bool IsObsolete => _target.IsRetired || _target.Data.IsDestroyed ||
        !_target.IsCurrentGpuAllocationForCopy || _target.ResourceHandle != _targetHandle;
    internal bool Matches(WebGpuTexture2D target, ShaderProgramArtifact artifact)
        => ReferenceEquals(target, _target) && target.ResourceHandle == _targetHandle &&
            artifact.Identity == _artifactIdentity && !IsObsolete;

    public void Dispose()
    {
        try { Storage.Dispose(); }
        finally { _ownership.Dispose(); }
    }
}
