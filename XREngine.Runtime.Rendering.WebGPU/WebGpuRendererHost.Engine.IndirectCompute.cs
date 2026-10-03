using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    /// <summary>Consumes work-classification arguments directly from their canonical slot-owned allocation.</summary>
    internal void DispatchComputeIndirect(XRRenderProgram program, WebGpuOwnedStorageBuffer arguments, nuint byteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        RequireReady();
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        try
        {
            if (!_engineRecording)
                throw new InvalidOperationException("WebGPU.Compute.FrameRequired: indirect compute requires an ordered engine frame.");
            if (arguments.Owner != this || arguments.IsRetired || !arguments.IsGenerated || byteOffset % 4 != 0 ||
                byteOffset > uint.MaxValue || (ulong)byteOffset + 12 > arguments.ByteLength)
                throw new ArgumentOutOfRangeException(nameof(arguments), "Indirect compute arguments require three aligned uint values in live owned storage.");
            if (!api.TryPrepareForCompute())
            {
                MarkEngineDrawPending();
                return;
            }
            api.RecordComputeIndirect(arguments, arguments.ResourceHandle, (uint)byteOffset);
        }
        finally { api.ClearTransientComputeBindings(); }
    }

    /// <summary>Records GPU-written compute dimensions after their producer without mapping the argument buffer.</summary>
    public void DispatchComputeIndirect(XRRenderProgram program, XRDataBuffer arguments, nuint byteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        RequireReady();
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        try
        {
            if (!_engineRecording)
                throw new InvalidOperationException("WebGPU.Compute.FrameRequired: indirect compute requires an ordered engine frame.");
            if (arguments.Target is not (EBufferTarget.DispatchIndirectBuffer or EBufferTarget.DrawIndirectBuffer) ||
                byteOffset % 4 != 0 || byteOffset > uint.MaxValue || (ulong)byteOffset + 12 > arguments.Length)
                throw new ArgumentOutOfRangeException(nameof(byteOffset), "Indirect compute requires three aligned GPU-written uint dimensions.");
            WebGpuDataBuffer buffer = (WebGpuDataBuffer)GetOrCreateAPIRenderObject(arguments, generateNow: true)!;
            buffer.StagePendingUpload();
            if (!api.TryPrepareForCompute())
            {
                MarkEngineDrawPending();
                return;
            }
            api.RecordComputeIndirect(buffer, (uint)byteOffset);
        }
        finally { api.ClearTransientComputeBindings(); }
    }
}
