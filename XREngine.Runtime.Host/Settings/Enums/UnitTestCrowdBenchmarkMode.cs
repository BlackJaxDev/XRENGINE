namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// The avatar render mode of the crowd benchmark. Each mode sets the virtual geometry settings
/// of the Advanced pipeline, so runs of the same crowd compare one avatar path with another.
/// </summary>
public enum UnitTestCrowdBenchmarkMode
{
    /// <summary>Keeps the virtual geometry values of <see cref="UnitTestingRenderSettings"/>.</summary>
    UseRenderSettings,

    /// <summary>M0: full-detail triangles. The skinned cluster cut is off.</summary>
    Triangles,

    /// <summary>M1: triangles through the skinned cluster cut. Every vertex is deformed.</summary>
    ClusterCut,

    /// <summary>
    /// M2: the skinned cluster cut with deformation level of detail. Far avatars deform only the
    /// vertices of their coarsest allowed cluster level.
    /// </summary>
    DeformationLod,
}
