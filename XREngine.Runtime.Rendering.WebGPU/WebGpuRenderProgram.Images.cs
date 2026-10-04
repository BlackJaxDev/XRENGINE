using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private readonly record struct ImageViewKey(AbstractRenderAPIObject Owner, int Texture, int Mip, int Layer, int Layers, string Dimension);
    private readonly Dictionary<ImageViewKey, int> _imageViews = [];

    /// <summary>Publishes one exact storage image slot using the engine's existing compute image API.</summary>
    private void SetImage(uint binding, IRenderTextureResource texture, int mip, bool layered, int layer,
        XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)
    {
        Generate();
        int matched = -1;
        for (int index = 0; index < Artifact.Resources.Length; index++)
        {
            ShaderStageResourceLayout resource = Artifact.Resources[index];
            if (resource.Contract.Kind != ShaderAbiResourceKind.StorageImage || resource.Contract.Binding != binding) continue;
            if (matched >= 0) throw UnsupportedBinding(resource.Contract.Name, "numeric image binding is ambiguous across groups");
            matched = index;
        }
        if (matched < 0) throw UnsupportedBinding(Data.Name ?? "program", $"image binding {binding} is absent from the cooked layout");
        ShaderStageResourceLayout selected = Artifact.Resources[matched];
        ShaderTextureBindingType shape = _textureShapes[matched];
        if (!shape.IsStorage)
            throw UnsupportedBinding(selected.Contract.Name, "the cooked storage image shape is invalid");
        string imageAccess = access switch
        {
            XRRenderProgram.EImageAccess.ReadOnly => "read-only", XRRenderProgram.EImageAccess.WriteOnly => "write-only",
            XRRenderProgram.EImageAccess.ReadWrite => "read-write", _ => throw UnsupportedBinding(selected.Contract.Name, "invalid image access"),
        };
        if (imageAccess != shape.StorageAccess || ImageFormat(format) != shape.StorageFormat)
            throw UnsupportedBinding(selected.Contract.Name, "image access and numeric format must exactly match the cooked layout");
        if (texture is not XRTexture source)
            throw UnsupportedBinding(selected.Contract.Name, "storage images require an engine texture");
        WebGpuTextureResource physical = WebGpuTextureResource.Resolve(Renderer, source);
        if (!physical.Storage || physical.Samples != 1 || physical.Format != shape.StorageFormat || physical.Aspect != "all")
            throw UnsupportedBinding(selected.Contract.Name, "the texture must declare matching single-sample storage usage and the all aspect");
        int firstLayer = layered ? 0 : layer, layers = layered ? physical.Layers : 1;
        if (mip < 0 || mip >= physical.Mips || firstLayer < 0 || firstLayer >= physical.Layers ||
            shape.ViewDimension == "2d" && layers != 1)
            throw UnsupportedBinding(selected.Contract.Name, "the selected storage mip, layer range or dimension is invalid");
        ImageViewKey key = new(physical.Owner, physical.Handle, physical.BaseMip + mip,
            physical.BaseLayer + firstLayer, layers, shape.ViewDimension);
        if (!_imageViews.TryGetValue(key, out int view))
        {
            if (_imageViews.Count >= 1024) throw UnsupportedBinding(selected.Contract.Name, "the program exceeds 1024 retained storage subresource views");
            view = Renderer.CreateEngineTextureView(this, new BrowserTextureViewDescription(physical.Handle, key.Mip, 1, "all",
                selected.Contract.Name, key.Layer, key.Layers, key.Dimension));
            _imageViews.Add(key, view);
        }
        if (access != XRRenderProgram.EImageAccess.ReadOnly) source.MarkGpuWritable();
        _resourceHandles[matched] = view;
        _resourceOwners[matched] = physical.Owner;
    }

    private static string ImageFormat(XRRenderProgram.EImageFormat format) => format switch
    {
        XRRenderProgram.EImageFormat.R32F => "r32float", XRRenderProgram.EImageFormat.RG32F => "rg32float",
        XRRenderProgram.EImageFormat.RGBA32F => "rgba32float", XRRenderProgram.EImageFormat.RGBA16F => "rgba16float",
        XRRenderProgram.EImageFormat.RGBA8 => "rgba8unorm", XRRenderProgram.EImageFormat.R32UI => "r32uint",
        XRRenderProgram.EImageFormat.RG32UI => "rg32uint", XRRenderProgram.EImageFormat.RGBA32UI => "rgba32uint",
        XRRenderProgram.EImageFormat.RGBA16UI => "rgba16uint",
        XRRenderProgram.EImageFormat.R32I => "r32sint", XRRenderProgram.EImageFormat.RG32I => "rg32sint",
        XRRenderProgram.EImageFormat.RGBA32I => "rgba32sint",
        _ => throw UnsupportedBinding(format.ToString(), "the image format has no exact admitted storage encoding"),
    };

    private void ReleaseImageViewsUsing(AbstractRenderAPIObject resource)
    {
        // Resource retirement is cold; collect keys before changing the retained view dictionary.
        List<ImageViewKey>? obsolete = null;
        foreach ((ImageViewKey key, int handle) in _imageViews)
            if (ReferenceEquals(key.Owner, resource) || key.Owner is WebGpuTextureView view && view.DependsOn(resource))
            {
                Renderer.RetireEngineResourceAfterFrame(handle);
                (obsolete ??= []).Add(key);
            }
        if (obsolete is not null)
            foreach (ImageViewKey key in obsolete) _imageViews.Remove(key);
    }

    private void ClearImageViews()
    {
        foreach (int handle in _imageViews.Values) Renderer.RetireEngineResourceAfterFrame(handle);
        _imageViews.Clear();
    }
}
