namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    private RuntimeEngine.Rendering.EngineSettings? _browserQualitySettingsOwner;
    private BrowserWebGpuQualitySettings? _previousBrowserQuality;
    private BrowserWebGpuQualitySettings? _selectedBrowserQuality;

    private bool HasPendingBrowserQualityRestoration => _browserQualitySettingsOwner is not null;

    private void ApplySelectedBrowserQuality(bool hasCanvas, string? preset)
    {
        if (!hasCanvas)
            return;

        RuntimeEngine.Rendering.EngineSettings owner = RuntimeEngine.Rendering.Settings;
        if (preset is null)
        {
            owner.BrowserWebGpuQuality.Validate();
            return;
        }

        BrowserWebGpuQualitySettings selected = BrowserWebGpuQualitySettings.CreatePreset(preset);
        selected.Validate();
        _browserQualitySettingsOwner = owner;
        _previousBrowserQuality = owner.BrowserWebGpuQuality;
        _selectedBrowserQuality = selected;
        owner.BrowserWebGpuQuality = selected;
    }

    /// <summary>Restores only the exact settings slot installed by this session.</summary>
    private void RestoreSelectedBrowserQuality()
    {
        RuntimeEngine.Rendering.EngineSettings? owner = _browserQualitySettingsOwner;
        if (owner is null)
            return;
        if (ReferenceEquals(RuntimeEngine.Rendering.Settings, owner) &&
            ReferenceEquals(owner.BrowserWebGpuQuality, _selectedBrowserQuality))
            owner.BrowserWebGpuQuality = _previousBrowserQuality!;

        _browserQualitySettingsOwner = null;
        _previousBrowserQuality = null;
        _selectedBrowserQuality = null;
    }

    public string GetCanvasQualitySettingsJson()
        => RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.ToCanvasJson();
}
