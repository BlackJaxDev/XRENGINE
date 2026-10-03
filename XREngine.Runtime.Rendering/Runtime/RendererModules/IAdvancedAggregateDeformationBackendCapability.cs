namespace XREngine.Rendering;

/// <summary>
/// Backend lowering of canonical aggregate inputs with bounded packed bindings, exact authored
/// influence caps and morph thresholds, and morph-only jobs. Desktop's legacy GLSL route does not
/// implement this capability and retains its existing bindings and deformation semantics.
/// </summary>
public interface IAdvancedAggregateDeformationBackendCapability
{
    bool SupportsAggregateDeformation { get; }

    ERendererComputeEnqueueStatus TryDispatchAggregateDeformation(
        AdvancedGpuDeformationResources resources,
        in AdvancedDeformationDispatchBatch batch);
}
