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
        out GPUIndirectRenderCommand command)
    {
        if (commandIndex >= TotalCommandCount ||
            _allLoadedCommandsBuffer is null ||
            commandIndex >= _allLoadedCommandsBuffer.ElementCount)
        {
            command = default;
            return false;
        }

        command = _allLoadedCommandsBuffer
            .GetDataRawAtIndex<GPUIndirectRenderCommand>(commandIndex);
        return true;
    }
}
