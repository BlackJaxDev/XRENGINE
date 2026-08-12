namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    /// <summary>
    /// Reads the immutable render-side CPU mirror for advanced extraction.
    /// This never maps a GPU-only resource, waits for completion, or performs
    /// readback; callers must hold a <see cref="RenderWorldSnapshot"/>.
    /// </summary>
    public bool TryGetAdvancedPreparationCommand(
        uint commandIndex,
        out DrawMetadata command)
    {
        if (commandIndex >= TotalCommandCount ||
            _allLoadedDrawMetadataBuffer is null ||
            commandIndex >= _allLoadedDrawMetadataBuffer.ElementCount)
        {
            command = default;
            return false;
        }

        command = _allLoadedDrawMetadataBuffer
            .GetDataRawAtIndex<DrawMetadata>(commandIndex);
        return true;
    }
}
