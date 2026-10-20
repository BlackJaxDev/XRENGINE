namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>A backend resource shape paired with engine ownership, physical members, and stage visibility.</summary>
public sealed record ShaderStageResourceLayout(
    ShaderAbiResourceContract Contract,
    ShaderStageVisibility Visibility,
    string BindingType,
    bool DynamicOffset)
{
    /// <summary>Storage is a runtime-sized array whose declared byte size and members describe one element.</summary>
    public bool RuntimeArray { get; internal init; }
}
