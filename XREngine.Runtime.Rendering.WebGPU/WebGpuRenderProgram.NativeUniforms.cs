namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Copies an exact verified native uniform image before the dispatch snapshots its dynamic offset.</summary>
    internal void SetUniformBlock(string name, ReadOnlySpan<byte> bytes)
    {
        if (!IsGenerated)
            throw UnsupportedBinding(name, "the cooked program must be prepared before publishing a native uniform image");
        foreach (WebGpuUniformBlock block in _blocks)
        {
            if (block.Contract.Name != name) continue;
            if (bytes.Length != block.Bytes.Length)
                throw UnsupportedBinding(name, "the native uniform image must match the entire cooked physical structure");
            bytes.CopyTo(block.Bytes);
            return;
        }
        throw UnsupportedBinding(name, "the native uniform image has no exact named cooked block");
    }
}
