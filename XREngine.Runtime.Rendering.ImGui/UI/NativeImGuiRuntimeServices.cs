using ImGuiNET;
using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Implements context and font operations using the native Dear ImGui binding.</summary>
internal sealed class NativeImGuiRuntimeServices : IRuntimeImGuiServices
{
    public nint CurrentContext
    {
        get => ImGui.GetCurrentContext();
        set => ImGui.SetCurrentContext(value);
    }

    public void ConfigureDisplay(Vector2 size, Vector2 framebufferScale)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = size;
        io.DisplayFramebufferScale = framebufferScale;
    }

    public void EndFrame() => ImGui.EndFrame();

    public unsafe bool TryUseDefaultEditorFont(nint io, float sizePixels, bool forceReload)
        => NativeImGuiFontAtlas.TryUseDefaultEditorFont(new ImGuiIOPtr((ImGuiIO*)io), sizePixels, forceReload);

    public void MarkContextDestroyed(nint context) => NativeImGuiFontAtlas.MarkContextDestroyed(context);
}
