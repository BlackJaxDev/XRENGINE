namespace XREngine.Rendering.Meshlets;

/// <summary>Selects how the exponent filter shares exponents. The values match the native meshopt_EncodeExpMode enum.</summary>
public enum MeshOptimizerExpMode
{
    /// <summary>Each component has its own exponent. This gives the best quality.</summary>
    Separate = 0,
    /// <summary>All components of a vector share one exponent. This gives better compression.</summary>
    SharedVector = 1,
    /// <summary>Each component shares one exponent across all vectors. This gives the best compression.</summary>
    SharedComponent = 2,
    /// <summary>Each component has its own exponent, and the exponent is clamped to zero.</summary>
    Clamped = 3,
}
