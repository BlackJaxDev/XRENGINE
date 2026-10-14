namespace XREngine.Rendering.Shadows;

/// <summary>
/// Cumulative counts of the first term that made the primary shadow view necessary, at each primary collection test
/// of a directional light collection.
/// </summary>
/// <remarks>
/// The test stops at the first term that applies, so a later term is not evaluated in that call. The sum of all counts
/// is the number of light collections. On the shadow atlas path the test uses the decision that the atlas planning
/// recorded in the same collect pass, so <paramref name="NoRenderWorld"/> does not apply there, and
/// <paramref name="NoCascadeReceiver"/> applies only when the backend cannot render cascades.
/// </remarks>
/// <param name="None">No term applied. The primary shadow view did not collect.</param>
/// <param name="CascadesDisabled">The light had cascaded shadows disabled.</param>
/// <param name="NoCascadeReceiver">
/// The backend could not render cascades, or, without an atlas decision, no cascade source had a receiver texture.
/// </param>
/// <param name="NoPublishedCascades">No cascade source had published cascades.</param>
/// <param name="NoRenderWorld">The light had no render world.</param>
/// <param name="PrimaryViewport">
/// A window or VR eye viewport that targets the world read the primary shadow map: its camera did not prefer cascades,
/// or the light had no published cascades for the cascade source of that camera.
/// </param>
/// <param name="NoRelevantViewport">No window or VR eye viewport with a camera targeted the world.</param>
/// <param name="PendingShadowCapture">
/// The light used cascades, but the atlas planning found pending scene capture work, which reads the primary shadow map.
/// </param>
/// <param name="ShadowOnlyCasters">
/// No consumer read the primary shadow map, but the scene could have shadow-casting renderables that no main view
/// collected. The primary shadow view then refreshed their GPU scene rows for the GPU-driven cascades.
/// </param>
public readonly record struct DirectionalPrimaryShadowNeedCounts(
    long None,
    long CascadesDisabled,
    long NoCascadeReceiver,
    long NoPublishedCascades,
    long NoRenderWorld,
    long PrimaryViewport,
    long NoRelevantViewport,
    long PendingShadowCapture,
    long ShadowOnlyCasters);
