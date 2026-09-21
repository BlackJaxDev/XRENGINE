using System.ComponentModel;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public abstract partial class XRTexture
{
    private bool _isGpuWritable;

    /// <summary>
    /// Whether this texture has been used as a framebuffer or writable image target.
    /// Derived caches must refresh from its GPU image even when CPU upload data is retained.
    /// </summary>
    [MemoryPackIgnore, YamlIgnore, Browsable(false)]
    public bool IsGpuWritable => _isGpuWritable;

    internal void MarkGpuWritable()
    {
        if (!_isGpuWritable)
            SetField(ref _isGpuWritable, true, nameof(IsGpuWritable));
    }
}
