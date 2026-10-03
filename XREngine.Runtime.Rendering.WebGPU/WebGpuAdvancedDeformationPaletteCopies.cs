using System.Globalization;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Bounded retained native copy plans for one completion-protected aggregate input
/// slot. Native command dependencies pin both physical buffers through retirement.
/// </summary>
internal sealed class WebGpuAdvancedDeformationPaletteCopies(WebGpuRendererHost renderer) : IDisposable
{
    private const int MaximumCopies = 4096;
    private const int VariantsPerCopy = 4;
    private readonly record struct Entry(AdvancedGpuDeformationPaletteCopy Copy, int Destination, int Offset, int Command, uint LastUsedFrame);
    private Entry[] _entries = [];
    private int _count;

    internal bool AreSourcesCurrent(ReadOnlySpan<AdvancedGpuDeformationPaletteCopy> copies)
    {
        foreach (ref readonly AdvancedGpuDeformationPaletteCopy copy in copies)
            if (!copy.IsSourceCurrent || !ReferenceEquals(copy.SourceOwner.Owner, renderer) ||
                copy.SourceOwner.OwnerGeneration != renderer.BackendGeneration)
                return false;
        return true;
    }

    internal void Validate(ReadOnlySpan<AdvancedGpuDeformationPaletteCopy> copies, uint paletteByteLength)
    {
        if (copies.Length > MaximumCopies)
            throw new NotSupportedException("WebGPU.Advanced.GpuPoseCapacity: GPU palette copies exceed the bounded native command capacity.");
        uint previousEnd = 0;
        foreach (ref readonly AdvancedGpuDeformationPaletteCopy copy in copies)
        {
            if (!copy.IsSourceCurrent || !ReferenceEquals(copy.SourceOwner.Owner, renderer) ||
                copy.SourceOwner.OwnerGeneration != renderer.BackendGeneration ||
                copy.ByteLength == 0 || copy.SourceByteOffset % 48 != 0 ||
                copy.DestinationByteOffset % 48 != 0 || copy.ByteLength % 48 != 0 ||
                copy.SourceByteOffset > copy.SourceByteLength || copy.ByteLength > copy.SourceByteLength - copy.SourceByteOffset ||
                copy.DestinationByteOffset < previousEnd || copy.DestinationByteOffset > paletteByteLength ||
                copy.ByteLength > paletteByteLength - copy.DestinationByteOffset)
                throw new InvalidOperationException("WebGPU.Advanced.GpuPoseSourceChanged: GPU palette copies require exact live source generations and disjoint packed destination ranges.");
            previousEnd = checked(copy.DestinationByteOffset + copy.ByteLength);
        }
    }

    internal void Record(ReadOnlySpan<AdvancedGpuDeformationPaletteCopy> copies, WebGpuOwnedStorageBuffer storage, int paletteOffset)
    {
        int requiredEntries = checked(copies.Length * VariantsPerCopy);
        if (_entries.Length < requiredEntries)
            Array.Resize(ref _entries, Math.Min(MaximumCopies * VariantsPerCopy,
                Math.Max(requiredEntries, Math.Max(VariantsPerCopy * 4, _entries.Length * 2))));
        for (int index = 0; index < copies.Length; index++)
        {
            AdvancedGpuDeformationPaletteCopy copy = copies[index];
            int destinationOffset = checked(paletteOffset + (int)copy.DestinationByteOffset);
            int first = index * VariantsPerCopy;
            int selected = -1, replacement = -1;
            uint oldest = uint.MaxValue;
            for (int variant = first; variant < first + VariantsPerCopy; variant++)
            {
                ref Entry candidate = ref _entries[variant];
                if (candidate.Command != 0 && (candidate.Destination != storage.ResourceHandle ||
                    !candidate.Copy.IsSourceGenerationCurrent || candidate.Copy.SourceOwner.OwnerGeneration != renderer.BackendGeneration))
                    Retire(ref candidate);
                if (candidate.Command == 0)
                {
                    if (replacement < 0 || oldest != 0)
                    { replacement = variant; oldest = 0; }
                    continue;
                }
                // Alternating GPU palette buffers do not align with a three-slot
                // aggregate ring. Keep their recurring physical copy shapes warm.
                if (candidate.Copy.SourceHandle == copy.SourceHandle &&
                    ReferenceEquals(candidate.Copy.SourceOwner, copy.SourceOwner) &&
                    candidate.Copy.SourceByteOffset == copy.SourceByteOffset && candidate.Copy.ByteLength == copy.ByteLength &&
                    candidate.Offset == destinationOffset)
                    selected = variant;
                if (replacement < 0 || candidate.LastUsedFrame < oldest)
                { replacement = variant; oldest = candidate.LastUsedFrame; }
            }
            if (selected < 0)
            {
                selected = replacement;
                string descriptor = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"label\":\"Canonical GPU palette copy\",\"commands\":[{{\"type\":\"copyBuffer\",\"source\":{copy.SourceHandle},\"sourceOffset\":{copy.SourceByteOffset},\"destination\":{storage.ResourceHandle},\"destinationOffset\":{destinationOffset},\"size\":{copy.ByteLength}}}]}}");
                int command = renderer.PrepareCommands(descriptor);
                Retire(ref _entries[selected]);
                _entries[selected] = new(copy, storage.ResourceHandle, destinationOffset, command, renderer.EngineFrameSequence);
            }
            else
                _entries[selected] = _entries[selected] with { Copy = copy, LastUsedFrame = renderer.EngineFrameSequence };
            _count = Math.Max(_count, index + 1);
            renderer.RecordEngineCommands(_entries[selected].Command, []);
            storage.MarkRecorded();
        }
        for (int index = requiredEntries; index < _count * VariantsPerCopy; index++)
            Retire(ref _entries[index]);
        _count = copies.Length;
    }

    private void Retire(ref Entry entry)
    {
        if (entry.Command != 0)
            renderer.RetireEngineResourceAfterFrame(entry.Command);
        entry = default;
    }

    public void Dispose()
    {
        for (int index = 0; index < _count * VariantsPerCopy; index++)
            Retire(ref _entries[index]);
        _count = 0;
    }
}
