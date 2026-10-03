using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains one immutable count-lowering plan; only its GPU-produced contents vary per frame.</summary>
internal sealed class WebGpuIndirectCountArguments : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly WebGpuIndirectCountKernel _kernel;
    private readonly Task _preparation;
    private readonly int _sourceHandle;
    private readonly int _countHandle;
    private int _output;
    private int _parameters;
    private int _bindings;
    private int _command;
    private bool _disposed;

    public WebGpuIndirectCountArguments(WebGpuRendererHost renderer, WebGpuDataBuffer source,
        WebGpuDataBuffer count, uint maximum, uint stride, uint offset, uint countOffset)
    {
        _renderer = renderer;
        _kernel = renderer.GetIndirectCountKernel();
        _sourceHandle = source.ResourceHandle;
        _countHandle = count.ResourceHandle;
        _preparation = PrepareAsync(source.Data.Length, count.Data.Length, maximum, stride, offset, countOffset);
    }

    public int ResourceHandle => _output;
    public bool IsReady
    {
        get
        {
            if (_preparation.IsFaulted || _preparation.IsCanceled) _preparation.GetAwaiter().GetResult();
            return !_disposed && _command != 0 && _preparation.IsCompletedSuccessfully;
        }
    }

    public bool UsesHandle(int handle) => handle == _sourceHandle || handle == _countHandle;

    private async Task PrepareAsync(uint sourceBytes, uint countBytes,
        uint maximum, uint stride, uint offset, uint countOffset)
    {
        await _kernel.Preparation;
        if (_disposed || !_renderer.AcceptsBackendWork) return;
        _output = _renderer.CreateBuffer(new BrowserBufferDescription(checked((int)(maximum * 20)),
            BrowserBufferUsage.Storage | BrowserBufferUsage.Indirect, "Count-masked indexed arguments"));
        _parameters = _renderer.CreateBuffer(new BrowserBufferDescription(16,
            BrowserBufferUsage.Uniform | BrowserBufferUsage.CopyDestination, "Indirect count parameters"));
        Span<byte> parameters = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, offset / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[4..], stride / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[8..], countOffset / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[12..], maximum);
        _renderer.WriteBuffer(_parameters, 0, parameters);
        _bindings = _renderer.CreateBindingGroup(
            $$$"""{"label":"Indirect count masking","layout":{{{_kernel.Layout}}},"entries":[{"binding":0,"resource":{{{_sourceHandle}}},"size":{{{sourceBytes}}}},{"binding":1,"resource":{{{_countHandle}}},"size":{{{countBytes}}}},{"binding":2,"resource":{{{_output}}},"size":{{{maximum * 20}}}},{"binding":3,"resource":{{{_parameters}}},"size":16}]}""");
        _command = _renderer.PrepareCommands(
            $$$"""{"label":"Indirect count masking","commands":[{"type":"compute","pipeline":{{{_kernel.Pipeline}}},"bindings":[{"index":0,"group":{{{_bindings}}},"dynamicOffsets":[]}],"workgroups":[{{{(maximum + 63) / 64}}},1,1]}]}""");
    }

    public void Record()
    {
        if (!IsReady) throw new InvalidOperationException("WebGPU.Indirect.CountMaskPending: preparation has not completed.");
        _renderer.RecordEngineCommands(_command, []);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.RetireEngineResourceAfterFrame(_command);
        _renderer.RetireEngineResourceAfterFrame(_bindings);
        _renderer.RetireEngineResourceAfterFrame(_parameters);
        _renderer.RetireEngineResourceAfterFrame(_output);
        _command = _bindings = _parameters = _output = 0;
    }
}
