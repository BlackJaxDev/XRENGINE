using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private EngineMaterialSemanticIdentity _engineSemantic;
    private UberMaterialShaderTransition? _pendingUberShaderTransition;
    private XRShader? _subscribedUberCanonicalShader;

    private bool ConsumeUberShaderTransition(object? previous, object? replacement)
    {
        UberMaterialShaderTransition? transition = _pendingUberShaderTransition;
        SetField(ref _pendingUberShaderTransition, null, publishNotifications: false, nameof(Shaders));
        return transition is not null && transition.Matches(this, previous, replacement);
    }

    private void ReconcileUberCanonicalSubscription()
    {
        XRShader? canonical = null;
        XRShader? generated = null;
        foreach (XRShader shader in Shaders)
            if (UberShaderVariantBuilder.TryGetCanonicalShader(shader, out XRShader? source))
            {
                canonical = source;
                generated = shader;
                break;
            }
        SetUberCanonicalSubscription(canonical);
        // Check again after attaching, so a source edit during the handoff cannot escape.
        if (HasEngineSemantic && generated is not null && !UberShaderVariantBuilder.HasCurrentProvenance(generated))
            InvalidateEngineSemantic();
    }

    private void SetUberCanonicalSubscription(XRShader? canonical)
    {
        if (ReferenceEquals(_subscribedUberCanonicalShader, canonical))
            return;
        if (_subscribedUberCanonicalShader is { } previous)
        {
            previous.AuthoredSourceChanged -= AuthoredShaderSourceChanged;
            previous.Reloaded -= ShaderReloaded;
        }
        SetField(ref _subscribedUberCanonicalShader, canonical, publishNotifications: false, nameof(Shaders));
        if (canonical is not null)
        {
            canonical.AuthoredSourceChanged += AuthoredShaderSourceChanged;
            canonical.Reloaded += ShaderReloaded;
        }
    }

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
