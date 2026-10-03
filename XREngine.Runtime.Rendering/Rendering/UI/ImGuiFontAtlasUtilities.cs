namespace XREngine.Rendering.UI;

/// <summary>Routes font-atlas setup to the host's native Dear ImGui capability.</summary>
internal static class ImGuiFontAtlasUtilities
{
    public static bool TryUseDefaultEditorFont(nint io, float sizePixels = 18.0f, bool forceReload = false)
        => ImGuiRuntimeServices.Required.TryUseDefaultEditorFont(io, sizePixels, forceReload);

    public static void MarkContextDestroyed(nint context)
        => ImGuiRuntimeServices.Current?.MarkContextDestroyed(context);
}
