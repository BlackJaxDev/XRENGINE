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

    public Task<bool> InitializeLocal(object actionManifest, object vrManifest, XRWindow window)
        => actionManifest is IActionManifest typedActionManifest && vrManifest is VrManifest typedVrManifest
            ? EngineVrLifecycle.InitializeLocal(typedActionManifest, typedVrManifest, window)
            : Task.FromResult(false);

    public void InitRenderEmulated(XRWindow window)
        => EngineVrLifecycle.InitRenderEmulated(window);

    public Task<bool> InitializeClient(object actionManifest, object vrManifest)
        => actionManifest is IActionManifest typedActionManifest && vrManifest is VrManifest typedVrManifest
            ? EngineVrLifecycle.IninitializeClient(typedActionManifest, typedVrManifest)
            : Task.FromResult(false);

    public bool InitializeServer()
        => EngineVrLifecycle.InitializeServer();

    public void StartInputClient()
        => EngineVrLifecycle.StartInputClient();

    public void StopInputServer()
        => EngineVrLifecycle.StopInputServer();

    public Task SendInputs()
        => EngineVrLifecycle.SendInputs();
}
