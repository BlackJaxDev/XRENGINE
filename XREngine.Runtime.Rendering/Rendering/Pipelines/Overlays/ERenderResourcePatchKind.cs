namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>The change that a <see cref="RenderResourcePatch"/> makes to a base resource layout.</summary>
public enum ERenderResourcePatchKind
{
    /// <summary>Declares a new resource.</summary>
    Add,

    /// <summary>Replaces the declaration of a base resource with the same name.</summary>
    Override,

    /// <summary>Removes a base resource.</summary>
    Remove,
}
