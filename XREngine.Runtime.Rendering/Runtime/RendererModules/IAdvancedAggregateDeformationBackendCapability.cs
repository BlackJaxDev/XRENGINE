namespace XREngine.Rendering;

/// <summary>
/// Backend lowering of canonical aggregate inputs with bounded packed bindings, exact authored
/// influence caps and morph thresholds, and morph-only jobs. Desktop's legacy GLSL route does not
/// implement this capability and retains its existing bindings and deformation semantics.
/// </summary>
public interface IAdvancedAggregateDeformationBackendCapability
{
    bool SupportsAggregateDeformation { get; }

    /// <summary>
    /// Captures an already resident compact GPU palette generation without uploading
    /// its CPU mirror. Returning false preserves the explicit unsupported-path gate.
    /// </summary>
    bool TryCaptureAggregateGpuPaletteCopy(XRDataBuffer source, uint sourceByteOffset,
        uint destinationByteOffset, uint byteLength, out AdvancedGpuDeformationPaletteCopy copy)
    {
        copy = default;
        return false;
    }

    ERendererComputeEnqueueStatus TryDispatchAggregateDeformation(
        AdvancedGpuDeformationResources resources,
        in AdvancedDeformationDispatchBatch batch);
}
