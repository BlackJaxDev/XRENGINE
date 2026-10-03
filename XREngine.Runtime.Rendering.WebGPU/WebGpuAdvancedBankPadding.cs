namespace XREngine.Rendering.WebGPU;

/// <summary>Unreferenced descriptors required by the fixed shader layout; never mapped to a logical texture handle.</summary>
internal sealed class WebGpuAdvancedBankPadding : AbstractRenderAPIObject
{
    private readonly WebGpuRendererHost _renderer;
    private readonly int[] _textures = new int[3];
    private readonly int[] _views = new int[3];
    private int _sampler;
    internal WebGpuAdvancedBankPadding(WebGpuRendererHost renderer) : base(renderer) => _renderer = renderer;
    public override bool IsGenerated => _sampler != 0;
    public override nint GetHandle() => _sampler;
    public override string GetDescribingName() => "Advanced unreferenced bank padding";
    internal int Sampler { get { Generate(); return _sampler; } }
    internal int View(int slot) { Generate(); return _views[slot < 10 ? 0 : slot - 9]; }
    public override void Generate()
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        ValidateOwnerGeneration();
        if (IsGenerated) return;
        try
        {
            for (int index = 0; index < 3; index++)
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
    public override void Destroy()
    {
        _renderer.ReleaseEngineDrawDependencies(this);
        for (int index = 0; index < 3; index++)
        {
            if (_views[index] != 0) _renderer.RetireEngineResourceAfterFrame(_views[index]);
            if (_textures[index] != 0) _renderer.RetireEngineResourceAfterFrame(_textures[index]);
            _views[index] = _textures[index] = 0;
        }
        if (_sampler != 0) _renderer.RetireEngineResourceAfterFrame(_sampler);
        SetField(ref _sampler, 0, publishNotifications: false);
    }
}
