using System.Runtime.InteropServices;

namespace XREngine.Rendering.WebGPU;

/// <summary>Physical packing of one canonical output slot's CPU-authored aggregate inputs.</summary>
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
    private uint _frameSequence;
    private ulong _resourceGeneration;

    internal WebGpuAdvancedDeformationInputArena(WebGpuRendererHost renderer, XRDataBuffer output)
    {
        _renderer = renderer;
        Output = output;
        Storage = new(renderer, "Canonical aggregate deformation inputs");
    }

    internal XRDataBuffer Output { get; }
    internal WebGpuOwnedStorageBuffer Storage { get; }

    internal void Prepare(AdvancedGpuDeformationResources resources)
    {
        AdvancedGpuDeformationPublication publication = resources.Publication;
        if (!ReferenceEquals(Output, publication.CurrentVertices))
            throw new InvalidOperationException("WebGPU.Advanced.DeformationOwnerMismatch: packed inputs require their canonical output slot.");
        if (_frameSequence == _renderer.EngineFrameSequence)
        {
            if (_resourceGeneration != publication.ResourceGeneration)
                throw new InvalidOperationException("WebGPU.Advanced.DeformationChanged: an aggregate output slot changed during its recorded frame.");
            return;
        }
        bool changedLayout = !Storage.IsGenerated;
        bool changedGeneration = _resourceGeneration != publication.ResourceGeneration;
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
                Storage.UploadPreparation(WebGpuDeformationSource.GetSourceBytes(source)[..checked((int)byteLength)], _offsets[section]);
            _sources[section] = source;
            _lengths[section] = byteLength;
            _revisions[section] = source.Revision;
        }
        Storage.UploadPreparation(_header);
        _resourceGeneration = publication.ResourceGeneration;
        _frameSequence = _renderer.EngineFrameSequence;
    }

    public void Dispose() => Storage.Dispose();
}
