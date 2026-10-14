namespace XREngine.Components.Lights;

/// <summary>
/// Identifies the first term that makes a directional light need its primary shadow view.
/// The primary shadow test evaluates the terms in the order of the members after <see cref="None"/>.
/// </summary>
internal enum EDirectionalPrimaryShadowNeed : byte
{
    /// <summary>No term applies. The cascades replace the primary shadow view.</summary>
    None,

    /// <summary>The light has cascaded shadows disabled.</summary>
    CascadesDisabled,

    /// <summary>
    /// No cascade source has a receiver texture. On the Vulkan cascade atlas path the light releases the receiver
    /// textures, so this term applies in each frame on that path.
    /// </summary>
    NoCascadeReceiver,

    /// <summary>No cascade source has published cascades.</summary>
    NoPublishedCascades,

    /// <summary>The light has no render world.</summary>
    NoRenderWorld,

    /// <summary>An active window or VR eye viewport that targets the world prefers the primary shadow map.</summary>
    PrimaryViewport,

    /// <summary>No active window or VR eye viewport with a camera targets the world.</summary>
    NoRelevantViewport,
}
