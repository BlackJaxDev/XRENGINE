using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One backend-neutral material texture slot. Texture and sampler identity remain
/// independently generation checked until the selected backend encoding is built.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedMaterialTextureBinding(
    AdvancedTextureReference Texture,
    AdvancedSamplerReference Sampler);
