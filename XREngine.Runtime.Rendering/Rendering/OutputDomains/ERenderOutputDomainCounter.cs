namespace XREngine.Rendering.OutputDomains;

/// <summary>
/// Names one cumulative counter of <see cref="RenderOutputDomainTelemetry"/>. Each counter
/// exists once per <see cref="ERenderOutputDomain"/>. The counters show how much of a domain's
/// frame cost comes from state that another domain replaced.
/// </summary>
public enum ERenderOutputDomainCounter : byte
{
    /// <summary>Frames that the domain started: desktop frame transactions, or XR eye frames.</summary>
    Frames = 0,

    /// <summary>Frame plans that the domain built and sealed.</summary>
    PlanBuilds,

    /// <summary>Frame operations in the plans that the domain built.</summary>
    PlanOperations,

    /// <summary>Output rank evaluations while the domain's plans sorted their operations.</summary>
    PlanRankEvaluations,

    /// <summary>Prepared mesh operation cohorts that the domain reused unchanged.</summary>
    MeshCohortMatches,

    /// <summary>Prepared mesh operation cohorts that the domain could not reuse and prepared again.</summary>
    MeshCohortMisses,

    /// <summary>Queued mesh render requests that the domain materialized one by one.</summary>
    MeshRequestsMaterialized,

    /// <summary>Persistent program binding artifacts that the domain replaced in an existing slot.</summary>
    ArtifactReplacements,

    /// <summary>Persistent program binding artifact tables that the domain cleared because they were full.</summary>
    ArtifactTableEvictions,

    /// <summary>Descriptor owner lookups of the domain that found no published allocation.</summary>
    DescriptorOwnerMisses,

    /// <summary>The number of counter values. Not a counter.</summary>
    Count,
}
