namespace XREngine.Rendering.Commands;

/// <summary>
/// In-place material binding rebind for the retained-row commit. The full-size upload of a
/// streamed texture can change its sampler record too (for example when the mip count grows
/// from one level to a full chain), so the material needs a new sampler handle. The material
/// handle, its rows, and its draws stay the same. The swap-time row gate records such a
/// material instead of failing. The retained-row publication then plans one existing-material
/// transition for it with the full-plan material resolve, and the retained-row commit applies
/// that transition inside the open publication transaction: it acquires the new bindings,
/// rewrites the binding span of the material record, and then releases the old bindings.
/// </summary>
public sealed partial class AdvancedGpuScenePublisher
{
    // Set while CommittedMaterialBindingsMatch runs, when a binding of the same texture object
    // needs a new sampler (or another resource change that a texture refresh cannot apply).
    private bool _materialRebindInCheck;

    // The result of the last WitnessedCommittedMaterialBindingsMatch: true when the material
    // matched only because a rebind was accepted.
    private bool _lastCheckRequestedMaterialRebind;

    // Rows of the current gate walk whose material needs a rebind, one row for each material.
    private int[] _materialRebindRows = new int[8];
    private AdvancedGpuHandle[] _materialRebindHandles = new AdvancedGpuHandle[8];
    private int _materialRebindCount;

    /// <summary>
    /// Records that the material of <paramref name="commandIndex"/> needs a binding rebind in
    /// this publication. A material that several rows use is recorded one time.
    /// </summary>
    private void RecordMaterialRebind(int commandIndex, AdvancedGpuHandle materialHandle)
    {
        for (int index = 0; index < _materialRebindCount; ++index)
            if (_materialRebindHandles[index] == materialHandle)
                return;

        if (_materialRebindCount == _materialRebindRows.Length)
        {
            Array.Resize(ref _materialRebindRows, _materialRebindRows.Length * 2);
            Array.Resize(ref _materialRebindHandles, _materialRebindHandles.Length * 2);
        }
        _materialRebindRows[_materialRebindCount] = commandIndex;
        _materialRebindHandles[_materialRebindCount] = materialHandle;
        ++_materialRebindCount;
    }

    /// <summary>Discards the recorded rebinds, for example when the full plan publishes the frame.</summary>
    private void ClearMaterialRebinds()
    {
        if (_materialRebindCount == 0)
            return;
        Array.Clear(_materialRebindHandles, 0, _materialRebindCount);
        _materialRebindCount = 0;
    }

    /// <summary>
    /// Plans one existing-material transition for each recorded rebind, with the material
    /// resolve of the full plan, and preflights the material publisher. Returns false when a
    /// resolve does not give an update of the same material handle with changed resources;
    /// the frame then takes the full plan, which resets the planned state. The resource
    /// acquisitions and releases of the transitions stay in the shared resource plan, so the
    /// global resource preflight that follows checks them together with the global resources.
    /// </summary>
    private bool TryPlanMaterialRebinds()
    {
        BeginStampedPlan(ref _plannedVariantSlotGeneration, _plannedVariantSlotStamps);
        BeginStampedPlan(ref _plannedReleaseSlotGeneration, _plannedReleaseSlotStamps);
        BeginStampedPlan(ref _plannedExistingMaterialSlotGeneration, _plannedExistingMaterialSlotStamps);
        for (int index = 0; index < _materialRebindCount; ++index)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[_materialRebindRows[index]];
            int registrationIndex = plan.RegistrationIndex;
            if (registrationIndex < 0 || registrationIndex >= _registrationCount ||
                !TryResolvePlannedMaterialCounting(
                    plan.MaterialSource,
                    in plan.Command,
                    registrationIndex,
                    out int materialPlanIndex,
                    out _,
                    out _,
                    out _))
                return false;

            ref readonly AdvancedGpuSceneMaterialTransition transition =
                ref _plannedMaterials[materialPlanIndex];
            if (!transition.ExistingHandle.IsValid ||
                transition.ExistingHandle != _registrations[registrationIndex].Material ||
                !transition.ResourcesChanged)
                return false;
        }

        return _materialPublisher.TryPreflightTransition(
            _plannedMaterialRequests.AsSpan(0, _plannedMaterialCount),
            _plannedMaterialReleases.AsSpan(0, _plannedMaterialReleaseCount),
            out _);
    }
}
