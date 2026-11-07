namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Marks a command property that holds the name of a pipeline resource. Editors show a resource picker for the
/// property, and a resource rename updates every marked property that holds the old name.
/// </summary>
/// <param name="kind">The kind of resource that the property names.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class RenderResourceNameAttribute(ERenderResourceNameKind kind = ERenderResourceNameKind.Any) : Attribute
{
    /// <summary>Gets the kind of resource that the property names.</summary>
    public ERenderResourceNameKind Kind { get; } = kind;
}
