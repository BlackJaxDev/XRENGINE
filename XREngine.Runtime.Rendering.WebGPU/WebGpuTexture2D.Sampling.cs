using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed unsafe partial class WebGpuTexture2D
{
    private readonly record struct SampledViewKey(int BaseMip, int MipCount);
    private readonly record struct SamplerState(string AddressU, string AddressV,
        string MinFilter, string MagFilter, string MipmapFilter, float MinLod, float MaxLod, int Anisotropy,
        string? Compare);
    private readonly Dictionary<SampledViewKey, int> _sampledViews = [];
    private SamplerState _samplerState;
    private int _samplerHandle;

    internal int GetSampledView(bool depth)
    {
        Generate();
        bool depthFormat = Format is "depth16unorm" or "depth24plus" or "depth32float" or "depth24plus-stencil8";
        if (_samples != 1 || depth != depthFormat || !depth && Format is not ("r8unorm" or "rgba8unorm" or "rgba8unorm-srgb" or "rgba16float"))
            throw Unsupported("Sample", depth
                ? "depth sampling requires a single-sample depth 2D texture"
                : "the selected profile admits only single-sample R8, RGBA8 or RGBA16F color textures");
        int baseMip = Data.LargestMipmapLevel;
        int finalMip = Math.Min(_mipCount - 1, Data.SmallestAllowedMipmapLevel);
        if (baseMip < 0 || baseMip > finalMip)
            throw Unsupported("Sample", "the authored sampled mip range is empty or outside texture storage");
        SampledViewKey key = new(baseMip, finalMip - baseMip + 1);
        if (_sampledViews.TryGetValue(key, out int view)) return view;
        if (_sampledViews.Count >= 32)
            throw Unsupported("Sample", "the texture exceeds 32 retained sampled mip ranges for its current storage generation");
        view = Renderer.CreateTextureView(new BrowserTextureViewDescription(_handle,
            key.BaseMip, key.MipCount, depthFormat ? "depth-only" : "all", Data.Name ?? "Engine sampled view"));
        _sampledViews.Add(key, view);
        return view;
    }

    internal int GetSampler(bool comparison)
    {
        Generate();
        if (Data.LodBias != 0)
            throw Unsupported("Sampler", "nonzero sampler LOD bias has no exact WebGPU encoding");
        if (Data.EnableComparison != comparison || comparison && Data.CompareFunc != ETextureCompareFunc.LessOrEqual)
            throw Unsupported("Sampler", "the shader binding and authored sampler must agree on ordinary or less-equal comparison sampling");
        (string min, string mip, bool mipmapped) = Data.MinFilter switch
        {
            ETexMinFilter.Nearest => ("nearest", "nearest", false),
            ETexMinFilter.Linear => ("linear", "nearest", false),
            ETexMinFilter.NearestMipmapNearest => ("nearest", "nearest", true),
            ETexMinFilter.LinearMipmapNearest => ("linear", "nearest", true),
            ETexMinFilter.NearestMipmapLinear => ("nearest", "linear", true),
            ETexMinFilter.LinearMipmapLinear => ("linear", "linear", true),
            _ => throw Unsupported("Sampler", "the minification filter has no exact WebGPU encoding"),
        };
        string mag = Data.MagFilter switch
        {
            ETexMagFilter.Nearest => "nearest", ETexMagFilter.Linear => "linear",
            _ => throw Unsupported("Sampler", "the magnification filter has no exact WebGPU encoding"),
        };
        float anisotropy = Data.MaxAnisotropy;
        if (!float.IsFinite(anisotropy) || anisotropy < 1 || anisotropy > 16 || anisotropy != MathF.Truncate(anisotropy) ||
            anisotropy > 1 && (min != "linear" || mag != "linear" || mip != "linear"))
            throw Unsupported("Sampler", "anisotropy requires an integer from one to sixteen and linear minification, magnification, and mip filters");
        float minLod = mipmapped ? Math.Max(Data.MinLOD, 0) : 0;
        float maxLod = mipmapped ? Math.Min(Data.MaxLOD, 32) : 0;
        if (minLod > maxLod || minLod > 32 || maxLod < 0 ||
            !mipmapped && (Data.MinLOD > 0 || Data.MaxLOD < 0))
            throw Unsupported("Sampler", "the authored LOD clamp range excludes available sampled levels");
        SamplerState state = new(Address(Data.UWrap), Address(Data.VWrap), min, mag, mip, minLod, maxLod,
            (int)anisotropy, comparison ? "less-equal" : null);
        if (_samplerHandle != 0 && state == _samplerState) return _samplerHandle;
        if (_samplerHandle != 0)
        {
            Renderer.ReleaseEngineDrawDependencies(this);
            Renderer.RetireEngineResourceAfterFrame(_samplerHandle);
            SetField(ref _samplerHandle, 0);
        }
        int handle = Renderer.CreateSampler(new BrowserSamplerDescription(state.AddressU, state.AddressV,
            state.MinFilter, state.MagFilter, state.MipmapFilter, Data.Name ?? "Engine sampler",
            state.MaxLod, state.Anisotropy, LodMinClamp: state.MinLod, Compare: state.Compare));
        SetField(ref _samplerState, state);
        SetField(ref _samplerHandle, handle);
        return handle;
    }

    private void RetireSamplingResources()
    {
        foreach (int view in _sampledViews.Values) Renderer.RetireEngineResourceAfterFrame(view);
        _sampledViews.Clear();
        if (_samplerHandle != 0) Renderer.RetireEngineResourceAfterFrame(_samplerHandle);
        SetField(ref _samplerHandle, 0);
    }

    private static string Address(ETexWrapMode mode) => mode switch
    {
        ETexWrapMode.Repeat => "repeat", ETexWrapMode.MirroredRepeat => "mirror-repeat",
        ETexWrapMode.ClampToEdge => "clamp-to-edge",
        _ => throw Unsupported("Sampler", "the wrap mode has no exact WebGPU encoding"),
    };
}
