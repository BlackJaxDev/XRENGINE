namespace XREngine.Rendering;

/// <summary>Which explicitly CPU-owned commands a shared GPU mesh operation asks its backend to replay.</summary>
public enum EAuthoredIndexedCpuReplayPolicy
{
    None,
    MeshesOnly,
    MeshesAndNonMesh,
}
