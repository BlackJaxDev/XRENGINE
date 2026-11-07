namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>The value that a <see cref="RenderBranchCondition"/> reads.</summary>
public enum ERenderBranchConditionSource
{
    /// <summary>The fixed <see cref="RenderBranchCondition.Value"/>. The key is not used.</summary>
    Constant,

    /// <summary>A bool, int, uint, or float variable of the rendering pipeline instance, found by key.</summary>
    PipelineVariable,

    /// <summary>A setting that the pipeline registers in <see cref="RenderPipeline.BranchSettings"/>, found by key.</summary>
    PipelineSetting,

    /// <summary>Whether the post-process stage with the key is present and enabled for the rendering camera.</summary>
    StageEnabled,

    /// <summary>A render state or backend capability. The key is an <see cref="ERenderBranchCapability"/> name.</summary>
    Capability,
}
