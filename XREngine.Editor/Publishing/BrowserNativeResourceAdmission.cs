using XREngine.Rendering;

namespace XREngine.Editor.Publishing;

/// <summary>Frozen authored resource metadata; identity is compared only and never treated as a live GPU source.</summary>
internal sealed record BrowserNativeResourceAdmission(string ScenePath, string Path, string Resource,
    object Identity, AdvancedTextureRecord Texture, AdvancedSamplerRecord Sampler, bool Probe,
    string? Reason = null, EAdvancedShadowType? ShadowType = null, int? AuthoredDecalPass = null);
