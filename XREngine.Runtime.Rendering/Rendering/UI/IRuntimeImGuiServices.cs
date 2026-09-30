using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>
/// Provides native Dear ImGui context and font operations to renderer-neutral orchestration.
/// The host installs this capability only when it uses Dear ImGui.
/// </summary>
public interface IRuntimeImGuiServices
{
    nint CurrentContext { get; set; }
    void ConfigureDisplay(Vector2 size, Vector2 framebufferScale);
    void EndFrame();
    bool TryUseDefaultEditorFont(nint io, float sizePixels, bool forceReload);
    void MarkContextDestroyed(nint context);
}
