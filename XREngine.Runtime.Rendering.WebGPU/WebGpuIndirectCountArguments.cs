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
        int output = await _renderer.CreateEngineBufferAsync(this, new BrowserBufferDescription(checked((int)(maximum * 20)),
            BrowserBufferUsage.Storage | BrowserBufferUsage.Indirect, "Count-masked indexed arguments"));
        if (!Accept(output)) return;
        _output = output;
        int parameters = await _renderer.CreateEngineBufferAsync(this, new BrowserBufferDescription(16,
            BrowserBufferUsage.Uniform | BrowserBufferUsage.CopyDestination, "Indirect count parameters"));
        if (!Accept(parameters)) return;
        _parameters = parameters;
        Span<byte> parameterBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(parameterBytes, offset / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameterBytes[4..], stride / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameterBytes[8..], countOffset / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameterBytes[12..], maximum);
        _renderer.StageEngineBufferPreparation(_parameters, 0, parameterBytes);
        int bindings = await _renderer.CreateEngineBindingGroupAsync(this,
            $$$"""{"label":"Indirect count masking","layout":{{{_kernel.Layout}}},"entries":[{"binding":0,"resource":{{{_sourceHandle}}},"size":{{{sourceBytes}}}},{"binding":1,"resource":{{{_countHandle}}},"size":{{{countBytes}}}},{"binding":2,"resource":{{{_output}}},"size":{{{maximum * 20}}}},{"binding":3,"resource":{{{_parameters}}},"size":16}]}""");
        if (!Accept(bindings)) return;
        _bindings = bindings;
        int command = await _renderer.PrepareEngineCommandsAsync(this,
            $$$"""{"label":"Indirect count masking","commands":[{"type":"compute","pipeline":{{{_kernel.Pipeline}}},"bindings":[{"index":0,"group":{{{_bindings}}},"dynamicOffsets":[]}],"workgroups":[{{{(maximum + 63) / 64}}},1,1]}]}""");
        if (Accept(command)) _command = command;
    }

    private bool Accept(int handle)
    {
        if (!_disposed && _renderer.AcceptsBackendWork) return true;
        _renderer.RetireEngineResourceAfterFrame(handle);
        return false;
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
        _renderer.CancelEngineResourceRequests(this);
        _renderer.RetireEngineResourceAfterFrame(_command);
        _renderer.RetireEngineResourceAfterFrame(_bindings);
        _renderer.RetireEngineResourceAfterFrame(_parameters);
        _renderer.RetireEngineResourceAfterFrame(_output);
        _command = _bindings = _parameters = _output = 0;
    }
}
