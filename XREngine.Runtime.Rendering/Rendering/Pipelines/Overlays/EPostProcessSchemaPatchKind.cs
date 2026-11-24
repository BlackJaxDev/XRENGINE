namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>The change that a <see cref="PostProcessSchemaPatch"/> makes to a base post-process schema.</summary>
public enum EPostProcessSchemaPatchKind
{
    /// <summary>Adds a library stage or a custom stage.</summary>
    AddStage,

    /// <summary>Removes a stage from the schema that the camera editor shows.</summary>
    HideStage,

    /// <summary>Changes the default value of one stage parameter.</summary>
    OverrideDefault,

    /// <summary>Moves a stage to a category.</summary>
    MoveToCategory,
}
