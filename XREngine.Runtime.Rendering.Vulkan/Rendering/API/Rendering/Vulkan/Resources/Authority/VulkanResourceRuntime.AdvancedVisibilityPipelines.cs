namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private readonly object _advancedVisibilityPipelinesGate = new();
    private VulkanAdvancedVisibilityPipelineRuntime? _advancedVisibilityPipelines;
    private bool _advancedVisibilityPipelinePreparationStopped;

    /// <summary>Generation-owned executable compute lane for advanced visibility.</summary>
    internal VulkanAdvancedVisibilityPipelineRuntime AdvancedVisibilityPipelines
    {
        get
        {
            lock (_advancedVisibilityPipelinesGate)
            {
                if (_advancedVisibilityPipelinePreparationStopped)
                    throw new InvalidOperationException(
                        "Advanced visibility pipeline preparation has stopped for this renderer generation.");
                return _advancedVisibilityPipelines ??=
                    new VulkanAdvancedVisibilityPipelineRuntime(this);
            }
        }
    }

    internal void StopAdvancedVisibilityPipelinePreparation()
    {
        VulkanAdvancedVisibilityPipelineRuntime? pipelines;
        lock (_advancedVisibilityPipelinesGate)
        {
            _advancedVisibilityPipelinePreparationStopped = true;
            pipelines = _advancedVisibilityPipelines;
        }
        pipelines?.StopPreparation();
    }

    internal AdvancedVisibilityPreparationDiagnosticsSnapshot CaptureAdvancedVisibilityPreparationDiagnostics()
    {
        lock (_advancedVisibilityPipelinesGate)
            return _advancedVisibilityPipelines?.CapturePreparationDiagnostics() ?? default;
    }
}
