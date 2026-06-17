namespace XREngine;

internal sealed class RuntimeEditorPreferences
{
    public EViewportPresentationMode ViewportPresentationMode { get; set; } = EViewportPresentationMode.NativeWindow;
    public int ScenePanelResizeDebounceMs { get; set; } = 100;
    public bool HoverOutlineEnabled { get; set; } = true;
    public bool SelectionOutlineEnabled { get; set; } = true;
    public ColorF4 HoverOutlineColor { get; set; } = ColorF4.Yellow;
    public ColorF4 SelectionOutlineColor { get; set; } = ColorF4.White;
    public RuntimeDebugPreferences Debug { get; } = new();
    public RuntimeThemePreferences Theme { get; } = new();

    public enum EViewportPresentationMode
    {
        NativeWindow,
        UseViewportPanel
    }
}
