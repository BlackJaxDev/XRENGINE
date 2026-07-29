namespace XREngine.Rendering;

/// <summary>
/// Defined value returned when a logical resource is invalid or nonresident.
/// </summary>
public enum EAdvancedResourceFallback : uint
{
    Zero = 0,
    White = 1,
    Black = 2,
    FlatNormal = 3,
    OpaqueBlack = 4,
    Identity = 5,
}
