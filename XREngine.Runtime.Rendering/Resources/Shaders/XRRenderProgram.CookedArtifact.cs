using System.Diagnostics.CodeAnalysis;
using XREngine.Rendering.Shaders.Compilation;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRRenderProgram
{
    private ShaderProgramArtifact? _cookedArtifact;
    private string? _cookedArtifactIdentity;

    /// <summary>A serialized exact module identity, resolved only through the owning content catalog.</summary>
    [YamlMember(Order = 1000)]
    public string? CookedArtifactIdentity
    {
        get => _cookedArtifactIdentity;
        set
        {
            value = ShaderProgramArtifactCatalog.ValidateIdentity(value);
            if (SetField(ref _cookedArtifactIdentity, value))
            {
                _shaderInterfaceDirty = true;
                if (_cookedArtifact?.Identity != value)
                    SetField(ref _cookedArtifact, null, nameof(CookedArtifact));
            }
        }
    }

    /// <summary>An explicit whole-program cooked override installed by the engine asset loader.</summary>
    [YamlIgnore]
    public ShaderProgramArtifact? CookedArtifact
    {
        get => _cookedArtifact;
        set
        {
            ShaderProgramArtifactCatalog.ValidateIdentity(value?.Identity);
            if (SetField(ref _cookedArtifact, value))
                _shaderInterfaceDirty = true;
            CookedArtifactIdentity = value?.Identity;
        }
    }

    /// <summary>Resolves already-attached companions without a content catalog.</summary>
    public bool TryGetCookedArtifact(ShaderCompileTarget target, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
        => TryGetCookedArtifact(target, null, out artifact);

    /// <summary>
    /// Resolves a target-specific module without guessing a replacement for custom
    /// stages. Stage companions must agree on the entire descriptor identity.
    /// </summary>
    public bool TryGetCookedArtifact(ShaderCompileTarget target, IShaderProgramArtifactResolver? resolver,
        [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        artifact = _cookedArtifact;
        if (artifact is not null && artifact.Identity == _cookedArtifactIdentity && artifact.Target == target)
            return true;
        artifact = null;
        // An explicit program reference is authoritative. A missing module must
        // not accidentally fall through to a different set of stage companions.
        if (_cookedArtifactIdentity is not null)
            return resolver is not null && resolver.TryResolve(_cookedArtifactIdentity, target, out artifact);
        if (Shaders.Count == 0 || !Shaders[0].TryGetCookedArtifact(target, resolver, out ShaderProgramArtifact? candidate))
            return false;
        for (int index = 1; index < Shaders.Count; index++)
            if (!Shaders[index].TryGetCookedArtifact(target, resolver, out ShaderProgramArtifact? stage) || stage.Identity != candidate.Identity)
                return false;
        artifact = candidate;
        return true;
    }
}
