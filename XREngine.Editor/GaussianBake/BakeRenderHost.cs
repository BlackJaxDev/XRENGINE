using XREngine.Native;
using XREngine.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Scene;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Runs the engine with one hidden OpenGL window for a gaussian preview bake. The window gives the graphics context;
/// all captures render offscreen, so the window is never shown and has no taskbar entry.
/// </summary>
public static class BakeRenderHost
{
    /// <summary>The title of the hidden bake window.</summary>
    public const string WindowTitle = "XRE Gaussian Bake";

    /// <summary>The name of the empty world that the window renders until the bake world is ready.</summary>
    public const string IdleWorldName = "GaussianBakeIdle";

    private const int WindowWidth = 320;
    private const int WindowHeight = 180;
    private const int UnexpectedExitCode = 1;

    /// <summary>
    /// Starts the engine, runs <paramref name="body"/> after the window is created, then shuts the engine down.
    /// </summary>
    /// <param name="options">The bake options.</param>
    /// <param name="stage">The root node of the preview stage.</param>
    /// <param name="body">The bake work. It returns the process exit code.</param>
    /// <returns>The exit code of <paramref name="body"/>, or the exit code of the failure that stopped it.</returns>
    public static int Run(
        BakeSplatPreviewOptions options,
        SceneNode stage,
        Func<IBakeRenderServices, CancellationToken, Task<int>> body)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(body);

        EditorBakeRenderServices services = new(stage);
        using CancellationTokenSource cancellation = new();
        int exitCode = UnexpectedExitCode;

        void AfterWindowsCreated(GameStartupSettings settings, GameState state)
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;
            bool? visible = ReadWindowVisibility();
            Log($"Bake window visible: {(visible.HasValue ? visible.Value.ToString() : "unknown (no native window handle)")}");

            if (!AllWindowsUseOpenGl())
            {
                LogError($"Gaussian preview bake failed ({GaussianBakeFailureCodes.CaptureFailed}): the bake window did not get an OpenGL context.");
                int failure = GaussianBakeFailureCodes.ExitCodeFor(GaussianBakeFailureCodes.CaptureFailed);
                Volatile.Write(ref exitCode, failure);
                Environment.ExitCode = failure;

                // This callback runs on the window thread, so the windows can close now.
                Engine.ShutDown();
                return;
            }

            // A window ticks and runs render-thread work only while it has a world. The empty world keeps the
            // window ticking until the bake world replaces it.
            XRWorld idleWorld = new(IdleWorldName, new XRScene(IdleWorldName));
            IRuntimeRenderWorld idleRenderWorld = Engine.GetOrCreateRenderWorld(idleWorld);
            for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
                RuntimeEngine.Windows[i].SetWorld(idleRenderWorld);

            _ = Task.Run(async () =>
            {
                int result = await RunBodyAsync(body, services, cancellation.Token).ConfigureAwait(false);
                Volatile.Write(ref exitCode, result);
                Environment.ExitCode = result;
                RuntimeEngine.EnqueueRenderThreadTask(Engine.ShutDown, "GaussianBake.ShutDown", RenderThreadJobKind.RequiresGraphicsContext);
            });
        }

        Log($"Gaussian preview bake: kind={options.Kind}, input='{options.InputPak}', asset='{options.AssetPath}', output='{options.OutputDirectory}'.");

        // Saved user settings can select another backend. A session value wins over them for this process only,
        // and the capture readback expects the OpenGL row order.
        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.PreferredRenderBackend, ERenderLibrary.OpenGL);
        Engine.AfterCreateWindows += AfterWindowsCreated;
        try
        {
            Engine.Run(CreateStartupSettings(), new GameState(), beginPlayingAllWorlds: false);
        }
        finally
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;
            cancellation.Cancel();
        }

        int finalExitCode = Volatile.Read(ref exitCode);
        Environment.ExitCode = finalExitCode;
        return finalExitCode;
    }

    private static async Task<int> RunBodyAsync(
        Func<IBakeRenderServices, CancellationToken, Task<int>> body,
        IBakeRenderServices services,
        CancellationToken cancellationToken)
    {
        try
        {
            return await body(services, cancellationToken).ConfigureAwait(false);
        }
        catch (GaussianBakeException ex)
        {
            LogError($"Gaussian preview bake failed ({ex.FailureCode}): {ex.Message}");
            return GaussianBakeFailureCodes.ExitCodeFor(ex.FailureCode);
        }
        catch (OperationCanceledException)
        {
            LogError("Gaussian preview bake stopped because the engine shut down.");
            return UnexpectedExitCode;
        }
        catch (Exception ex)
        {
            LogError($"Gaussian preview bake failed with an unexpected error: {ex}");
            return UnexpectedExitCode;
        }
    }

    private static GameStartupSettings CreateStartupSettings()
        => new()
        {
            StartupWindows =
            [
                new GameWindowStartupSettings
                {
                    WindowTitle = WindowTitle,
                    WindowState = EWindowState.Windowed,
                    Width = WindowWidth,
                    Height = WindowHeight,
                    Visible = false,
                },
            ],
            DefaultUserSettings = new UserSettings
            {
                PreferredRenderBackend = ERenderLibrary.OpenGL,
                VSync = EVSyncMode.Off,
            },
            NetworkingType = ENetworkingType.Local,
        };

    /// <summary>
    /// Reads the visibility of the windows from the operating system, through the native handle of each window backend.
    /// Returns true when any window is visible, false when all are hidden, and null when no window has a native handle.
    /// </summary>
    private static bool? ReadWindowVisibility()
    {
        bool? visible = null;
        for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
        {
            IRuntimeWindowBackend? backend = RuntimeEngine.Windows[i].DesktopWindowBackend;
            if (backend is null || backend.OperatingSystemWindowHandle == 0)
                continue;

            visible = (visible ?? false) || NativeMethods.IsWindowVisible(backend.OperatingSystemWindowHandle);
        }

        return visible;
    }

    private static bool AllWindowsUseOpenGl()
    {
        if (RuntimeEngine.Windows.Count == 0)
            return false;

        for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
            if (RuntimeEngine.Windows[i].DesktopWindowBackend?.GraphicsApi != RuntimeGraphicsApiKind.OpenGL)
                return false;

        return true;
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        Debug.Out(message);
    }

    private static void LogError(string message)
    {
        Console.Error.WriteLine(message);
        Debug.LogWarning(message);
    }
}
