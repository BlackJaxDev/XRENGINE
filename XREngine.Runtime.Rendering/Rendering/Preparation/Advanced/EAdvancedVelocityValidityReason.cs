namespace XREngine.Rendering;

/// <summary>
/// Reason current/previous deformation cannot produce valid velocity.
/// </summary>
public enum EAdvancedVelocityValidityReason : uint
{
    Valid = 0u,
    NewlyVisible = 1u,
    FrameGap = 2u,
    TopologyChanged = 3u,
    VertexCountChanged = 4u,
    ArenaOverflow = 5u,
}
