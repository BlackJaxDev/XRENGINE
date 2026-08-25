namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Exposes the narrow OpenXR lifecycle surface driven by an application composition root.
/// </summary>
public interface IOpenXrApplicationLifecycle
{
    void EnableRuntimeMonitoring();
    void UpdateRuntimeState();
    void CollectVisible();
    void SwapBuffers();
    void Render();
    void PostRender();
}
