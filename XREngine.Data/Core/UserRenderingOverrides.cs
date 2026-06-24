using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine;

[Serializable]
[MemoryPackable]
public partial class UserRenderingOverrides : XRBase
{
    private UserCommonRenderingOverrides _common = new();
    private UserQualityRenderingOverrides _quality = new();
    private UserPerformanceRenderingOverrides _performance = new();

    public UserRenderingOverrides()
        => AttachSubSettings(_common, _quality, _performance);

    public UserCommonRenderingOverrides Common
    {
        get => _common;
        set => SetField(ref _common, value ?? new UserCommonRenderingOverrides());
    }

    public UserQualityRenderingOverrides Quality
    {
        get => _quality;
        set => SetField(ref _quality, value ?? new UserQualityRenderingOverrides());
    }

    public UserPerformanceRenderingOverrides Performance
    {
        get => _performance;
        set => SetField(ref _performance, value ?? new UserPerformanceRenderingOverrides());
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(Common) || propName == nameof(Quality) || propName == nameof(Performance))
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
    }

    private void AttachSubSettings(params IXRNotifyPropertyChanged?[] settings)
    {
        for (int i = 0; i < settings.Length; i++)
        {
            if (settings[i] is not null)
                settings[i]!.PropertyChanged += HandleSubSettingsChanged;
        }
    }

    private static void RefreshSubSettings<T>(T previous, T current, XRPropertyChangedEventHandler handler)
    {
        if (previous is IXRNotifyPropertyChanged previousNotify)
            previousNotify.PropertyChanged -= handler;

        if (current is IXRNotifyPropertyChanged currentNotify)
            currentNotify.PropertyChanged += handler;
    }

    private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        => OnPropertyChanged(e.PropertyName, e.PreviousValue, e.NewValue);
}

[Serializable]
[MemoryPackable]
public partial class UserCommonRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<RenderBackendFallbackPolicy> _renderBackendFallbackPolicyOverride = new();

    [Category("Rendering Overrides")]
    [Description("User override for render backend fallback behavior during startup.")]
    public OverrideableSetting<RenderBackendFallbackPolicy> RenderBackendFallbackPolicyOverride
    {
        get => _renderBackendFallbackPolicyOverride;
        set => SetField(ref _renderBackendFallbackPolicyOverride, value ?? new());
    }
}

[Serializable]
[MemoryPackable]
public partial class UserQualityRenderingOverrides : OverrideableSettingsOwnerBase
{
}

[Serializable]
[MemoryPackable]
public partial class UserPerformanceRenderingOverrides : OverrideableSettingsOwnerBase
{
}
