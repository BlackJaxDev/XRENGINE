namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    /// <summary>Associates the caller-owned update/collect/swap/render lifecycle with this existing renderer.</summary>
    public OwnerScope EnterOwnerScope() => new(this);

    public readonly struct OwnerScope : IDisposable
    {
        private readonly ThreadCurrentScope _scope;
        internal OwnerScope(WebGpuRendererHost renderer) => _scope = EnterThreadCurrentScope(renderer);
        public void Dispose() => _scope.Dispose();
    }
}
