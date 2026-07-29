namespace XREngine.Rendering;

/// <summary>
/// Exact structural generation changes that require command-packet re-recording.
/// </summary>
[Flags]
public enum EAdvancedCommandPacketInvalidation
{
    None = 0,
    Topology = 1 << 0,
    Capacity = 1 << 1,
    Binding = 1 << 2,
    Shader = 1 << 3,
    Resource = 1 << 4,
}
