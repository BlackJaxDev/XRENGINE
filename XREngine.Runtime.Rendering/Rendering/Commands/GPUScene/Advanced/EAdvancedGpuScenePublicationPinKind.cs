namespace XREngine.Rendering.Commands;

/// <summary>
/// Distinguishes CPU frame-package retention from GPU command submission
/// retention when determining safe tombstone reclamation.
/// </summary>
public enum EAdvancedGpuScenePublicationPinKind : byte
{
    Package,
    Gpu,
}
