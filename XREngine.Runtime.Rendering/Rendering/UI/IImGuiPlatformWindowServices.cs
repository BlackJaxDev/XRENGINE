namespace XREngine.Rendering.UI;

/// <summary>Applies native viewport activation, taskbar, and hit-testing behavior by platform handle.</summary>
public interface IImGuiPlatformWindowServices
{
    bool IsInputTransparent(uint flags);
    void ConfigureNativeWindow(nint windowHandle, uint flags);
    void ReleaseNativeWindow(nint windowHandle);
    bool TryShowWithoutActivation(nint windowHandle, uint flags);
}
