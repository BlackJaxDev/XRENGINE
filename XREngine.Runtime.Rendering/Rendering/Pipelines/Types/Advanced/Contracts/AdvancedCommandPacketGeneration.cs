namespace XREngine.Rendering;

/// <summary>
/// Structural command-packet generations. These are the only generation channels
/// that invalidate an otherwise reusable recorded packet.
/// </summary>
public readonly record struct AdvancedCommandPacketGeneration(
    ulong Topology,
    ulong Capacity,
    ulong Binding,
    ulong Shader,
    ulong Resource);
