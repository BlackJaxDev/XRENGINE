namespace XREngine.Rendering.WebGPU;

/// <summary>One compatible native kernel and bounded exact texture/sampler closure.</summary>
internal sealed class WebGpuAdvancedShadingCohort : IDisposable
{
    internal const int SlotCount = WebGpuAdvancedMaterialContract.TextureSlotCount;
    private readonly WebGpuRendererHost _renderer;
    private readonly int[] _textureHandles = new int[SlotCount];
    private readonly int[] _mips = new int[SlotCount];
    internal readonly WebGpuAdvancedTexturePair[] Pairs = new WebGpuAdvancedTexturePair[SlotCount];
    internal uint[] BindingWords = new uint[SlotCount * 8];
    internal int BindingWordCount = SlotCount * 8;
    internal readonly AbstractRenderAPIObject?[] TextureOwners = new AbstractRenderAPIObject?[SlotCount];
    internal readonly WebGpuAdvancedSampler?[] SamplerOwners = new WebGpuAdvancedSampler?[SlotCount];
    internal readonly int[] Views = new int[SlotCount];
    internal readonly WebGpuOwnedStorageBuffer Bindings;
    internal uint Kernel;
    internal int PairCount;
    internal WebGpuAdvancedShadingCohort(WebGpuRendererHost renderer)
    { _renderer = renderer; Bindings = new(renderer, "Advanced exact texture binding map"); }
    internal bool Matches(uint kernel, ReadOnlySpan<WebGpuAdvancedTexturePair> pairs)
    {
        if (Kernel != kernel || PairCount != pairs.Length) return false;
        for (int index = 0; index < PairCount; index++) if (Pairs[index] != pairs[index]) return false;
        return true;
    }
    internal void SetView(int slot, in WebGpuTextureResource resource)
    {
        if (Views[slot] != 0 && ReferenceEquals(TextureOwners[slot], resource.Owner) &&
            _textureHandles[slot] == resource.Handle && _mips[slot] == resource.Mips) return;
        ReleaseTextureView(slot);
        Views[slot] = _renderer.CreateTextureView(new BrowserTextureViewDescription(resource.Handle, MipCount: resource.Mips,
            ArrayLayerCount: resource.Layers, Dimension: slot < 10 ? "2d" : slot == 10 ? "cube" : "2d-array",
            Aspect: WebGpuTextureFormatContract.IsDepth(resource.Format) ? "depth-only" : "all"));
        TextureOwners[slot] = resource.Owner; _textureHandles[slot] = resource.Handle; _mips[slot] = resource.Mips;
    }
    internal void ReleaseView(int slot)
    {
        ReleaseTextureView(slot);
        SetSampler(slot, null);
    }
    internal void SetSampler(int slot, WebGpuAdvancedSampler? sampler)
    {
        WebGpuAdvancedSampler? previous = SamplerOwners[slot];
        if (ReferenceEquals(previous, sampler)) return;
        sampler?.Retain();
        SamplerOwners[slot] = sampler;
        previous?.Release();
    }
    private void ReleaseTextureView(int slot)
    {
        if (Views[slot] != 0)
        {
            if (TextureOwners[slot] is { } owner) _renderer.ReleaseEngineStorageGeneration(owner, Views[slot]);
            _renderer.RetireEngineResourceAfterFrame(Views[slot]);
        }
        Views[slot] = _textureHandles[slot] = _mips[slot] = 0;
        TextureOwners[slot] = null;
    }
    public void Dispose()
    {
        Bindings.Dispose();
        for (int slot = 0; slot < SlotCount; slot++) ReleaseView(slot);
    }
}
