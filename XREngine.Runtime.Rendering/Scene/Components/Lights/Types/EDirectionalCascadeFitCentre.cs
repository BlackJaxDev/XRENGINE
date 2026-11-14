namespace XREngine.Components.Lights;

/// <summary>
/// Selects the centre of the bounding sphere that a directional cascade fits around its
/// slice of the camera frustum.
/// </summary>
public enum EDirectionalCascadeFitCentre
{
    /// <summary>
    /// Centres the sphere on the centroid of the slice corners, ahead of the camera on its
    /// view axis. The sphere is the smallest of the two, so the texels are the finest, but
    /// it moves when the camera turns, and a turn refits the cascades.
    /// </summary>
    ViewCentroid = 0,

    /// <summary>
    /// Centres the sphere on the camera position (the head for a stereo source), with a
    /// radius to the farthest slice corner. A turn does not move the fit, so it does not
    /// refit the cascades. The sphere is larger, so the texels are 21-29% coarser in the
    /// default cascade split.
    /// </summary>
    CameraPosition = 1,
}
