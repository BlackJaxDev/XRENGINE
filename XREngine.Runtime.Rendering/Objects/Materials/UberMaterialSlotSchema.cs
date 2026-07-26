namespace XREngine.Rendering;

/// <summary>
/// Authoritative repeated-slot contract for an uber feature family.
/// </summary>
public sealed record UberMaterialSlotSchema(
    string Id,
    string FeatureId,
    int SlotCount,
    string[] FieldSuffixes,
    EUberSamplerRole[] SamplerRoles);
