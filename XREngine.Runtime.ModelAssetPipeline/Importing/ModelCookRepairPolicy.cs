namespace XREngine.Rendering.Models;

/// <summary>
/// Controls whether optional derived geometry may be reconstructed from cached core geometry.
/// </summary>
public enum ModelCookRepairPolicy
{
    RejectMissingDerivedData = 0,
    RepairOptionalDerivedData = 1,
}
