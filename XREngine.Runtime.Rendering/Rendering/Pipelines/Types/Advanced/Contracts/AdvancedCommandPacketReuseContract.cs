namespace XREngine.Rendering;

/// <summary>
/// Determines whether a recorded advanced command packet remains structurally reusable.
/// Frame-data generations are intentionally excluded: buffers/images are refreshed through
/// stable bindings without rebuilding command topology.
/// </summary>
public static class AdvancedCommandPacketReuseContract
{
    /// <summary>
    /// Returns every structural channel that changed between recording and execution.
    /// </summary>
    public static EAdvancedCommandPacketInvalidation GetInvalidation(
        in AdvancedCommandPacketState recorded,
        in AdvancedCommandPacketState current)
    {
        AdvancedCommandPacketGeneration left = recorded.CommandPacket;
        AdvancedCommandPacketGeneration right = current.CommandPacket;
        EAdvancedCommandPacketInvalidation result =
            EAdvancedCommandPacketInvalidation.None;

        if (left.Topology != right.Topology)
            result |= EAdvancedCommandPacketInvalidation.Topology;
        if (left.Capacity != right.Capacity)
            result |= EAdvancedCommandPacketInvalidation.Capacity;
        if (left.Binding != right.Binding)
            result |= EAdvancedCommandPacketInvalidation.Binding;
        if (left.Shader != right.Shader)
            result |= EAdvancedCommandPacketInvalidation.Shader;
        if (left.Resource != right.Resource)
            result |= EAdvancedCommandPacketInvalidation.Resource;

        return result;
    }

    /// <summary>
    /// Returns whether no structural generation changed.
    /// </summary>
    public static bool CanReuse(
        in AdvancedCommandPacketState recorded,
        in AdvancedCommandPacketState current)
        => GetInvalidation(recorded, current) ==
           EAdvancedCommandPacketInvalidation.None;
}
