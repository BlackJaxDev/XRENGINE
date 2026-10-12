using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using XREngine.Rendering.Shaders.Compilation;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRShader : IPostCookedBinaryDeserialize
{
    private ShaderProgramArtifact? _cookedArtifact;
    private string? _cookedArtifactIdentity;

    /// <summary>
    /// Serialized descriptor SHA-256 assigned by the target cook. The runtime
    /// content catalog supplies the exact module; source text never selects it.
    /// </summary>
    [YamlMember(Order = 1000)]
    public string? CookedArtifactIdentity
    {
        get => _cookedArtifactIdentity;
        set
        {
            value = ShaderProgramArtifactCatalog.ValidateIdentity(value);
            if (SetField(ref _cookedArtifactIdentity, value) && _cookedArtifact?.Identity != value)
                SetField(ref _cookedArtifact, null, nameof(CookedArtifact));
        }
    }

    /// <summary>
    /// Optional whole-module companion supplied by the asset loader. Every stage
    /// in a program must identify the same module before it can replace authored stages.
    /// </summary>
    [YamlIgnore]
    public ShaderProgramArtifact? CookedArtifact
    {
        get => _cookedArtifact;
        set
        {
            ShaderProgramArtifactCatalog.ValidateIdentity(value?.Identity);
            SetField(ref _cookedArtifact, value);
            CookedArtifactIdentity = value?.Identity;
        }
    }

    /// <summary>Restores source-change invalidation after cooked hydration suppresses property callbacks.</summary>
    public void OnPostCookedBinaryDeserialize()
    {
        InvalidateResolvedSourceCache();
        if (Source is TextFile source)
        {
            source.TextChanged -= OnSourceTextChanged;
            source.TextChanged += OnSourceTextChanged;
        }
    }

    /// <summary>Resolves only the explicit serialized identity from the supplied content owner.</summary>
    public bool TryGetCookedArtifact(ShaderCompileTarget target, IShaderProgramArtifactResolver? resolver,
        [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        artifact = _cookedArtifact;
        if (artifact is not null && artifact.Identity == _cookedArtifactIdentity && artifact.Target == target)
            return true;
        artifact = null;
        return _cookedArtifactIdentity is not null && resolver is not null && resolver.TryResolve(_cookedArtifactIdentity, target, out artifact);
    }
}
