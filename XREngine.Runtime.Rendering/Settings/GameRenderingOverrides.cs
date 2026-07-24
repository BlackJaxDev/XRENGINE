using MemoryPack;
using XREngine.Data.Core;

namespace XREngine;

/// <summary>
/// Groups project-level rendering overrides by backend and responsibility.
/// </summary>
[Serializable]
[MemoryPackable]
public partial class GameRenderingOverrides : XRBase
{
    private GameCommonRenderingOverrides _common = new();
    private GameOpenGLRenderingOverrides _openGL = new();
    private GameVulkanRenderingOverrides _vulkan = new();
    private GameQualityRenderingOverrides _quality = new();
    private GameTechnicalRenderingOverrides _technical = new();

    public GameRenderingOverrides()
        => AttachSubSettings(_common, _openGL, _vulkan, _quality, _technical);

    public GameCommonRenderingOverrides Common
    {
        get => _common;
        set => SetField(ref _common, value ?? new GameCommonRenderingOverrides());
    }

    public GameOpenGLRenderingOverrides OpenGL
    {
        get => _openGL;
        set => SetField(ref _openGL, value ?? new GameOpenGLRenderingOverrides());
    }

    public GameVulkanRenderingOverrides Vulkan
    {
        get => _vulkan;
        set => SetField(ref _vulkan, value ?? new GameVulkanRenderingOverrides());
    }

    public GameQualityRenderingOverrides Quality
    {
        get => _quality;
        set => SetField(ref _quality, value ?? new GameQualityRenderingOverrides());
    }

    public GameTechnicalRenderingOverrides Technical
    {
        get => _technical;
        set => SetField(ref _technical, value ?? new GameTechnicalRenderingOverrides());
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(Common)
            || propName == nameof(OpenGL)
            || propName == nameof(Vulkan)
            || propName == nameof(Quality)
            || propName == nameof(Technical))
        {
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
        }
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
