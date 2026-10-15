namespace XREngine.Rendering;

/// <summary>
/// Cumulative times of the parts of Advanced preparation extraction, in milliseconds. Read two
/// captures inside a window and divide the differences by the build or row count.
/// </summary>
/// <param name="Enabled">False when <c>XRE_ADVANCED_EXTRACTION_SUBSTAGE_TIMERS</c> was not set at startup.</param>
/// <param name="Builds">Extraction builds that reached the row loop.</param>
/// <param name="Rows">Extracted rows.</param>
/// <param name="PrePassMilliseconds">The canonical mesh preparation pre-pass.</param>
/// <param name="RouteAndCoverMilliseconds">Bounds routes, renderer output state captures, and material checks.</param>
/// <param name="GeometryAndCandidateMilliseconds">Geometry lookups, visibility candidates, and geometry offsets.</param>
/// <param name="MeshPreparationMilliseconds">Topology generations and deformation mesh slices.</param>
/// <param name="PoseMilliseconds">Deformation pose captures.</param>
/// <param name="HistoryAndArenaMilliseconds">History checks and deformed arena slices.</param>
/// <param name="ScheduleAndJobMilliseconds">Animation schedules, feature flags, and jobs.</param>
/// <param name="PayloadMilliseconds">Temporal reasons, visibility payloads, and rejected deformations.</param>
/// <param name="AdmissionVerdictsMilliseconds">Deformation admission verdicts.</param>
public readonly record struct AdvancedExtractionSubStageTimings(
    bool Enabled,
    long Builds,
    long Rows,
    double PrePassMilliseconds,
    double RouteAndCoverMilliseconds,
    double GeometryAndCandidateMilliseconds,
    double MeshPreparationMilliseconds,
    double PoseMilliseconds,
    double HistoryAndArenaMilliseconds,
    double ScheduleAndJobMilliseconds,
    double PayloadMilliseconds,
    double AdmissionVerdictsMilliseconds);
