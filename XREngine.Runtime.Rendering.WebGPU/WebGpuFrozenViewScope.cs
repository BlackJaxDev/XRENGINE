namespace XREngine.Rendering.WebGPU;

/// <summary>Restores the enclosing immutable view without a per-draw allocation.</summary>
internal readonly struct WebGpuFrozenViewScope(WebGpuRendererHost renderer, RenderFrameViewSelection? previous) : IDisposable
{
    public void Dispose() => renderer.SetFrozenView(previous);
}
