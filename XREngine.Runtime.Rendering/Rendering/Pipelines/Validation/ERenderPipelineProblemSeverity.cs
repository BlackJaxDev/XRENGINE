namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>The severity of a render pipeline problem.</summary>
public enum ERenderPipelineProblemSeverity
{
    /// <summary>A hint. The pipeline works, but it can be cleaner.</summary>
    Info,

    /// <summary>The pipeline can render incorrectly or waste memory.</summary>
    Warning,

    /// <summary>The pipeline renders incorrectly or fails to build.</summary>
    Error,
}
