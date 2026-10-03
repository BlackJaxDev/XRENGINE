namespace XREngine.Rendering.WebGPU;

/// <summary>Owns a retained indexed-indirect operation and its optional GPU count lowering resources.</summary>
internal sealed class WebGpuIndirectDrawCommand(WebGpuRendererHost renderer,
    WebGpuIndirectCountArguments? maskedArguments) : IDisposable
{
    public WebGpuIndirectCountArguments? MaskedArguments { get; } = maskedArguments;
    public int CommandHandle { get; set; }
    public bool IsReady => MaskedArguments is null || MaskedArguments.IsReady;

    public void Dispose()
    {
        renderer.RetireEngineResourceAfterFrame(CommandHandle);
        CommandHandle = 0;
        MaskedArguments?.Dispose();
    }
}
