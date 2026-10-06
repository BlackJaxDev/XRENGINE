using XREngine.Input;
using XREngine.Native;
using XREngine.Execution;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop.Windowing;
using XREngine.Networking;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Installs desktop input and operating-system services during application composition.</summary>
public static class DesktopPlatformBackend
{
    /// <summary>Returns the primary display extent used by borderless desktop windows.</summary>
    public static IVector2 GetPrimaryDisplaySize()
        => new(NativeMethods.GetSystemMetrics(0), NativeMethods.GetSystemMetrics(1));

    public static void Register()
    {
        RuntimeWindowBackendRegistry.Install(new DesktopSilkWindowBackendFactory());
        InputPlatformServices.ReadCapsLockState = static () =>
            NativeMethods.TryDetermineSystemCapsLockState(out bool enabled) ? enabled : null;
        RenderWorkerPlatformServices.HighPriorityInitializer = WindowsThreadQos.ApplyHighRenderPriority;
        XREngine.Data.FileMappingServices.Backend = new DesktopFileMappingBackend();
        XREngine.Data.HostAssetFileOutputServices.Current = new DesktopHostAssetFileOutput();
        XREngine.Data.RuntimePlatformPaths.Current = new DesktopPlatformPaths();
        XREngine.Data.RuntimeProcessServices.Current = new DesktopProcessRunner();
        XREngine.Data.RuntimeProcessMemoryServices.WorkingSetBytesReader = DesktopProcessDiagnostics.ReadWorkingSetBytes;
        XREngine.Data.RuntimeAssemblyLoadingServices.Current = new DesktopRuntimeAssemblyLoader();
        XREngine.Core.Files.AssetFileSystemServices.Current = new DesktopAssetFileSystem();
        ShaderSourceFileBackendServices.Current = new DesktopShaderSourceFileBackend();
        HostFileTransferServices.Current = new DesktopHostFileTransferBackend();
        XREngine.Rendering.RuntimeClipboardServices.Current = new DesktopClipboardServices();
        XREngine.Rendering.RuntimeDiagnosticCaptureFileOutput.Current = new DesktopDiagnosticCaptureFileOutput();
        RendererNativeCallbackBridge.EntryPoints = new Rendering.DesktopRendererNativeCallbackEntryPoints();
        RendererImGuiViewportCallbackBridge.EntryPoints = new Rendering.DesktopImGuiViewportEntryPoints();
    }
}
