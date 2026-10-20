namespace XREngine.Rendering;

/// <summary>Parts of Advanced preparation extraction that the optional sub-stage timers measure.</summary>
internal enum EAdvancedExtractionSubStage
{
    /// <summary>The canonical mesh preparation pre-pass over all commands.</summary>
    PrePass,
    /// <summary>The bounds route, the renderer output state capture, and the material check of a row.</summary>
    RouteAndCover,
    /// <summary>The geometry lookup, the visibility candidate, and the geometry offsets of a row.</summary>
    GeometryAndCandidate,
    /// <summary>The topology generation and the deformation mesh slice of a skinned row.</summary>
    MeshPreparation,
    /// <summary>The deformation pose capture of a skinned row.</summary>
    Pose,
    /// <summary>The history check and the deformed arena slice of a skinned row.</summary>
    HistoryAndArena,
    /// <summary>The animation schedule, the feature flags, and the job of a skinned row.</summary>
    ScheduleAndJob,
    /// <summary>The temporal reason and the visibility payload of a row, and rejected deformations.</summary>
    Payload,
    /// <summary>The deformation admission verdicts over all commands.</summary>
    AdmissionVerdicts,
    Count,
}
