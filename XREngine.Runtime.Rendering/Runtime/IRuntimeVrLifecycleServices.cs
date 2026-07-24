using OpenVR.NET.Manifest;
using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Application-owned VR runtime startup and transport operations used by rendering.
/// </summary>
public interface IRuntimeVrLifecycleServices
{
    bool InitializeOpenXR(XRWindow? window);
    Task<bool> InitializeLocal(IActionManifest actionManifest, VrManifest vrManifest, XRWindow window);
    void InitRenderEmulated(XRWindow window);
    Task<bool> InitializeClient(IActionManifest actionManifest, VrManifest vrManifest);
    bool InitializeServer();
    void StartInputClient();
    void StopInputServer();
    Task SendInputs();
}
