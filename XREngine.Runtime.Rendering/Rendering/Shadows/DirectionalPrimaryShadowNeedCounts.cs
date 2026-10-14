namespace XREngine.Rendering.Shadows;

/// <summary>
/// Cumulative counts of the first term that made the primary shadow view necessary, at each primary collection test
/// of a directional light collection.
/// </summary>
/// <remarks>
/// The test evaluates the terms in the order of the parameters after <paramref name="None"/>, and it stops at the
/// first term that applies. A later term is therefore not evaluated in that call. The sum of all counts is the number
/// of light collections.
/// </remarks>
/// <param name="None">No term applied. The cascades replaced the primary shadow view.</param>
/// <param name="CascadesDisabled">The light had cascaded shadows disabled.</param>
/// <param name="NoCascadeReceiver">
/// No cascade source had a receiver texture. On the Vulkan cascade atlas path this term applies in each frame,
/// because that path releases the receiver textures.
/// </param>
/// <param name="NoPublishedCascades">No cascade source had published cascades.</param>
/// <param name="NoRenderWorld">The light had no render world.</param>
/// <param name="PrimaryViewport">A window or VR eye viewport that targets the world preferred the primary shadow map.</param>
/// <param name="NoRelevantViewport">No window or VR eye viewport with a camera targeted the world.</param>
public readonly record struct DirectionalPrimaryShadowNeedCounts(
    long None,
    long CascadesDisabled,
    long NoCascadeReceiver,
    long NoPublishedCascades,
    long NoRenderWorld,
    long PrimaryViewport,
    long NoRelevantViewport);
