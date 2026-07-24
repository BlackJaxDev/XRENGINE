namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private readonly PhysicsChainPaletteAtlasAllocator _gpuDrivenPaletteSliceAllocator = new();
    private readonly List<uint> _gpuDrivenPaletteSliceBases = [];
    private readonly HashSet<uint> _gpuDrivenPaletteSlicesSeeded = [];

    /// <summary>
    /// Returns stable palette-atlas residency diagnostics without mapping or
    /// synchronously reading any GPU resource.
    /// </summary>
    public PhysicsChainPaletteAtlasDiagnostics GetPaletteAtlasDiagnosticsSnapshot()
        => new(
            _gpuDrivenPaletteSliceAllocator.LiveSliceCount,
            _gpuDrivenPaletteBindings.Count,
            _gpuDrivenPaletteBindings.Count - _gpuDrivenPaletteSliceAllocator.LiveSliceCount,
            _gpuDrivenPaletteSliceAllocator.HighWater,
            _gpuDrivenSkinPaletteBuffer?.ElementCount ?? 0u,
            _gpuDrivenBoneMappings.Count);

    private void PrepareStableGpuDrivenPaletteLayout()
    {
        _gpuDrivenPaletteSliceAllocator.BeginLayout();
        _gpuDrivenPaletteSliceBases.Clear();

        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            // A complete palette depends only on this chain and the shared mesh
            // layout, so compatible renderers can consume one generated slice.
            // Partial palettes retain renderer ownership because non-chain bones
            // can contain different animation state.
            object compatibilityOwner = binding.DrivesCompleteBonePalette && binding.Renderer.Mesh is { } mesh
                ? mesh
                : binding.Renderer;
            var key = new PhysicsChainPaletteSliceKey(binding.Component, compatibilityOwner);
            PhysicsChainPaletteSlice slice = _gpuDrivenPaletteSliceAllocator.Acquire(
                key,
                binding.BoneMatrixElementCount);
            _gpuDrivenPaletteSliceBases.Add(slice.BaseElement);
        }

        _gpuDrivenPaletteSliceAllocator.EndLayout();
    }

    private void ResetStableGpuDrivenPaletteLayout()
    {
        _gpuDrivenPaletteSliceAllocator.Reset();
        _gpuDrivenPaletteSliceBases.Clear();
        _gpuDrivenPaletteSlicesSeeded.Clear();
    }
}

public readonly record struct PhysicsChainPaletteAtlasDiagnostics(
    int LiveSliceCount,
    int RendererBindingCount,
    int SharedRendererBindingCount,
    uint LiveHighWaterElements,
    uint BufferCapacityElements,
    int GeneratedMappingCount);
