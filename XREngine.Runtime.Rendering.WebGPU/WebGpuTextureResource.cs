namespace XREngine.Rendering.WebGPU;

/// <summary>Generation identity and exact bounds of a physical texture, including authored views.</summary>
internal readonly record struct WebGpuTextureResource(AbstractRenderAPIObject Owner, int Handle, uint Width, uint Height,
    int Mips, int Layers, uint Samples, string Format, bool Storage, int BaseMip = 0, int BaseLayer = 0, string Aspect = "all")
{
    public static WebGpuTextureResource Resolve(WebGpuRendererHost renderer, XRTexture resource)
    {
        AbstractRenderAPIObject api = renderer.GetOrCreateAPIRenderObject(resource, generateNow: true)!;
        api.Generate();
        return api switch
        {
            WebGpuTexture2D value => new(value, value.ResourceHandle, value.Width, value.Height,
                value.Data.Mipmaps.Length, 1, value.SampleCount, value.Format, value.Data.RequiresStorageUsage),
            WebGpuTexture2DArray value => new(value, value.ResourceHandle, value.Width, value.Height,
                value.MipLevelCount, value.ArrayLayerCount, value.SampleCount, value.Format, value.Data.RequiresStorageUsage),
            WebGpuTextureCube value => new(value, value.ResourceHandle, value.Width, value.Height,
                value.MipLevelCount, value.ArrayLayerCount, value.SampleCount, value.Format, value.Data.RequiresStorageUsage),
            WebGpuTextureView value => value.Resource,
            _ => throw new NotSupportedException($"WebGPU.Texture.ResourceUnsupported: '{resource.GetType().Name}' has no admitted physical texture."),
        };
    }

    public static void MarkRecorded(AbstractRenderAPIObject owner)
    {
        switch (owner)
        {
            case WebGpuTexture2D value: value.MarkRecorded(); break;
            case WebGpuTexture2DArray value: value.MarkRecorded(); break;
            case WebGpuTextureCube value: value.MarkRecorded(); break;
            case WebGpuTextureView value: value.MarkRecorded(); break;
            default: throw new NotSupportedException("WebGPU.Texture.RecordUnsupported: resource cannot retain recorded use.");
        }
    }
}
