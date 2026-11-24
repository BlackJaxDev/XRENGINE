namespace XREngine.Components;

/// <summary>
/// Selects how gravity acts on a chain.
/// </summary>
public enum EPhysicsChainGravityMode
{
    /// <summary>Gravity is a world-space movement per step, scaled by the object. Falloff removes a fixed part of it.</summary>
    Force,
    /// <summary>
    /// Gravity scales with each segment length, so the settled angle depends on the gravity and pull ratio and not on the bone size.
    /// Falloff has its full effect at rest and less effect as the segment turns away from rest.
    /// </summary>
    SegmentRatio,
    /// <summary>
    /// Gravity biases each segment's rest direction toward gravity by its magnitude (zero keeps the rest pose;
    /// one aligns with gravity), and pull moves the segment toward that biased target. Zero pull produces no gravity motion.
    /// </summary>
    RestBias,
}
