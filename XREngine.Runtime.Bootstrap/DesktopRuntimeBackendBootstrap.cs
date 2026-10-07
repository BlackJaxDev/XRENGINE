namespace XREngine.Runtime.Bootstrap;

/// <summary>Registers packaged desktop subsystem providers before shared engine services are accessed.</summary>
internal static class DesktopRuntimeBackendBootstrap
{
    private static readonly Lock Sync = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Sync)
        {
            if (_registered)
                return;
            Runtime.Platform.Desktop.DesktopLoggingBackend.EnsureRegistered();
            Runtime.Platform.Desktop.DesktopWorkerBackend.EnsureRegistered();
            global::XREngine.Audio.NAudioBackend.Register();
            global::XREngine.Audio.OpenALBackend.Register();
            global::XREngine.Audio.SteamAudioBackend.Register();
            Components.OVRLipSyncBackend.Register();
            Components.Audio2Face3DNativeBackend.Register();
            OpenVrRuntimeBackend.Register();
            OpenXrRuntimeBackend.Register();
            Rendering.Meshlets.MeshOptimizerBackend.Register();
            Networking.SocketNetworkBackend.Register();
            Networking.OscNetworkBackend.Register();
            Input.XInputBackend.Register();
            Core.Files.DirectStorageBackend.Register();
            Rendering.DesktopDiagnosticsBackend.Register();
            Runtime.Media.FFmpeg.FfmpegMediaBackend.Register();
            Runtime.Platform.Desktop.DesktopPlatformBackend.Register();
            Runtime.Imaging.Magick.MagickImagingBackend.Register();
            Runtime.Text.FreeType.FreeTypeFontBackend.Register();
            Runtime.UI.Skia.SkiaFontBackend.Register();
            Runtime.UI.Ultralight.UltralightUiBackend.Register();
            _registered = true;
        }
    }
}
