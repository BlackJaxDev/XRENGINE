namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Cook-time association of a complete semantic variant key with one exact descriptor hash.</summary>
public sealed record EngineMaterialVariantEntry(EngineMaterialVariantKey Key, string DescriptorIdentity);
