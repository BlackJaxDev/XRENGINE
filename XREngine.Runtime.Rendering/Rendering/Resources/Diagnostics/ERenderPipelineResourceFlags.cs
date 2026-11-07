namespace XREngine.Rendering.Resources.Diagnostics;

/// <summary>
/// Describes the state of one resource in a <see cref="RenderPipelineResourceSnapshot"/>.
/// </summary>
[Flags]
public enum ERenderPipelineResourceFlags
{
    /// <summary>No flag is set.</summary>
    None = 0,
    /// <summary>The resource layout declares the resource.</summary>
    Declared = 1 << 0,
    /// <summary>At least one render pass reads or writes the resource.</summary>
    Used = 1 << 1,
    /// <summary>The resource keeps its content across frames.</summary>
    History = 1 << 2,
    /// <summary>The resource comes from outside the pipeline, or the pipeline imports it.</summary>
    External = 1 << 3,
    /// <summary>The resource exists only while the frame uses it.</summary>
    Transient = 1 << 4,
    /// <summary>The generation holds a physical instance of the resource.</summary>
    Materialized = 1 << 5,
    /// <summary>The predicate of the declaration returned false for the profile of the generation.</summary>
    Disabled = 1 << 6,
    /// <summary>The declaration marks the resource as required.</summary>
    Required = 1 << 7,
    /// <summary>The descriptor of the resource allows the backend to alias its memory with other transient resources.</summary>
    AliasCandidate = 1 << 8,
}
