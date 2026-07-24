using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine;

/// <summary>
/// Defines project overrides for the OpenGL backend.
/// </summary>
[Serializable]
[MemoryPackable]
public partial class GameOpenGLRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<bool> _allowProgramPipelinesOverride = new();
    private OverrideableSetting<bool> _useDetailPreservingComputeMipmapsOverride = new();

    [Category("OpenGL Overrides")]
    [Description("Project override for OpenGL program pipelines.")]
    public OverrideableSetting<bool> AllowProgramPipelinesOverride
    {
        get => _allowProgramPipelinesOverride;
        set => SetField(ref _allowProgramPipelinesOverride, value ?? new());
    }

    [Category("OpenGL Overrides")]
    [Description("Project override for detail-preserving OpenGL compute mipmap generation.")]
    public OverrideableSetting<bool> UseDetailPreservingComputeMipmapsOverride
    {
        get => _useDetailPreservingComputeMipmapsOverride;
        set => SetField(ref _useDetailPreservingComputeMipmapsOverride, value ?? new());
    }
}
