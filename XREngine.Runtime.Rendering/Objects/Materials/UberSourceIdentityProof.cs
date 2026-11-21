namespace XREngine.Rendering;

/// <summary>Retains a canonical Uber shader that produced a valid source manifest.</summary>
internal sealed record UberSourceIdentityProof(
    UberShaderSourceStamp Canonical,
    ShaderSourceProviderOwner Owner,
    long SnippetVersion);
