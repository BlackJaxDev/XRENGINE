namespace XREngine.Rendering;

/// <summary>Retains builder-owned proof of a generated shader and its canonical inputs.</summary>
internal sealed record UberShaderVariantProvenance(
    UberShaderSourceStamp Canonical, UberShaderSourceStamp Variant,
    ShaderSourceFileDependency[] Dependencies, ShaderSourceDirectoryDependency[] DirectoryDependencies,
    ShaderSourceProviderOwner Owner, long SnippetVersion)
{
    internal bool IsCurrent => Canonical.IsCurrent && Variant.IsCurrent &&
        SnippetVersion == ShaderSourceResolver.RegisteredSnippetVersion &&
        ShaderSourceResolver.AreDependenciesCurrent(Dependencies, DirectoryDependencies, Owner) &&
        Canonical.IsCurrent && Variant.IsCurrent;
}
