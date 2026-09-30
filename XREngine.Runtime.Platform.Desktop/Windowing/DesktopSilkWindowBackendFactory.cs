using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Data.Vectors;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Maps authored desktop window policy to Silk on the native window owner.</summary>
public sealed class DesktopSilkWindowBackendFactory : IRuntimeWindowBackendFactory
{
    private static readonly object AuxiliaryWindowsSync = new();
    private static readonly List<DesktopSilkWindowBackend> AuxiliaryWindows = [];

    internal static void RegisterAuxiliaryWindow(DesktopSilkWindowBackend window)
    {
        lock (AuxiliaryWindowsSync)
            AuxiliaryWindows.Add(window);
    }

    internal static void UnregisterAuxiliaryWindow(DesktopSilkWindowBackend window)
    {
        lock (AuxiliaryWindowsSync)
            AuxiliaryWindows.Remove(window);
    }

    internal static void PumpAuxiliaryWindows(int ownerThreadId, long parentGeneration)
    {
        lock (AuxiliaryWindowsSync)
        {
            for (int i = 0; i < AuxiliaryWindows.Count; i++)
            {
                DesktopSilkWindowBackend window = AuxiliaryWindows[i];
                if (window.OwnerThreadId == ownerThreadId && window.SharedParentGeneration == parentGeneration)
                    window.PumpEvents();
            }
        }
    }

    public IRuntimeWindowBackend Create(in RuntimeWindowCreateOptions request)
    {
        if (request.SharedContext is not null && request.SharedContext is not DesktopSilkGlContext)
            throw new NotSupportedException("Desktop GL context sharing requires a live context from the installed desktop window backend.");

        if (request.ResizeStrategy == EInteractiveWindowResizeStrategy.SdlBackend)
        {
            Silk.NET.Windowing.Sdl.SdlWindowing.Use();
            Silk.NET.Input.Sdl.SdlInput.Use();
        }
        else
        {
            Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
            Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
        }

        WindowOptions options = WindowOptions.Default;
        if (request.Purpose == RuntimeWindowPurpose.Presentation)
        {
            options.IsEventDriven = true;
            options.FramesPerSecond = 0.0;
            options.UpdatesPerSecond = 0.0;
            options.VideoMode = VideoMode.Default;
            options.Samples = 1;
        }
        options.API = request.GraphicsApi switch
        {
            RuntimeGraphicsApiKind.OpenGL => new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ResolveOpenGlFlags(request),
                new APIVersion(
                    checked((byte)request.OpenGlMajorVersion),
                    checked((byte)request.OpenGlMinorVersion))),
            RuntimeGraphicsApiKind.Vulkan => new GraphicsAPI(
                ContextAPI.Vulkan,
                ContextProfile.Core,
                ContextFlags.ForwardCompatible,
                new APIVersion(1, 1)),
            _ => throw new NotSupportedException($"Desktop window graphics API '{request.GraphicsApi}' is unavailable."),
        };
        options.Position = new Vector2D<int>(request.Position.X, request.Position.Y);
        options.Size = new Vector2D<int>(request.Size.X, request.Size.Y);
        options.Title = request.Startup.Title ?? string.Empty;
        options.WindowState = request.Startup.State == EWindowState.Fullscreen
            ? WindowState.Fullscreen
            : WindowState.Normal;
        options.WindowBorder = request.Startup.State is EWindowState.Borderless or EWindowState.Fullscreen ||
            !request.Startup.UseNativeTitleBar ||
            request.ResizeStrategy == EInteractiveWindowResizeStrategy.EngineBorderlessResize
                ? WindowBorder.Hidden
                : WindowBorder.Resizable;
        options.VSync = request.VSyncEnabled;
        options.TopMost = request.TopMost;
        options.IsVisible = request.Visible;
        options.ShouldSwapAutomatically = request.SwapAutomatically;
        options.TransparentFramebuffer = request.TransparentFramebuffer;
        options.PreferredBitDepth = ResolveChannelBits(request.ColorBits > 0
            ? request.ColorBits
            : request.PreferHdrOutput ? 64 : 24);
        options.PreferredDepthBufferBits = request.DepthBits > 0 ? request.DepthBits : null;
        options.PreferredStencilBufferBits = request.StencilBits > 0 ? request.StencilBits : 8;
        options.SharedContext = (request.SharedContext as DesktopSilkGlContext)?.SilkContext;
        return new DesktopSilkWindowBackend(Window.Create(options), request);
    }

    /// <summary>
    /// Converts a total color depth into Silk's per-channel request. 24- and 32-bit requests both
    /// resolve to 8-bit channels with alpha, the platform default; deeper requests split evenly
    /// across the four channels. The windowing layer treats these as preferences, not requirements.
    /// </summary>
    private static Vector4D<int> ResolveChannelBits(int colorBits)
        => new(colorBits > 32 ? colorBits / 4 : 8);

    private static ContextFlags ResolveOpenGlFlags(in RuntimeWindowCreateOptions request)
    {
        ContextFlags flags = 0;
        if (request.OpenGlDebugContext)
            flags |= ContextFlags.Debug;
        if (request.OpenGlForwardCompatible)
            flags |= ContextFlags.ForwardCompatible;
        return flags;
    }
}
