using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public delegate bool RenderPipelineResourcePredicate(RenderPipelineResourceProfile profile);

public sealed class RenderPipelineResourceLayout
{
    private readonly IReadOnlyDictionary<string, RenderPipelineResourceSpec> _byName;

    internal RenderPipelineResourceLayout(
        RenderPipelineResourceProfile profile,
        IReadOnlyList<RenderPipelineResourceSpec> orderedSpecs,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName)
    {
        Profile = profile;
        OrderedSpecs = orderedSpecs;
        _byName = byName;
    }

    public static RenderPipelineResourceLayout Empty { get; } = new(
        RenderPipelineResourceProfile.Empty,
        Array.Empty<RenderPipelineResourceSpec>(),
        new ReadOnlyDictionary<string, RenderPipelineResourceSpec>(
            new Dictionary<string, RenderPipelineResourceSpec>(StringComparer.OrdinalIgnoreCase)));

    public RenderPipelineResourceProfile Profile { get; }
    public IReadOnlyList<RenderPipelineResourceSpec> OrderedSpecs { get; }
    public IReadOnlyDictionary<string, RenderPipelineResourceSpec> ResourcesByName => _byName;

    public bool TryGet(string name, [NotNullWhen(true)] out RenderPipelineResourceSpec? spec)
        => _byName.TryGetValue(name, out spec);

    public IEnumerable<TextureResourceDescriptor> LowerTextureDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
        {
            if (spec is TextureSpec textureSpec)
                yield return textureSpec.ToDescriptor();
            else if (spec is TextureViewSpec viewSpec)
                yield return viewSpec.ToDescriptor();
        }
    }

    public IEnumerable<FrameBufferResourceDescriptor> LowerFrameBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is FrameBufferSpec frameBufferSpec)
                yield return frameBufferSpec.ToDescriptor();
    }

    public IEnumerable<RenderBufferResourceDescriptor> LowerRenderBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is RenderBufferSpec renderBufferSpec)
                yield return renderBufferSpec.ToDescriptor();
    }

    public IEnumerable<BufferResourceDescriptor> LowerBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is BufferSpec bufferSpec)
                yield return bufferSpec.ToDescriptor();
    }
}