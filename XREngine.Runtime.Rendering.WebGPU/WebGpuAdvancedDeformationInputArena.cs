using System.Runtime.InteropServices;

namespace XREngine.Rendering.WebGPU;

/// <summary>Physical packing of one canonical output slot's CPU and GPU-owned aggregate inputs.</summary>
internal sealed class WebGpuAdvancedDeformationInputArena : IDisposable
{
    private const int SectionCount = AdvancedGpuDeformationResources.PackedInputSectionCount;
    private const int HeaderBytes = SectionCount * 16;
    private readonly WebGpuRendererHost _renderer;
    private readonly XRDataBuffer?[] _sources = new XRDataBuffer?[SectionCount];
    private readonly uint[] _capacities = new uint[SectionCount];
    private readonly uint[] _lengths = new uint[SectionCount];
    private readonly ulong[] _revisions = new ulong[SectionCount];
    private readonly int[] _offsets = new int[SectionCount];
    private readonly byte[] _header = new byte[HeaderBytes];
    private readonly WebGpuAdvancedDeformationPaletteCopies _paletteCopies;
    private uint _frameSequence;
    private AdvancedGpuDeformationPublication _publication;

    internal WebGpuAdvancedDeformationInputArena(WebGpuRendererHost renderer, XRDataBuffer output)
    {
        _renderer = renderer;
        _paletteCopies = new(renderer);
        Output = output;
        Storage = new(renderer, "Canonical aggregate deformation inputs");
    }

    internal XRDataBuffer Output { get; }
    internal WebGpuOwnedStorageBuffer Storage { get; }

    internal bool TryPrepare(AdvancedGpuDeformationResources resources)
    {
        AdvancedGpuDeformationPublication publication = resources.Publication;
        if (!ReferenceEquals(Output, publication.CurrentVertices))
            throw new InvalidOperationException("WebGPU.Advanced.DeformationOwnerMismatch: packed inputs require their canonical output slot.");
        if (_frameSequence == _renderer.EngineFrameSequence)
        {
            if (_publication != publication)
                throw new InvalidOperationException("WebGPU.Advanced.DeformationChanged: an aggregate output slot changed during its recorded frame.");
            return true;
        }
        resources.GetPackedInputSection(5, out uint paletteByteLength);
        ReadOnlySpan<AdvancedGpuDeformationPaletteCopy> paletteCopies = publication.GpuPaletteCopies.Span;
        // A source replaced before any copy is recorded can be recaptured on the
        // next attempt. Return a rejection before mutating this slot instead of
        // poisoning it through the partially-recorded-work exception path.
        if (!_paletteCopies.AreSourcesCurrent(paletteCopies))
            return false;
        _paletteCopies.Validate(paletteCopies, paletteByteLength);
        bool changedLayout = !Storage.IsGenerated;
        bool changedGeneration = _publication.ResourceGeneration != publication.ResourceGeneration;
        for (int section = 0; section < SectionCount; section++)
            changedLayout |= _capacities[section] != resources.GetPackedInputSection(section, out _).Length;
        if (changedLayout)
        {
            int cursor = HeaderBytes;
            for (int section = 0; section < SectionCount; section++)
            {
                XRDataBuffer source = resources.GetPackedInputSection(section, out _);
                _offsets[section] = cursor;
                _capacities[section] = source.Length;
                cursor = checked((cursor + checked((int)source.Length) + 15) & ~15);
                if (cursor > _renderer.MaximumAdvancedStorageBytes)
                    throw new NotSupportedException("WebGPU.Advanced.DeformationCapacity: packed canonical aggregate inputs exceed the device storage binding limit.");
            }
            Storage.EnsureCapacity(cursor);
        }
        Span<uint> header = MemoryMarshal.Cast<byte, uint>(_header.AsSpan());
        for (int section = 0; section < SectionCount; section++)
        {
            XRDataBuffer source = resources.GetPackedInputSection(section, out uint byteLength);
            if (source.GpuProduced || byteLength > source.Length || (byteLength & 3) != 0)
                throw new NotSupportedException("WebGPU.Advanced.DeformationInputInvalid: input packing requires exact word-aligned canonical CPU records.");
            header[section * 4] = checked((uint)_offsets[section] / 4u);
            header[section * 4 + 1] = byteLength;
            bool dynamic = section is 0 or 1 or 2 or 5 or 7 or 12;
            if (byteLength != 0 && (changedLayout || changedGeneration || dynamic || !ReferenceEquals(_sources[section], source) ||
                _lengths[section] != byteLength || _revisions[section] != source.Revision))
            {
                ReadOnlySpan<byte> bytes = WebGpuDeformationSource.GetSourceBytes(source)[..checked((int)byteLength)];
                if (section == 5 && !paletteCopies.IsEmpty)
                    UploadCpuPaletteRanges(bytes, paletteCopies);
                else
                    Storage.UploadPreparation(bytes, _offsets[section]);
            }
            _sources[section] = source;
            _lengths[section] = byteLength;
            _revisions[section] = source.Revision;
        }
        Storage.UploadPreparation(_header);
        // Finish every CPU preparation write before exposing this arena to the
        // ordered frame. Copies then follow their source producers and precede
        // aggregate dispatch, without a later CPU write over GPU palette bytes.
        _paletteCopies.Record(paletteCopies, Storage, _offsets[5]);
        _publication = publication;
        _frameSequence = _renderer.EngineFrameSequence;
        return true;
    }

    private void UploadCpuPaletteRanges(ReadOnlySpan<byte> bytes, ReadOnlySpan<AdvancedGpuDeformationPaletteCopy> copies)
    {
        int cursor = 0;
        foreach (ref readonly AdvancedGpuDeformationPaletteCopy copy in copies)
        {
            int start = checked((int)copy.DestinationByteOffset);
            if (start > cursor)
                Storage.UploadPreparation(bytes[cursor..start], checked(_offsets[5] + cursor));
            cursor = checked(start + (int)copy.ByteLength);
        }
        if (cursor < bytes.Length)
            Storage.UploadPreparation(bytes[cursor..], checked(_offsets[5] + cursor));
    }

    public void Dispose()
    {
        _paletteCopies.Dispose();
        Storage.Dispose();
    }
}
