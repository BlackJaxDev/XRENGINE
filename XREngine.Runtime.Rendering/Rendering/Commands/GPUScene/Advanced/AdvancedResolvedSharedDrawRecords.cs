namespace XREngine.Rendering.Commands;

/// <summary>
/// Full draw dependency chain resolved without managed renderer identity.
/// </summary>
public struct AdvancedResolvedSharedDrawRecords
{
    public AdvancedResolvedDrawRecords Scene;
    public AdvancedMaterialRecord Material;
}
