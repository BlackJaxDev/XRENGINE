using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Resolves a declared material variant from the same resident cooked catalog as its shader artifact.</summary>
public interface IEngineMaterialVariantResolver
{
    bool TryResolve(EngineMaterialVariantKey key, [NotNullWhen(true)] out ShaderProgramArtifact? artifact);
}
