using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    // The palette layout of the current bindings: one entry for each binding, in binding order.
    // RefreshPaletteLayout advances _paletteLayoutVersion only when an entry changes, so output
    // pages and palette consumers compare one version instead of every binding.
    private readonly List<PaletteLayoutEntry> _paletteLayoutEntries = [];
    // Maps a slice base to the first binding that uses it. Used only while the layout changes.
    private readonly Dictionary<uint, int> _paletteLayoutFirstBindings = [];
    // Starts above the version that a new output page records, so no page matches before the
    // first refresh.
    private long _paletteLayoutVersion = 1L;
    // The element range that holds every slice of the layout.
    private uint _paletteLayoutStart;
    private uint _paletteLayoutEnd;

    /// <summary>The palette layout of one binding.</summary>
    private struct PaletteLayoutEntry
    {
        public XRMeshRenderer Renderer;
        public IPhysicsChainComputeSource Component;
        public GPUPhysicsChainRequest? Request;
        public uint Base;
        // The slice size: the bone matrix count, at least 1.
        public uint Count;
        public long BoneGeneration;
        public int BindingGeneration;
        public bool DrivesCompleteBonePalette;
        // The first binding that uses the same slice. Compatible complete palettes share a slice.
        public int FirstBindingOfSlice;
    }

    /// <summary>
    /// Compares the palette bindings of this frame with the layout entries. When a binding
    /// differs, rebuilds the entries and advances <see cref="_paletteLayoutVersion"/>.
    /// Returns false when the slice bases do not match the bindings.
    /// </summary>
    private bool RefreshPaletteLayout()
    {
        ReadOnlySpan<GpuDrivenRendererPaletteBinding> bindings = CollectionsMarshal.AsSpan(_gpuDrivenPaletteBindings);
        ReadOnlySpan<uint> bases = CollectionsMarshal.AsSpan(_gpuDrivenPaletteSliceBases);
        if (bases.Length != bindings.Length)
        {
            InvalidatePaletteLayout();
            return false;
        }
        if (IsPaletteLayoutCurrent(bindings, bases))
            return true;

        _paletteLayoutEntries.Clear();
        _paletteLayoutFirstBindings.Clear();
        uint start = uint.MaxValue;
        uint end = 0u;
        for (int index = 0; index < bindings.Length; ++index)
        {
            ref readonly GpuDrivenRendererPaletteBinding binding = ref bindings[index];
            uint baseElement = bases[index];
            uint count = Math.Max(binding.BoneMatrixElementCount, 1u);
            if (!_paletteLayoutFirstBindings.TryGetValue(baseElement, out int first))
            {
                first = index;
                _paletteLayoutFirstBindings.Add(baseElement, index);
            }
            _paletteLayoutEntries.Add(new PaletteLayoutEntry
            {
                Renderer = binding.Renderer,
                Component = binding.Component,
                Request = binding.Request,
                Base = baseElement,
                Count = count,
                BoneGeneration = binding.RendererBoneBufferGeneration,
                BindingGeneration = binding.BindingGeneration,
                DrivesCompleteBonePalette = binding.DrivesCompleteBonePalette,
                FirstBindingOfSlice = first,
            });
            start = Math.Min(start, baseElement);
            end = Math.Max(end, checked(baseElement + count));
        }
        _paletteLayoutFirstBindings.Clear();
        _paletteLayoutStart = bindings.Length == 0 ? 0u : start;
        _paletteLayoutEnd = end;
        ++_paletteLayoutVersion;
        return true;
    }

    private bool IsPaletteLayoutCurrent(ReadOnlySpan<GpuDrivenRendererPaletteBinding> bindings,
        ReadOnlySpan<uint> bases)
    {
        ReadOnlySpan<PaletteLayoutEntry> entries = CollectionsMarshal.AsSpan(_paletteLayoutEntries);
        if (entries.Length != bindings.Length)
            return false;
        for (int index = 0; index < entries.Length; ++index)
        {
            ref readonly PaletteLayoutEntry entry = ref entries[index];
            ref readonly GpuDrivenRendererPaletteBinding binding = ref bindings[index];
            if (!ReferenceEquals(entry.Renderer, binding.Renderer) ||
                !ReferenceEquals(entry.Component, binding.Component) ||
                !ReferenceEquals(entry.Request, binding.Request) ||
                entry.Base != bases[index] ||
                entry.Count != Math.Max(binding.BoneMatrixElementCount, 1u) ||
                entry.BoneGeneration != binding.RendererBoneBufferGeneration ||
                entry.BindingGeneration != binding.BindingGeneration ||
                entry.DrivesCompleteBonePalette != binding.DrivesCompleteBonePalette)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Clears the layout entries and advances the version, so that no output page or palette
    /// consumer uses the old layout.
    /// </summary>
    private void InvalidatePaletteLayout()
    {
        _paletteLayoutEntries.Clear();
        _paletteLayoutStart = 0u;
        _paletteLayoutEnd = 0u;
        ++_paletteLayoutVersion;
    }

    /// <summary>
    /// Fills the palette slice map of <paramref name="page"/> from the layout entries, unless the
    /// page already holds this layout. Returns false when one renderer has two different slices.
    /// </summary>
    private bool FillOutputPagePaletteSlices(PhysicsChainOutputPage page)
    {
        if (page.PaletteSlicesLayoutVersion == _paletteLayoutVersion)
            return true;

        page.PaletteSlices.Clear();
        page.PaletteSlicesLayoutVersion = 0L;
        ReadOnlySpan<PaletteLayoutEntry> entries = CollectionsMarshal.AsSpan(_paletteLayoutEntries);
        for (int index = 0; index < entries.Length; ++index)
        {
            ref readonly PaletteLayoutEntry entry = ref entries[index];
            var slice = (entry.Base, entry.Count, entry.BoneGeneration);
            if (page.PaletteSlices.TryGetValue(entry.Renderer, out var existing))
            {
                if (existing != slice)
                    return false;
            }
            else
                page.PaletteSlices.Add(entry.Renderer, slice);
        }
        page.PaletteSlicesLayoutVersion = _paletteLayoutVersion;
        return true;
    }
}
