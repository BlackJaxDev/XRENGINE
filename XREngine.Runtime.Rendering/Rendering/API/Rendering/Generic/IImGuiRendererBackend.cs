namespace XREngine.Rendering;

/// <summary>
/// Backend contract used by the renderer-neutral ImGui frame orchestration.
/// </summary>
public interface IImGuiRendererBackend
{
    void MakeCurrent();
    void Update(float deltaSeconds);
    void Render();
    void UpdatePlatformWindows(bool deferGpuLifecycle);
    void RenderPlatformWindows();
}
