using OpenVR.NET.Manifest;
using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Bridges application-owned VR startup and transport behavior into Runtime.Rendering.
/// </summary>
internal sealed class EngineRuntimeVrLifecycleServices : IRuntimeVrLifecycleServices
{
    public bool InitializeOpenXR(XRWindow? window)
        => EngineVrLifecycle.InitializeOpenXR(window);

    public Task<bool> InitializeLocal(IActionManifest actionManifest, VrManifest vrManifest, XRWindow window)
        => EngineVrLifecycle.InitializeLocal(actionManifest, vrManifest, window);

    public void InitRenderEmulated(XRWindow window)
        => EngineVrLifecycle.InitRenderEmulated(window);

    public Task<bool> InitializeClient(IActionManifest actionManifest, VrManifest vrManifest)
        => EngineVrLifecycle.IninitializeClient(actionManifest, vrManifest);

    public bool InitializeServer()
        => EngineVrLifecycle.InitializeServer();

    public void StartInputClient()
        => EngineVrLifecycle.StartInputClient();

    public void StopInputServer()
        => EngineVrLifecycle.StopInputServer();

    public Task SendInputs()
        => EngineVrLifecycle.SendInputs();
}
