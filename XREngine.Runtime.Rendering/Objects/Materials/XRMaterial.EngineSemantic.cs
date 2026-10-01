using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private EngineMaterialSemanticIdentity _engineSemantic;

    /// <summary>
    /// Explicit authored behavior for engine-cooked variants. Ordinary materials remain
    /// unmarked; changing authored shader stages or source clears this eligibility.
    /// </summary>
    [YamlMember(Order = 1001)]
    public EngineMaterialSemanticIdentity EngineSemantic
    {
        get => _engineSemantic;
        set
        {
            value.Validate();
            if (SetField(ref _engineSemantic, value))
                BumpShaderStateRevision();
        }
    }

    [Browsable(false)]
    [YamlIgnore]
    public bool HasEngineSemantic => _engineSemantic.Semantic != EngineMaterialSemantic.None;

    private void InvalidateEngineSemantic()
    {
        if (HasEngineSemantic)
            EngineSemantic = EngineMaterialSemanticIdentity.None;
    }

    private void AuthoredShaderSourceChanged(XRShader shader)
        => InvalidateEngineSemantic();
}
