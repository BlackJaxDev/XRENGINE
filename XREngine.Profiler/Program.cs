using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace XREngine.Profiler;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        int port = XREngine.Data.Profiling.ProfilerProtocol.DefaultPort;

        // Allow overriding the listen port via command-line or environment.
        if (args.Length > 0 && int.TryParse(args[0], out int argPort))
            port = argPort;
        else if (Environment.GetEnvironmentVariable(Data.Profiling.ProfilerProtocol.PortEnvVar) is string envPort
                 && int.TryParse(envPort, out int envParsed))
            port = envParsed;

        // Create the standalone Silk.NET window (GLFW, OpenGL 3.3 core).
        Window.PrioritizeGlfw();

        var options = WindowOptions.Default with
        {
            Title = "XREngine Profiler",
            Size = new Vector2D<int>(1440, 900),
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = true,
            ShouldSwapAutomatically = true,
        };

        using var window = Window.Create(options);

        ProfilerImGuiApp? app = null;

        window.Load += () =>
        {
            app = new ProfilerImGuiApp(window, port);
        };

        window.Update += dt =>
        {
            app?.Update((float)dt);
        };

        window.Render += dt =>
        {
            app?.Render((float)dt);
        };

        window.Closing += () =>
        {
            app?.Dispose();
            app = null;
        };

        window.Run();
    }
}
