namespace XREngine.Rendering.GI.DDGI;

/// <summary>Debug visualization modes for DDGI volume and screen sampling passes.</summary>
public enum EDDGIDebugMode
{
    /// <summary>Normal DDGI diffuse indirect lighting added to scene.</summary>
    None = 0,
    /// <summary>Isolates DDGI diffuse contribution, suppressing direct lighting.</summary>
    DDGIOnly = 1,
    /// <summary>Visualizes probe grid cell boundaries and trilinear fractions.</summary>
    ProbeNeighborhood = 2,
    /// <summary>Visualizes cascade indices and smooth boundary blend transitions.</summary>
    CascadeCoverage = 3,
}
