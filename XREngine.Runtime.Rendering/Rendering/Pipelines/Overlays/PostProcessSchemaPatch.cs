using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>One change to the post-process schema of a base pipeline.</summary>
public sealed class PostProcessSchemaPatch
{
    /// <summary>Gets or sets the change kind.</summary>
    public EPostProcessSchemaPatchKind Kind { get; set; }

    /// <summary>Gets or sets the key of the stage that the patch changes or adds.</summary>
    public string StageKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the declaration of <see cref="EPostProcessSchemaPatchKind.AddStage"/>.</summary>
    public AuthoredPostProcessStage? Stage { get; set; }

    /// <summary>Gets or sets the parameter of <see cref="EPostProcessSchemaPatchKind.OverrideDefault"/>.</summary>
    public string? ParameterName { get; set; }

    /// <summary>Gets or sets the script value text of <see cref="EPostProcessSchemaPatchKind.OverrideDefault"/>.</summary>
    public string? ValueText { get; set; }

    /// <summary>Gets or sets the category key of <see cref="EPostProcessSchemaPatchKind.MoveToCategory"/>.</summary>
    public string? CategoryKey { get; set; }

    /// <summary>Returns a deep copy of this patch.</summary>
    public PostProcessSchemaPatch Clone()
        => new()
        {
            Kind = Kind,
            StageKey = StageKey,
            Stage = Stage?.Clone(),
            ParameterName = ParameterName,
            ValueText = ValueText,
            CategoryKey = CategoryKey,
        };

    /// <summary>Returns a short text, such as <c>hide bloom</c>.</summary>
    public string Describe()
        => Kind switch
        {
            EPostProcessSchemaPatchKind.AddStage => string.Concat("add stage ", StageKey),
            EPostProcessSchemaPatchKind.HideStage => string.Concat("hide stage ", StageKey),
            EPostProcessSchemaPatchKind.OverrideDefault => string.Concat("default ", StageKey, ".", ParameterName),
            _ => string.Concat("category ", StageKey, " = ", CategoryKey),
        };

    /// <inheritdoc />
    public override string ToString()
        => Describe();
}
