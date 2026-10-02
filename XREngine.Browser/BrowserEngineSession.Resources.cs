namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    /// <summary>Supplies the canvas output's AA default without replacing an authored selection.</summary>
    private static void ConfigureCanvasResourceProfileDefaults(GameStartupSettings settings)
    {
        // Shared engine defaults target desktop output. The cooked canvas pipeline
        // owns a single-sample, AA-free profile; an absent override must select that
        // profile before viewport resize, collection, and rendering capture their keys.
        if (!settings.AntiAliasingModeOverride.HasOverride &&
            !settings.DefaultUserSettings.AntiAliasingModeOverride.HasOverride)
        {
            settings.AntiAliasingModeOverride.SetOverride(EAntiAliasingMode.None);
        }
    }
}
