namespace XREngine.Rendering.Commands;

/// <summary>
/// Independent mutation domains for one canonical resident owner. Consumers
/// must key only the domains their resource actually reads.
/// </summary>
public readonly record struct AdvancedGpuOwnerGenerations(
    ulong Topology,
    ulong Content,
    ulong Lookup)
{
    public static readonly AdvancedGpuOwnerGenerations Initial = new(1u, 1u, 1u);
}
