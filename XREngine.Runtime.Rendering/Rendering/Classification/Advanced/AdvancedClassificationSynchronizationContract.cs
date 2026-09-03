using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Synchronization boundaries and barrier descriptors for GPU material classification.
/// </summary>
public static class AdvancedClassificationSynchronizationContract
{
    public const string BoundaryVisibilityToClassification = "AdvancedClassification.VisibilityToClassification";
    public const string BoundaryClassificationToShading = "AdvancedClassification.ClassificationToShading";

    /// <summary>
    /// Barrier requirements transitioning visibility attachments to classification compute shader inputs.
    /// </summary>
    public static readonly RenderPipelineResourceUsage VisibilityInputUsage =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage;

    /// <summary>
    /// Barrier requirements transitioning classification buffers to indirect dispatch arguments and shading inputs.
    /// </summary>
    public static readonly RenderPipelineResourceUsage ClassificationOutputUsage =
        RenderPipelineResourceUsage.StorageBuffer | RenderPipelineResourceUsage.IndirectBuffer;
}
