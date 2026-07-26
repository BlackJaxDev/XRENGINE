using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine;

/// <summary>
/// Defines project overrides shared by all rendering backends.
/// </summary>
[Serializable]
[MemoryPackable]
public partial class GameCommonRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<RenderBackendFallbackPolicy> _renderBackendFallbackPolicyOverride = new();

    [Category("Rendering Overrides")]
    [Description("Project override for render backend fallback behavior during startup.")]
    public OverrideableSetting<RenderBackendFallbackPolicy> RenderBackendFallbackPolicyOverride
    {
        get => _renderBackendFallbackPolicyOverride;
        set => SetField(ref _renderBackendFallbackPolicyOverride, value ?? new());
    }
}
