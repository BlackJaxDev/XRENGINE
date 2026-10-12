using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>A runtime content owner's exact, already-loaded shader descriptor identities.</summary>
public interface IShaderProgramArtifactResolver
{
    bool TryResolve(string identity, ShaderCompileTarget target, [NotNullWhen(true)] out ShaderProgramArtifact? artifact);
}
