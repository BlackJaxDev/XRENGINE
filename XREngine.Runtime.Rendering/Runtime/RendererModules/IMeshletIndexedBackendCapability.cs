namespace XREngine.Rendering;

/// <summary>Consumes resident GPU-scene meshlets using authored raster programs and GPU-written indexed arguments.</summary>
public interface IMeshletIndexedBackendCapability
{
    /// <summary>Reports installed compute-to-indexed capability independently of hardware task/mesh shader support.</summary>
    EMeshletSubmissionStatus GetMeshletIndexedAdmission(out string reason);

    /// <summary>Records the request or explicitly defers/rejects it. Callers must never fall through to another primitive path.</summary>
    EMeshletSubmissionStatus EnqueueMeshletIndexed(in MeshletIndexedBackendRequest request, out string reason);
}
