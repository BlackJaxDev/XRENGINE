namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private readonly Dictionary<(VkRenderProgram Program, ulong LinkGeneration, int SetCount), uint>
        _activeDescriptorSetMasks = [];
    private readonly Dictionary<(VkRenderProgram Program, ulong LinkGeneration, bool SharedMaterialTier, bool DescriptorHeap), bool>
        _drawSlotInvariance = [];

    /// <summary>
    /// Returns the active set mask for a linked program. The mask depends only on the
    /// reflected bindings, so a walk of them is not repeated for every draw of every frame.
    /// </summary>
    private uint GetCachedActiveDescriptorSetMask(
        VkRenderProgram program,
        IReadOnlyList<DescriptorBindingInfo> bindings,
        int setCount)
    {
        var key = (program, program.LinkGeneration, setCount);
        if (!_activeDescriptorSetMasks.TryGetValue(key, out uint mask))
        {
            if (_activeDescriptorSetMasks.Count >= MaxFrameDescriptorAllocations)
                _activeDescriptorSetMasks.Clear();
            mask = ComputeActiveDescriptorSetMask(bindings, setCount, program.ExternallyOwnedDescriptorSetMask);
            _activeDescriptorSetMasks[key] = mask;
        }
        return mask;
    }

    /// <summary>Returns the reflected draw-slot invariance for a linked program.</summary>
    private bool GetCachedDrawSlotInvariance(
        VkRenderProgram program,
        IReadOnlyList<DescriptorBindingInfo> bindings,
        bool usesSharedMaterialTier,
        bool descriptorHeapDrawBindingActive)
    {
        var key = (program, program.LinkGeneration, usesSharedMaterialTier, descriptorHeapDrawBindingActive);
        if (!_drawSlotInvariance.TryGetValue(key, out bool invariant))
        {
            if (_drawSlotInvariance.Count >= MaxFrameDescriptorAllocations)
                _drawSlotInvariance.Clear();
            invariant = AreDescriptorBindingsDrawSlotInvariant(
                bindings,
                usesSharedMaterialTier,
                descriptorHeapDrawBindingActive,
                program.ExternallyOwnedDescriptorSetMask);
            _drawSlotInvariance[key] = invariant;
        }
        return invariant;
    }
}
