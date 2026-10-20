namespace XREngine.Rendering.Shadows;

/// <summary>
/// Why a main-view GPU collection counted a renderable as a shadow-only caster candidate.
/// </summary>
/// <remarks>
/// The scene classifies the first four values only while a diagnostic capture records. The classification repeats the
/// checks of the base <see cref="XREngine.Rendering.Info.RenderInfo3D.AllowRender"/> method in order and stops at the
/// first check that fails.
/// </remarks>
public enum EShadowOnlyCasterCandidateReason
{
    /// <summary>The main camera does not render the layer of the renderable.</summary>
    LayerRejected,

    /// <summary>The renderable belongs to a mirror capture, and the collection excludes mirrors.</summary>
    MirrorRejected,

    /// <summary>The renderable is outside the collection volume.</summary>
    VolumeRejected,

    /// <summary>
    /// <see cref="XREngine.Rendering.Info.RenderInfo3D.AllowRender"/> rejected the renderable for a different reason,
    /// for example an override in a subclass.
    /// </summary>
    AllowRenderRejected,

    /// <summary>
    /// The pre-collection callback rejected the main camera, and the callback can accept a different camera. For
    /// example, an HLOD source that a far camera hides.
    /// </summary>
    CallbackRejected,

    /// <summary>
    /// The main view collected the renderable without its primary command, because the material enables the shadow
    /// pass but not the base pass.
    /// </summary>
    ShadowPassOnly,
}
