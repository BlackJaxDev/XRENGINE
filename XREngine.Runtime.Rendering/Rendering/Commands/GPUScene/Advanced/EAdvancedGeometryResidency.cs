namespace XREngine.Rendering.Commands;

public enum EAdvancedGeometryResidency : uint
{
    Missing = 0u,
    Pending = 1u,
    Resident = 2u,
    Evicted = 3u,
    Unsupported = 4u,
}
