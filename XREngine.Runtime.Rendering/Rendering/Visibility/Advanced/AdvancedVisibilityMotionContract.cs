namespace XREngine.Rendering;

/// <summary>
/// Resolves dense-velocity validity from current/previous geometry and history events.
/// </summary>
public static class AdvancedVisibilityMotionContract
{
    public static EAdvancedVelocityValidityReason Resolve(
        bool newSurface,
        bool teleported,
        bool topologyChanged,
        bool vertexCountChanged,
        bool historyReset,
        bool arenaOverflow,
        bool frameGap)
    {
        if (newSurface)
            return EAdvancedVelocityValidityReason.NewlyVisible;
        if (teleported)
            return EAdvancedVelocityValidityReason.Teleported;
        if (topologyChanged)
            return EAdvancedVelocityValidityReason.TopologyChanged;
        if (vertexCountChanged)
            return EAdvancedVelocityValidityReason.VertexCountChanged;
        if (historyReset)
            return EAdvancedVelocityValidityReason.HistoryReset;
        if (arenaOverflow)
            return EAdvancedVelocityValidityReason.ArenaOverflow;
        if (frameGap)
            return EAdvancedVelocityValidityReason.FrameGap;
        return EAdvancedVelocityValidityReason.Valid;
    }
}
