using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-slot ownership of the two exact frozen coverage samplers.</summary>
internal sealed class WebGpuAdvancedVisibilitySampling : IDisposable
{
    private readonly WebGpuAdvancedSampler?[] _samplers = new WebGpuAdvancedSampler?[7];
    private static readonly Action<WebGpuAdvancedSampler> Unused = static _ => { };

    internal void Bind(WebGpuRendererHost renderer, WebGpuRenderProgram program,
        AdvancedGpuScenePublicationSnapshot snapshot, in WebGpuAdvancedVisibilityBucket bucket)
    {
        BindRole(renderer, program, snapshot, 0, "CoverageTexture", bucket.CoverageTexture,
            bucket.Key.CoverageSampler, bucket.Key.CoverageSamplingKey);
        BindRole(renderer, program, snapshot, 1, "OpacityTexture", bucket.OpacityTexture,
            bucket.Key.OpacitySampler, bucket.Key.OpacitySamplingKey);
    }

    internal static readonly string[] UberNames = ["_MainTex", "_BumpMap", "_AlphaMask", "_PBRMetallicMaps", "_PBRSmoothnessMaps", "_SpecularMap", "_EmissionMap"];

    internal void BindUber(WebGpuRendererHost renderer, WebGpuRenderProgram program,
        AdvancedGpuScenePublicationSnapshot snapshot, in AdvancedUberBaseSurfaceRecord surface)
    {
        int roleCount = program.Artifact.Pass is "uber-visibility" or "uber-visibility-msaa" ? 3 : AdvancedUberBaseSurfaceRecord.RoleCount;
        for (int role = 0; role < roleCount; role++)
        {
            if (!UberBaseMaterialProfile.IsRoleActive(role, surface.Features)) { SetSampler(role, null); continue; }
            AdvancedMaterialTextureBinding binding = surface.GetBinding(role);
            if (!snapshot.ResourcePayloads.TryGetTextureSource(binding.Texture.Handle, out XRTexture source, out ulong generation) || source is not XRTexture2D texture ||
                !snapshot.Textures.TryGet(binding.Texture.Handle, out AdvancedTextureRecord record) ||
                !AdvancedGpuResourceSourceEncoder.TryEncode(source, EAdvancedResourceFallback.Zero, out AdvancedGpuResourceBindingSource current, out _, out _) ||
                current.SourceContentGeneration != generation || current.TextureRecord.Dimension != record.Dimension ||
                current.TextureRecord.Width != record.Width || current.TextureRecord.Height != record.Height || current.TextureRecord.FormatClass != record.FormatClass)
                throw new NotSupportedException("WebGPU.Advanced.UberRasterTextureChanged: each raster role must retain its exact published image and content generation.");
            BindRole(renderer, program, snapshot, role, UberNames[role], texture, binding.Sampler.Handle, surface.GetSamplingKey(role));
        }
    }

    private void BindRole(WebGpuRendererHost renderer, WebGpuRenderProgram program,
        AdvancedGpuScenePublicationSnapshot snapshot, int slot, string name, XRTexture2D? texture,
        AdvancedGpuHandle samplerHandle, uint samplingKey)
    {
        if (texture is null)
        { SetSampler(slot, null); return; }
        if (!snapshot.Samplers.TryGet(samplerHandle, out AdvancedSamplerRecord record))
            throw new NotSupportedException("WebGPU.Advanced.CoverageSamplerMissing: the retained coverage sampler is absent.");
        if (samplingKey != 0) record = new AdvancedEngineSurfaceSamplingKey(samplingKey).ApplyTo(in record);
        var key = (snapshot.DatabaseEpoch, samplerHandle, samplingKey);
        WebGpuAdvancedSampler? sampler = _samplers[slot];
        if (sampler is null || sampler.Key != key || !sampler.Matches(in record))
        {
            sampler = new(renderer, in record, key, Unused);
            SetSampler(slot, sampler);
        }
        sampler.Generate();
        WebGpuTexture2D owner = (WebGpuTexture2D)renderer.GetOrCreateAPIRenderObject(texture, generateNow: true)!;
        int view = samplingKey == 0 ? owner.GetSampledView(false) : owner.GetFrozenSampledView(samplingKey);
        program.BindAdvancedTexture(name, owner, view, sampler, sampler.ResourceHandle);
    }

    private void SetSampler(int slot, WebGpuAdvancedSampler? value)
    {
        if (ReferenceEquals(_samplers[slot], value)) return;
        value?.Retain();
        WebGpuAdvancedSampler? previous = _samplers[slot];
        _samplers[slot] = value;
        previous?.Release();
    }

    public void Dispose()
    { for (int slot = 0; slot < _samplers.Length; slot++) SetSampler(slot, null); }
}
