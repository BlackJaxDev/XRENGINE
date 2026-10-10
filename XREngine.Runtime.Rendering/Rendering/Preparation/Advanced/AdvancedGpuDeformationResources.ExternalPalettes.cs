using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    private AdvancedGpuPaletteCopy[] _externalPaletteCopies;
    private int _externalPaletteCopyCount;
    private uint[] _gpuDrivenPaletteIndices = [];

    private void CaptureDesktopPaletteCopies(XRMeshRenderer renderer, XRDataBuffer source, uint sourceBase, uint count,
        bool usesLocalBoneOwnership)
    {
        if (!usesLocalBoneOwnership)
        {
            AddExternalPaletteCopy(source, sourceBase, _paletteCount, count);
            return;
        }

        if (!ReferenceEquals(source, renderer.SkinPaletteBuffer) || sourceBase != 0u ||
            source.ComponentType != EComponentType.Float || source.ElementSize != 48 ||
            source.ClientSideSource is not { } memory || memory.Length < source.Length ||
            !source.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero)
            throw new InvalidOperationException("AggregateDeformation.MixedPaletteSourceInvalid: the local mixed palette requires its complete CPU-owned source image.");

        if (_gpuDrivenPaletteIndices.Length < count)
            Array.Resize(ref _gpuDrivenPaletteIndices, checked((int)NextPowerOfTwo(count)));
        int drivenCount = renderer.CaptureGpuDrivenBoneIndices(source, _gpuDrivenPaletteIndices.AsSpan(0, checked((int)count)));

        // CPU-owned rows may have changed without a source GPU upload. Seed the
        // aggregate image, then overwrite only GPU-owned rows with ordered copies.
        CopyPalette(source, 0u, _paletteScratch, _paletteCount, count);
        for (int index = 0; index < drivenCount;)
        {
            uint first = _gpuDrivenPaletteIndices[index++];
            uint end = first + 1u;
            while (index < drivenCount && _gpuDrivenPaletteIndices[index] == end)
            {
                ++index;
                ++end;
            }
            AddExternalPaletteCopy(source, first, checked(_paletteCount + first), end - first);
        }
    }

    private void AddExternalPaletteCopy(XRDataBuffer source, uint sourceBase, uint destinationBase, uint count)
    {
        if (_externalPaletteCopyCount > 0)
        {
            ref AdvancedGpuPaletteCopy previous = ref _externalPaletteCopies[_externalPaletteCopyCount - 1];
            if (ReferenceEquals(previous.Source, source) &&
                previous.SourceBase + previous.Count == sourceBase &&
                previous.DestinationBase + previous.Count == destinationBase)
            {
                previous = previous with { Count = checked(previous.Count + count) };
                return;
            }
        }

        if (_externalPaletteCopyCount == _externalPaletteCopies.Length)
        {
            // Mixed palettes can require several runs per draw. Each copy owns
            // at least one admitted palette row; grow only during preparation.
            int capacity = checked((int)NextPowerOfTwo(checked((uint)_externalPaletteCopyCount + 1u)));
            if (capacity > _paletteScratch.Length)
                throw new InvalidOperationException("AggregateDeformation.PaletteCopyCapacity: GPU copy ranges exceed the admitted palette capacity.");
            Array.Resize(ref _externalPaletteCopies, capacity);
        }
        _externalPaletteCopies[_externalPaletteCopyCount++] = new(source, sourceBase, destinationBase, count);
    }

    private bool TryCopyExternalPalettes(AbstractRenderer renderer, out bool copyAccepted)
    {
        copyAccepted = false;
        if (_externalPaletteCopyCount == 0)
            return true;

        // Upload CPU-authored ranges before GPU copies replace external ranges.
        XRDataBuffer destination = _paletteBuffers[_currentFrameSlot];
        destination.BindTo(GetAggregateProgram(), 5u);
        if (renderer.TryMemoryBarrier(EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.ShaderStorage) !=
            ERendererComputeEnqueueStatus.Enqueued)
            return false;

        nuint stride = (nuint)Unsafe.SizeOf<SkinPaletteMatrix>();
        for (int i = 0; i < _externalPaletteCopyCount; i++)
        {
            ref readonly AdvancedGpuPaletteCopy copy = ref _externalPaletteCopies[i];
            ERendererComputeEnqueueStatus status = renderer.TryEnqueueGpuBufferCopy(
                copy.Source,
                checked((nint)(copy.SourceBase * stride)),
                destination,
                checked((nint)(copy.DestinationBase * stride)),
                checked(copy.Count * stride),
                "AdvancedDeformation.ExternalPalette");
            if (status != ERendererComputeEnqueueStatus.Enqueued)
                return false;
            copyAccepted = true;
        }

        return renderer.TryMemoryBarrier(EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.ShaderStorage) ==
            ERendererComputeEnqueueStatus.Enqueued;
    }
}
