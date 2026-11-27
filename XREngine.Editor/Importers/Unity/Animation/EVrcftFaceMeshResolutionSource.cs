namespace XREngine.Scene.Importers;

/// <summary>
/// Identifies which rule selected the avatar face mesh during VRCFT template attachment.
/// </summary>
public enum EVrcftFaceMeshResolutionSource
{
    /// <summary>No target face mesh was found on the avatar hierarchy.</summary>
    None = 0,

    /// <summary>Selected from the caller's explicit node-path override.</summary>
    ExplicitOverride = 1,

    /// <summary>Selected from <c>AvatarPresentationComponent.VisemeRenderer</c>.</summary>
    AvatarPresentationVisemeRenderer = 2,

    /// <summary>Selected from a descendant node named <c>Body</c> with a <c>ModelComponent</c>.</summary>
    NamedBodyFallback = 3,

    /// <summary>Selected by matching Unified Expressions, ARKit, or SRanipal blendshape names.</summary>
    BlendshapeMatchFallback = 4,
}
