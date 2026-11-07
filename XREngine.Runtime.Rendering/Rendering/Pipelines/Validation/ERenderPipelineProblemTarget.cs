namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>The kind of object that a render pipeline problem points to.</summary>
public enum ERenderPipelineProblemTarget
{
    /// <summary>The whole pipeline.</summary>
    Pipeline,

    /// <summary>A command.</summary>
    Command,

    /// <summary>A render pass.</summary>
    Pass,

    /// <summary>A resource.</summary>
    Resource,

    /// <summary>A post-process schema stage.</summary>
    Stage,
}
