namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Publishes separately retained exact texture and sampler identities without consulting mutable texture sampling settings.</summary>
    internal void BindAdvancedTexture(string name, AbstractRenderAPIObject textureOwner, int view,
        AbstractRenderAPIObject samplerOwner, int sampler)
    {
        if (!_samplers.TryGetValue(name, out SamplerSlots slots) || slots.Texture < 0 || slots.Sampler < 0 ||
            view == 0 || sampler == 0 || textureOwner.Owner != Renderer || samplerOwner.Owner != Renderer ||
            textureOwner.IsRetired || samplerOwner.IsRetired)
            throw UnsupportedBinding(name, "an exact live native texture/sampler pair is required");
        _resourceHandles[slots.Texture] = view;
        _resourceOwners[slots.Texture] = textureOwner;
        _resourceHandles[slots.Sampler] = sampler;
        _resourceOwners[slots.Sampler] = samplerOwner;
    }
}
