namespace XREngine.Rendering.WebGPU;

/// <summary>One compatible native kernel and bounded exact texture/sampler closure.</summary>
internal sealed class WebGpuAdvancedShadingCohort : IDisposable
{
    internal const int SlotCount = WebGpuAdvancedMaterialContract.TextureSlotCount;
    private readonly WebGpuRendererHost _renderer;
    private readonly int[] _textureHandles = new int[SlotCount];
    private readonly uint[] _samplingKeys = new uint[SlotCount];
    private readonly int[] _mips = new int[SlotCount];
    private readonly WebGpuResourceRequest?[] _viewRequests = new WebGpuResourceRequest?[SlotCount];
    internal readonly WebGpuAdvancedTexturePair[] Pairs = new WebGpuAdvancedTexturePair[SlotCount];
    internal uint[] BindingWords = new uint[SlotCount * 8];
    internal int BindingWordCount = SlotCount * 8;
    internal readonly AbstractRenderAPIObject?[] TextureOwners = new AbstractRenderAPIObject?[SlotCount];
    internal readonly WebGpuAdvancedSampler?[] SamplerOwners = new WebGpuAdvancedSampler?[SlotCount];
    internal readonly int[] Views = new int[SlotCount];
    internal readonly WebGpuOwnedStorageBuffer Bindings;
    internal bool UberRaster;
    internal uint Kernel;
    internal int PairCount;
    internal WebGpuAdvancedShadingCohort(WebGpuRendererHost renderer)
    { _renderer = renderer; Bindings = new(renderer, "Advanced exact texture binding map"); }
    internal bool Matches(uint kernel, ReadOnlySpan<WebGpuAdvancedTexturePair> pairs, bool uberRaster = false)
    {
        if (UberRaster != uberRaster || Kernel != kernel || PairCount != pairs.Length) return false;
        for (int index = 0; index < PairCount; index++) if (Pairs[index] != pairs[index]) return false;
        return true;
    }
    internal void SetView(int slot, in WebGpuTextureResource resource, uint samplingKey = 0)
    {
        AdvancedEngineSurfaceSamplingKey sampling = new(samplingKey);
        int baseMip = samplingKey == 0 ? 0 : sampling.BaseMip;
        int mipCount = samplingKey == 0 ? resource.Mips : sampling.ViewMipCount;
        if (samplingKey != 0 && (!sampling.IsValid || sampling.StorageMipCount != resource.Mips))
            throw new NotSupportedException("WebGPU.Advanced.SampledView: frozen mip range does not match physical storage.");
        if (Views[slot] != 0 && ReferenceEquals(TextureOwners[slot], resource.Owner) &&
            _textureHandles[slot] == resource.Handle && _mips[slot] == resource.Mips && _samplingKeys[slot] == samplingKey) return;
        int candidate = _renderer.CreateEngineReplacement(this, ref _viewRequests[slot], 3, new BrowserTextureViewDescription(resource.Handle, BaseMip: baseMip, MipCount: mipCount,
            ArrayLayerCount: resource.Layers, Dimension: slot < 10 ? "2d" : slot == 10 ? "cube" : "2d-array",
            Aspect: WebGpuTextureFormatContract.IsDepth(resource.Format) ? "depth-only" : "all"));
        ReleaseTextureView(slot);
        Views[slot] = candidate;
        _samplingKeys[slot] = samplingKey;
        TextureOwners[slot] = resource.Owner; _textureHandles[slot] = resource.Handle; _mips[slot] = resource.Mips;
    }
    internal void ReleaseView(int slot)
    {
        if (_viewRequests[slot] is { } request) _renderer.CancelEngineResourceRequest(request);
        _viewRequests[slot] = null;
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
        _samplingKeys[slot] = 0;
        TextureOwners[slot] = null;
    }
    public void Dispose()
    {
        _renderer.CancelEngineResourceRequests(this);
        Bindings.Dispose();
        for (int slot = 0; slot < SlotCount; slot++) ReleaseView(slot);
    }
}
