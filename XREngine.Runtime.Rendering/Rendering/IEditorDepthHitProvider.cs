using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Allows procedural or non-mesh renderable components (such as infinite reference grids)
/// to provide analytical depth hits to the editor camera for dragging, zooming, and orbiting.
/// </summary>
public interface IEditorDepthHitProvider
{
    /// <summary>
    /// Attempts to calculate a depth hit on this provider for a normalized viewport coordinate.
    /// </summary>
    /// <param name="viewport">The active viewport.</param>
    /// <param name="normalizedViewportPoint">The normalized [0, 1] viewport coordinate.</param>
    /// <param name="worldHitPoint">The world-space position of the hit surface.</param>
    /// <param name="depth">The normalized camera depth buffer value (0 to 1) corresponding to the hit point.</param>
    /// <param name="hitPlane">The index or identifier of the specific hit plane or surface.</param>
    /// <returns>True if the ray intersects an active surface within its valid visible distance.</returns>
    bool TryGetDepthHit(
        XRViewport viewport,
        Vector2 normalizedViewportPoint,
        out Vector3 worldHitPoint,
        out float depth,
        out int hitPlane);
}
