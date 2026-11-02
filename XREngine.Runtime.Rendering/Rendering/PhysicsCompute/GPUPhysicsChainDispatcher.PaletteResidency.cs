using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private readonly PhysicsChainPaletteAtlasAllocator _gpuDrivenPaletteSliceAllocator = new();
    private readonly List<uint> _gpuDrivenPaletteSliceBases = [];
    private readonly HashSet<uint> _gpuDrivenPaletteSlicesSeeded = [];
    // The number of chains that write each renderer in the current layout.
    private readonly Dictionary<XRMeshRenderer, int> _paletteRendererBindingCounts =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

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
        _paletteRendererBindingCounts.Clear();
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
            ++CollectionsMarshal.GetValueRefOrAddDefault(_paletteRendererBindingCounts,
                _gpuDrivenPaletteBindings[bindingIndex].Renderer, out _);

        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            // A complete palette depends only on this chain and the shared mesh
            // layout, so compatible renderers can consume one generated slice.
            // A renderer reads one slice, so all of its other chains write the
            // same slice. Non-chain bones can contain different animation state.
            PhysicsChainPaletteSliceKey key = SharesPaletteSliceByMesh(in binding)
                ? new PhysicsChainPaletteSliceKey(binding.Component, binding.Renderer.Mesh!)
                : new PhysicsChainPaletteSliceKey(binding.Renderer, binding.Renderer);
            PhysicsChainPaletteSlice slice = _gpuDrivenPaletteSliceAllocator.Acquire(
                key,
                binding.BoneMatrixElementCount);
            _gpuDrivenPaletteSliceBases.Add(slice.BaseElement);
        }

        _gpuDrivenPaletteSliceAllocator.EndLayout();
    }

    /// <summary>
    /// True when the only chain of a renderer writes every bone of its mesh, so that renderers
    /// of the same mesh can read one slice. Valid after the layout counts its renderers.
    /// </summary>
    private bool SharesPaletteSliceByMesh(in GpuDrivenRendererPaletteBinding binding)
        => binding.DrivesCompleteBonePalette
            && binding.Renderer.Mesh is not null
            && _paletteRendererBindingCounts.GetValueOrDefault(binding.Renderer) == 1;

    private void ResetStableGpuDrivenPaletteLayout()
    {
        _gpuDrivenPaletteSliceAllocator.Reset();
        _gpuDrivenPaletteSliceBases.Clear();
        _gpuDrivenPaletteSlicesSeeded.Clear();
        InvalidatePaletteLayout();
        ResetPaletteSources();
    }
}

public readonly record struct PhysicsChainPaletteAtlasDiagnostics(
    int LiveSliceCount,
    int RendererBindingCount,
    int SharedRendererBindingCount,
    uint LiveHighWaterElements,
    uint BufferCapacityElements,
    int GeneratedMappingCount);
