using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>One change to the resource layout of a base pipeline.</summary>
public sealed class RenderResourcePatch
{
    /// <summary>Gets or sets the change kind.</summary>
    public ERenderResourcePatchKind Kind { get; set; }

    /// <summary>Gets or sets the resource name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the declaration of an add or an override.</summary>
    public AuthoredRenderResource? Resource { get; set; }

    /// <summary>Returns a deep copy of this patch.</summary>
    public RenderResourcePatch Clone()
        => new() { Kind = Kind, Name = Name, Resource = Resource?.Clone() };

    /// <summary>Returns a short text, such as <c>override HDRSceneTex</c>.</summary>
    public string Describe()
        => Kind switch
        {
            ERenderResourcePatchKind.Add => string.Concat("add resource ", Name),
            ERenderResourcePatchKind.Override => string.Concat("override resource ", Name),
            _ => string.Concat("remove resource ", Name),
        };

    /// <inheritdoc />
    public override string ToString()
        => Describe();
}
