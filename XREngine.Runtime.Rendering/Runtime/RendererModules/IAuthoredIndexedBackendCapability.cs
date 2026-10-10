namespace XREngine.Rendering;

/// <summary>Consumes complete resident GPU-scene publications through authored raster programs and GPU-written indexed arguments.</summary>
public interface IAuthoredIndexedBackendCapability
{
    /// <summary>Reports installed compute-to-indexed capability independently of hardware task/mesh shader support.</summary>
    EAuthoredIndexedSubmissionStatus GetMeshletIndexedAdmission(out string reason);

    /// <summary>Reports the whole-primitive indirect family independently of meshlet payload availability.</summary>
    EAuthoredIndexedSubmissionStatus GetIndirectIndexedAdmission(out string reason);

    /// <summary>Records the request or explicitly defers/rejects it. Callers must never fall through to another primitive path.</summary>
    EAuthoredIndexedSubmissionStatus EnqueueAuthoredIndexed(in AuthoredIndexedBackendRequest request, out string reason);
}
