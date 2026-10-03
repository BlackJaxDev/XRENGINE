using ImGuiNET;

namespace XREngine.Rendering.UI;

/// <summary>Font-atlas coordination for the OpenGL editor context.</summary>
internal static class ImGuiControllerUtilities
{
    public static unsafe bool TryUseDefaultEditorFont(ImGuiIOPtr io, float sizePixels = 18.0f)
        => ImGuiFontAtlasUtilities.TryUseDefaultEditorFont((nint)io.NativePtr, sizePixels);

    public static void MarkContextDestroyed(nint context)
        => ImGuiFontAtlasUtilities.MarkContextDestroyed(context);
}
