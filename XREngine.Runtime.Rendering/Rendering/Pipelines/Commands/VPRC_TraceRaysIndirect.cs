namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Resolves ray-dispatch dimensions from a named pipeline buffer and then performs a native ray dispatch.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_TraceRaysIndirect : VPRC_DispatchRays
{
    public string IndirectArgsBufferName { get; set; } = string.Empty;
    public uint IndirectArgsByteOffset { get; set; }

    protected override bool TryResolveDispatchDimensions(out uint width, out uint height, out uint depth, out string failure)
    {
        width = 0;
        height = 0;
        depth = 0;

        if (string.IsNullOrWhiteSpace(IndirectArgsBufferName))
        {
            failure = "IndirectArgsBufferName must be set.";
            return false;
        }

        if (!ActivePipelineInstance.TryGetBuffer(IndirectArgsBufferName, out XRDataBuffer? buffer) || buffer is null)
        {
            failure = $"Indirect args buffer '{IndirectArgsBufferName}' was not found.";
            return false;
        }

        if (buffer.ClientSideSource is null)
        {
            failure = $"Indirect args buffer '{IndirectArgsBufferName}' does not have CPU-visible contents to resolve dispatch dimensions.";
            return false;
        }

        uint stride = (uint)sizeof(uint);
        uint requiredBytes = IndirectArgsByteOffset + stride * 3u;
        if (requiredBytes > buffer.ClientSideSource.Length)
        {
            failure = $"Indirect args buffer '{IndirectArgsBufferName}' is too small for a three-uint dispatch record at byte offset {IndirectArgsByteOffset}.";
            return false;
        }

        width = buffer.Get<uint>(IndirectArgsByteOffset) ?? 0u;
        height = buffer.Get<uint>(IndirectArgsByteOffset + stride) ?? 0u;
        depth = buffer.Get<uint>(IndirectArgsByteOffset + stride * 2u) ?? 0u;

        if (width == 0 || height == 0 || depth == 0)
        {
            failure = "Indirect ray dispatch dimensions must be greater than zero.";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}
