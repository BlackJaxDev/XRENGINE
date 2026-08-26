namespace XREngine.Components;

/// <summary>
/// Backend-neutral output published by one physics-chain instance. Renderers
/// consume palette and bounds slices directly; CPU mirroring is optional and
/// tracked independently.
/// </summary>
public struct PhysicsChainOutput
{
    public PhysicsChainRuntimeHandle InstanceHandle { get; set; }
    public PhysicsChainArenaSlice CurrentPalette { get; set; }
    public PhysicsChainArenaSlice PreviousPalette { get; set; }
    public PhysicsChainArenaHandle BoundsSlot { get; set; }
    public uint OutputGeneration { get; set; }
    public long SimulationFrame { get; set; }
    public PhysicsChainCpuMirrorStatus CpuMirrorStatus { get; set; }
    public PhysicsChainBackendStatus BackendStatus { get; set; }

    public readonly bool IsValid
        => InstanceHandle.IsValid
            && CurrentPalette.IsValid
            && BoundsSlot.IsValid
            && OutputGeneration != 0u
            && BackendStatus == PhysicsChainBackendStatus.Ready;

    /// <summary>
    /// Resets temporal history after spawn, teleport, template/backend change,
    /// or slot reuse so motion consumers cannot observe an unrelated palette.
    /// </summary>
    public void ResetHistory()
        => PreviousPalette = CurrentPalette;

    public void AdvancePalette(PhysicsChainArenaSlice nextPalette, long simulationFrame)
    {
        PreviousPalette = CurrentPalette.IsValid ? CurrentPalette : nextPalette;
        CurrentPalette = nextPalette;
        SimulationFrame = simulationFrame;
    }
}
