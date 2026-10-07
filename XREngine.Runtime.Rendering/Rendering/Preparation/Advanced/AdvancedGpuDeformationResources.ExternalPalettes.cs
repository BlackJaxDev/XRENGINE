using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    private readonly AdvancedGpuPaletteCopy[] _externalPaletteCopies;
    private int _externalPaletteCopyCount;

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
            throw new InvalidOperationException("External palette copies exceed the draw capacity.");
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
