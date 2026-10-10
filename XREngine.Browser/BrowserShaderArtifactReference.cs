namespace XREngine.Browser;

/// <summary>Content-owned paths for one exact cooked whole-program shader module.</summary>
internal readonly record struct BrowserShaderArtifactReference(string Identity, string Descriptor, string Source);
