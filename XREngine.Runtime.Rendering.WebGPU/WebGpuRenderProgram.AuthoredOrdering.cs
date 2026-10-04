using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Copies each readable storage image on the GPU and retains the exact remaining descriptor owners.</summary>
    internal WebGpuBindingSet SnapshotAuthoredRasterBindings(WebGpuBindingSet source, WebGpuAuthoredRasterSnapshot snapshot,
        WebGpuOwnedStorageBuffer? orderingRanks = null)
    {
        if (source.IsDisposed || Artifact.ComputeEntryPoint is not null)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.RasterBindingsRequired: ordered raster requires a live authored raster binding set.");
        source.ResourceHandles.CopyTo(_resourceHandles);
        source.ResourceSizes.CopyTo(_resourceSizes);
        source.ResourceOwners.CopyTo(_resourceOwners);
        try
        {
            for (int index = 0; index < Artifact.Resources.Length; index++)
            {
                ShaderStageResourceLayout resource = Artifact.Resources[index];
                AbstractRenderAPIObject? owner = _resourceOwners[index];
                if (resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer)
                {
                    if (resource.BindingType != "read-only-storage")
                        throw new NotSupportedException("WebGPU.AuthoredOrdering.WritableRasterStorage: ordered candidates require read-only raster storage inputs.");
                    if (orderingRanks is not null && ReferenceEquals(owner, orderingRanks) &&
                        EngineAuthoredOrderGateContract.IsGateProgram(Artifact) &&
                        EngineAuthoredOrderGateContract.IsRankResource(resource))
                        continue;
                    uint size = owner switch
                    {
                        WebGpuDataBuffer data => checked((uint)data.BackendAllocatedByteSize),
                        WebGpuOwnedStorageBuffer owned => owned.ByteLength,
                        _ => throw new NotSupportedException("WebGPU.AuthoredOrdering.StorageOwner: raster storage must retain its exact copyable owner."),
                    };
                    WebGpuOwnedStorageBuffer copy = snapshot.CaptureBuffer(owner!, _resourceHandles[index], size);
                    _resourceHandles[index] = copy.ResourceHandle;
                    _resourceOwners[index] = copy;
                }
                else if (resource.Contract.Kind is ShaderAbiResourceKind.SampledImage or ShaderAbiResourceKind.StorageImage)
                {
                    if (resource.Contract.Kind == ShaderAbiResourceKind.StorageImage && IsWritableImage(index))
                        throw new NotSupportedException("WebGPU.AuthoredOrdering.WritableRasterImage: ordered candidates require immutable sampled images.");
                    if (owner is null) throw new InvalidOperationException("An ordered sampled image has no physical owner.");
                    Renderer.RetainAuthoredOrderingTexture(owner);
                }
            }
            SetNativeRasterBindingCacheOwner(snapshot.Arguments, 0, snapshot.Revision);
            if (!TrySnapshotBindings(false, out WebGpuBindingSet? result) || result is null)
                throw new InvalidOperationException("Ordered raster resources could not be retained.");
            return result;
        }
        finally
        {
            source.ResourceHandles.CopyTo(_resourceHandles);
            source.ResourceSizes.CopyTo(_resourceSizes);
            source.ResourceOwners.CopyTo(_resourceOwners);
            SetField(ref _bindingCacheOwner, null, publishNotifications: false);
            SetField(ref _bindingCacheIndex, 0u, publishNotifications: false);
            SetField(ref _bindingCacheRevision, 0ul, publishNotifications: false);
        }
    }
}
