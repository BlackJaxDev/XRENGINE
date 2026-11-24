using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Allocation-free ground surface query abstraction used by
/// <see cref="HumanoidFootPlacementIKComponent"/> so live scenes query Jolt/PhysX geometry
/// while unit tests can inject deterministic slope or stair surfaces.
/// </summary>
public interface IFootGroundProber
{
    /// <summary>
    /// Probes downward from above <paramref name="footWorldPosition"/> to locate the supporting
    /// ground surface point and unit normal.
    /// </summary>
    bool TryProbeGround(
        Vector3 footWorldPosition,
        float probeUpMeters,
        float probeDownMeters,
        out Vector3 groundPoint,
        out Vector3 groundNormal);
}
