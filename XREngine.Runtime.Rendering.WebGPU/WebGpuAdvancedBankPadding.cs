namespace XREngine.Rendering.WebGPU;

/// <summary>Unreferenced descriptors required by the fixed shader layout; never mapped to a logical texture handle.</summary>
internal sealed class WebGpuAdvancedBankPadding : AbstractRenderAPIObject
{
    private readonly WebGpuRendererHost _renderer;
    private readonly int[] _textures = new int[3];
    private readonly int[] _views = new int[3];
    private int _sampler;
    private int _depthTexture;
    private int _depthView;
    private int _comparisonSampler;
    internal WebGpuAdvancedBankPadding(WebGpuRendererHost renderer) : base(renderer) => _renderer = renderer;
    public override bool IsGenerated => _sampler != 0;
    public override nint GetHandle() => _sampler;
    public override string GetDescribingName() => "Advanced unreferenced bank padding";
    internal int Sampler(bool comparison)
    {
        if (comparison) { GenerateDepthPadding(); return _comparisonSampler; }
        Generate();
        return _sampler;
    }
    internal int View(int slot, bool depthComparison)
    {
        if (depthComparison && slot == 9) { GenerateDepthPadding(); return _depthView; }
        Generate();
        return _views[slot < 10 ? 0 : slot - 9];
    }
    public override void Generate()
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        ValidateOwnerGeneration();
        if (IsGenerated) return;
        try
        {
            for (int index = 0; index < _textures.Length; index++)
            {
                int layers = index == 1 ? 6 : 1;
                _textures[index] = _renderer.CreateTexture(new BrowserTextureDescription(1, 1, "rgba8unorm", BrowserTextureUsage.TextureBinding,
                    Label: "Unreferenced native bank padding", ArrayLayerCount: layers));
                _views[index] = _renderer.CreateTextureView(new BrowserTextureViewDescription(_textures[index], ArrayLayerCount: layers,
                    Dimension: index == 0 ? "2d" : index == 1 ? "cube" : "2d-array"));
            }
            SetField(ref _sampler, _renderer.CreateSampler(new BrowserSamplerDescription(Label: "Unreferenced native bank sampler")), publishNotifications: false);
        }
        catch { Destroy(); throw; }
    }

    private void GenerateDepthPadding()
    {
        Generate();
        if (_comparisonSampler != 0) return;
        // Depth descriptors belong only to the selected comparison family.
        // Publish the complete pair together; failure must preserve color padding.
        int texture = 0, view = 0, sampler = 0;
        try
        {
            texture = _renderer.CreateTexture(new BrowserTextureDescription(1, 1, "depth24plus", BrowserTextureUsage.TextureBinding,
                Label: "Unreferenced native depth padding"));
            view = _renderer.CreateTextureView(new BrowserTextureViewDescription(texture, Aspect: "depth-only"));
            sampler = _renderer.CreateSampler(new BrowserSamplerDescription(
                Label: "Unreferenced native depth comparison sampler", Compare: "less-equal"));
        }
        catch
        {
            if (sampler != 0) _renderer.RetireEngineResourceAfterFrame(sampler);
            if (view != 0) _renderer.RetireEngineResourceAfterFrame(view);
            if (texture != 0) _renderer.RetireEngineResourceAfterFrame(texture);
            throw;
        }
        SetField(ref _depthTexture, texture, publishNotifications: false);
        SetField(ref _depthView, view, publishNotifications: false);
        SetField(ref _comparisonSampler, sampler, publishNotifications: false);
    }

    public override void Destroy()
    {
        _renderer.ReleaseEngineDrawDependencies(this);
        for (int index = 0; index < _textures.Length; index++)
        {
            if (_views[index] != 0) _renderer.RetireEngineResourceAfterFrame(_views[index]);
            if (_textures[index] != 0) _renderer.RetireEngineResourceAfterFrame(_textures[index]);
            _views[index] = _textures[index] = 0;
        }
        if (_depthView != 0) _renderer.RetireEngineResourceAfterFrame(_depthView);
        if (_depthTexture != 0) _renderer.RetireEngineResourceAfterFrame(_depthTexture);
        if (_sampler != 0) _renderer.RetireEngineResourceAfterFrame(_sampler);
        if (_comparisonSampler != 0) _renderer.RetireEngineResourceAfterFrame(_comparisonSampler);
        SetField(ref _sampler, 0, publishNotifications: false);
        SetField(ref _depthTexture, 0, publishNotifications: false);
        SetField(ref _depthView, 0, publishNotifications: false);
        SetField(ref _comparisonSampler, 0, publishNotifications: false);
    }
}
