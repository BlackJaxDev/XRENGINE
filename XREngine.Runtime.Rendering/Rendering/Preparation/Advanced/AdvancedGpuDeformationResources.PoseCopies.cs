namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    private AdvancedGpuDeformationPaletteCopy[][] _paletteCopies = [];
    private int[] _slotPaletteCopyCounts = [];
    private int _paletteCopyCount;

    private void BeginPaletteCopies()
    {
        if (_paletteCopies.Length == 0)
        {
            _paletteCopies = new AdvancedGpuDeformationPaletteCopy[_frameSlotCount][];
            _slotPaletteCopyCounts = new int[_frameSlotCount];
            for (int slot = 0; slot < _frameSlotCount; slot++)
                _paletteCopies[slot] = [];
        }
        // This slot was completion-acquired before frame authoring began.
        Array.Clear(_paletteCopies[_currentFrameSlot], 0, _slotPaletteCopyCounts[_currentFrameSlot]);
        _slotPaletteCopyCounts[_currentFrameSlot] = 0;
        _paletteCopyCount = 0;
    }

    private void CapturePaletteCopy(XRDataBuffer source, uint sourceBase, uint count)
    {
        if (count == 0)
            return;
        if (_paletteCopyCount >= _maximumDeformationJobs)
            throw new NotSupportedException("AggregateDeformation.GpuPoseCapacity: GPU palette copies exceed the bounded job capacity.");
        if (AbstractRenderer.Current is not IAdvancedAggregateDeformationBackendCapability capability ||
            !capability.TryCaptureAggregateGpuPaletteCopy(source, checked(sourceBase * 48u),
                checked(_paletteCount * 48u), checked(count * 48u), out AdvancedGpuDeformationPaletteCopy copy))
            throw new NotSupportedException("AggregateDeformation.GpuPoseCopyUnavailable: the exact GPU palette generation must already be resident on an ordered GPU-copy backend; a CPU mirror cannot supply it.");
        if (_paletteCopies[_currentFrameSlot].Length == _paletteCopyCount)
            Array.Resize(ref _paletteCopies[_currentFrameSlot], Math.Min(_maximumDeformationJobs,
                Math.Max(4, checked(_paletteCopyCount * 2))));
        _paletteCopies[_currentFrameSlot][_paletteCopyCount++] = copy;
        _slotPaletteCopyCounts[_currentFrameSlot] = _paletteCopyCount;
    }

    private ReadOnlyMemory<AdvancedGpuDeformationPaletteCopy> PublishPaletteCopies()
        => _paletteCopyCount == 0 ? ReadOnlyMemory<AdvancedGpuDeformationPaletteCopy>.Empty
            : _paletteCopies[_currentFrameSlot].AsMemory(0, _paletteCopyCount);
}
