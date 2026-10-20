namespace XREngine.Components.Lights;

/// <summary>
/// Identifies the first term that makes a directional light need its primary shadow view.
/// </summary>
/// <remarks>
/// The primary shadow test evaluates the terms in this order: <see cref="CascadesDisabled"/>, then
/// <see cref="NoCascadeReceiver"/> when the backend cannot render cascades. On the shadow atlas path the test then uses
/// the decision that the atlas planning recorded in the same collect pass: <see cref="CascadesDisabled"/> or
/// <see cref="NoPublishedCascades"/>, <see cref="PrimaryViewport"/> or <see cref="NoRelevantViewport"/>,
/// <see cref="PendingShadowCapture"/>, and last <see cref="ShadowOnlyCasters"/>. Without a recorded atlas decision the
/// test uses <see cref="NoCascadeReceiver"/>, <see cref="NoPublishedCascades"/>, <see cref="NoRenderWorld"/>, and the
/// viewport test.
/// </remarks>
internal enum EDirectionalPrimaryShadowNeed : byte
{
    /// <summary>No term applies. Neither the atlas planning nor a legacy receiver needs the primary shadow view.</summary>
    None,

    /// <summary>The light has cascaded shadows disabled.</summary>
    CascadesDisabled,

    /// <summary>
    /// The backend cannot render cascades, or, without a recorded atlas decision, no cascade source has a receiver
    /// texture. In both cases the primary shadow map is the only directional receiver.
    /// </summary>
    NoCascadeReceiver,

    /// <summary>No cascade source has published cascades.</summary>
    NoPublishedCascades,

    /// <summary>The light has no render world.</summary>
    NoRenderWorld,

    /// <summary>
    /// An active window or VR eye viewport that targets the world reads the primary shadow map. Its camera does not
    /// prefer cascades, or the light has no published cascades for the cascade source of that camera.
    /// </summary>
    PrimaryViewport,

    /// <summary>No active window or VR eye viewport with a camera targets the world.</summary>
    NoRelevantViewport,

    /// <summary>
    /// The light uses cascades, but scene capture work is pending. A capture camera does not use cascades, so it reads
    /// the primary shadow map.
    /// </summary>
    PendingShadowCapture,

    /// <summary>
    /// No consumer reads the primary shadow map, but the scene can have shadow-casting renderables that no main view
    /// collected. The primary shadow view is then the only CPU collection that refreshes their GPU scene rows, which
    /// the GPU-driven cascades read.
    /// </summary>
    ShadowOnlyCasters,
}
