namespace XREngine.Components;

/// <summary>
/// Stable world-owned registration record. It contains IDs and arena slices,
/// never authoring objects or backend-specific buffer implementations.
/// </summary>
public struct PhysicsChainInstance
{
    public PhysicsChainRuntimeHandle RuntimeHandle { get; set; }
    public long TemplateId { get; set; }
    public long ColliderSetId { get; set; }
    public PhysicsChainArenaSlice RootInputSlice { get; set; }
    public PhysicsChainArenaSlice StateSlice { get; set; }
    public PhysicsChainArenaSlice PaletteSlice { get; set; }
    public PhysicsChainArenaHandle BoundsSlot { get; set; }
    public PhysicsChainQualityPolicy RequestedQuality { get; set; }
    public PhysicsChainQualityPolicy EffectiveQuality { get; set; }
    public PhysicsChainInstanceFlags Flags { get; set; }
    public uint Generation { get; set; }

    public readonly bool IsValid
        => RuntimeHandle.IsValid
            && TemplateId > 0L
            && RootInputSlice.IsValid
            && StateSlice.IsValid
            && PaletteSlice.IsValid
            && BoundsSlot.IsValid
            && Generation != 0u;
}
