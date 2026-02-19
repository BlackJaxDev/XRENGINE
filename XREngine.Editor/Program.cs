using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Diagnostics;
using EngineDebug = XREngine.Debug;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using XREngine;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Editor.Mcp;
using XREngine.Native;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Engine;
using static XREngine.Rendering.XRWorldInstance;

internal class Program
{
    private const string UnitTestingWorldSettingsFileName = "UnitTestingWorldSettings.json";
    private static readonly object s_startupTimerLock = new();
    private static Stopwatch? s_startupStopwatch;
    private static XRWindow? s_startupWindow;
    private static int s_startupWindowHooked;
    private static int s_captureInFlight;
    private static int s_startupTimerStopped;

    /// <summary>
    /// Determines which world to load on startup.
    /// </summary>
    public enum EWorldMode
    {
        /// <summary>
        /// Load a default empty world (default behavior).
        /// </summary>
        Default,
        /// <summary>
        /// Load the unit testing world using the JSON config file.
        /// </summary>
        UnitTesting,
    }

    /// <summary>
    /// This project serves as a hardcoded game client for development purposes.
    /// This editor will autogenerate the client exe csproj to compile production games.
    /// </summary>
    /// <param name="args"></param>
    [STAThread]
    private static void Main(string[] args)
    {
        //Begin tracking how long editor startup takes, and log the time when the first non-black frame is rendered.
        StartEditorStartupTimer();

        // Ensure support for legacy code pages needed by some third-party libraries (e.g., SharpFont).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Initialize logging as early as possible to capture output from all subsystems, including during project initialization.
        InitLogging();

        // Check for project initialization arguments and handle them if present. 
        // Returns false for failed initialization (e.g., missing or invalid parameters), 
        // in which case an error message will have been printed and the process should exit.
        if (!TryInitNewProject(args))
            return;

        EngineDebug.Out("XREngine Editor starting...");

        // Start undo/redo system
        Undo.Initialize();

        // Start file drop handler to support drag-and-drop importing of assets and scenes into the editor
        EditorFileDropHandler.Initialize();

        // Override render info constructors to inject editor-specific extra features.
        RenderInfo2D.ConstructorOverride = RenderInfo2DConstructor;
        RenderInfo3D.ConstructorOverride = RenderInfo3DConstructor;

        // Disable automatic code recomp for now
        CodeManager.Instance.CompileOnChange = false;

        // Set default JSON serialization settings for the editor, which are used for things like editor preferences and unit testing world config.
        JsonConvert.DefaultSettings = DefaultJsonSettings;

        // Start play mode controller
        EditorPlayModeController.Initialize();

        // Start vr pawn switcher to allow switching between desktop and VR pawns in the editor without restarting
        EditorOpenXrPawnSwitcher.Initialize();

        // Load unit testing settings from JSON file into static toggles object that can be referenced throughout the editor and unit testing worlds.
        EditorUnitTests.Toggles = LoadUnitTestingSettings(false);

        // Retrieve the world, startup settings, and last game state to run the engine
        InitializeEditor(
            out VRGameStartupSettings<EVRActionCategory, EVRGameAction> startupSettings,
            out GameState gameState);

        void BeforeWindowsCreated(GameStartupSettings settings, GameState __)
        {
            // Unsubscribe self to ensure this only runs once, before the first window creation during engine startup
            Engine.BeforeCreateWindows -= BeforeWindowsCreated;

            // Unit test initialization that must run after editor preferences are loaded 
            // but before windows are created (e.g., that may affect render pipeline selection).
            UnitTest_Init();
            
            // Initialize MCP server after last project or sandbox editor preferences are loaded
            McpServerHost.Initialize(args);

            // Assign the target world AFTER all settings have been applied and BEFORE windows are created, 
            // so that the render pipeline and other systems can be properly initialized.
            settings.StartupWindows[0].TargetWorld = GetTargetWorld(ResolveWorldMode(args));
        }
        Engine.BeforeCreateWindows += BeforeWindowsCreated;
        try
        {
            Engine.Run(startupSettings, gameState);
        }
        finally
        {
            McpServerHost.Shutdown();
        }
    }

    private static XRWorld GetTargetWorld(EWorldMode mode)
    {
        XRWorld targetWorld;
        if (mode == EWorldMode.UnitTesting)
        {
            targetWorld = EditorUnitTests.CreateSelectedWorld(true, false);
            EngineDebug.Out("Loading Unit Testing World...");
        }
        else
        {
            targetWorld = CreateDefaultEmptyWorld();
            EngineDebug.Out("Loading Default Empty World...");
        }
        return targetWorld;
    }

    private static void InitLogging()
    {
        // Install global trace listener to capture System.Diagnostics.Debug output from external libraries
        // and route it through the engine's logging system (console panel + log files)
        static void PrintTraceToGeneralLog(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                EngineDebug.Log(ELogCategory.General, message.TrimEnd('\r', '\n'));
        }
        XREngine.TraceListener.GlobalMessageCallback = PrintTraceToGeneralLog;
        XREngine.TraceListener.InstallGlobalListener();
        //ConsoleHelper.EnsureConsoleAttached();
    }

    private static void InitializeEditor(
        out VRGameStartupSettings<EVRActionCategory, EVRGameAction> startupSettings,
        out GameState gameState)
    {
        startupSettings = CreateEditorStartupSettings();
        gameState = Engine.LoadOrGenerateGameState();
    }

    private static void UnitTest_Init()
    {
        UnitTest_VerifyPlayModeStart();

        Engine.EditorPreferences.Debug.UseDebugOpaquePipeline = ResolveDebugOpaquePipelineSetting();
        EngineDebug.Out($"[DebugPipeline] Re-applied before window creation: {Engine.EditorPreferences.Debug.UseDebugOpaquePipeline}");

        GPURenderPassCollection.ConfigureIndirectDebug(opts =>
        {
            //opts.DisableCountDrawPath = true;
            //opts.SkipIndirectTailClear = true;
            opts.LogCountBufferWrites = true;
            opts.ValidateBufferLayouts = true;
            opts.ValidateLiveHandles = true;
            opts.ForceParameterRemap = true;
            opts.DumpIndirectArguments = true;
            opts.SkipIndirectTailClear = false;
            opts.DisableCountDrawPath = false;
        });
    }

    private static void UnitTest_VerifyPlayModeStart()
    {
        if (!EditorUnitTests.Toggles.StartInPlayModeWithoutTransitions)
            return;
        
        Engine.PlayMode.ForcePlayWithoutTransitions = true;
        static void StartPlayOnce()
        {
            Time.Timer.PostUpdateFrame -= StartPlayOnce;
            _ = Engine.PlayMode.EnterPlayModeAsync();
        }
        Time.Timer.PostUpdateFrame += StartPlayOnce;
    }

    private static void StartEditorStartupTimer()
    {
        lock (s_startupTimerLock)
        {
            if (s_startupStopwatch is not null)
                return;

            s_startupStopwatch = Stopwatch.StartNew();
            Engine.Windows.PostAdded += OnStartupWindowAdded;
        }
    }

    private static void OnStartupWindowAdded(XRWindow window)
    {
        if (Interlocked.CompareExchange(ref s_startupWindowHooked, 1, 0) != 0)
            return;

        s_startupWindow = window;
        //window.PostRenderViewportsCallback += OnStartupPostRenderViewports;
    }

    private static void OnStartupPostRenderViewports()
    {
        if (Volatile.Read(ref s_startupTimerStopped) != 0)
            return;

        var window = s_startupWindow;
        if (window is null)
            return;

        var renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        var viewport = window.Viewports.FirstOrDefault();
        if (viewport is null)
            return;

        if (Interlocked.CompareExchange(ref s_captureInFlight, 1, 0) != 0)
            return;

        renderer.CalcDotLuminanceFrontAsync(viewport.Region, false, (success, luminance) =>
        {
            try
            {
                const float nonBlackThreshold = 0.0001f;
                if (!success || luminance <= nonBlackThreshold)
                    return;

                StopStartupTimer(window, "Editor startup first non-black frame");
            }
            finally
            {
                Interlocked.Exchange(ref s_captureInFlight, 0);
            }
        });
    }

    private static void StopStartupTimer(XRWindow window, string messagePrefix)
    {
        if (Interlocked.CompareExchange(ref s_startupTimerStopped, 1, 0) != 0)
            return;

        s_startupStopwatch?.Stop();
        var elapsed = s_startupStopwatch?.Elapsed.TotalMilliseconds ?? 0;
        EngineDebug.Out($"{messagePrefix} in {elapsed:F0} ms.");
        window.PostRenderViewportsCallback -= OnStartupPostRenderViewports;
        Engine.Windows.PostAdded -= OnStartupWindowAdded;
    }

    /// <summary>
    /// Resolves the world mode from command line arguments or environment variables.
    /// </summary>
    private static EWorldMode ResolveWorldMode(string[] args)
    {
        // Check command line arguments first
        foreach (string arg in args)
        {
            string lower = arg.ToLowerInvariant();
            if (lower == "--unit-testing" || lower == "-unittest" || lower == "--unittest")
                return EWorldMode.UnitTesting;
            if (lower == "--default" || lower == "-default")
                return EWorldMode.Default;
        }

        // Check environment variable
        string? worldModeEnv = Environment.GetEnvironmentVariable("XRE_WORLD_MODE");
        if (!string.IsNullOrWhiteSpace(worldModeEnv) &&
            Enum.TryParse<EWorldMode>(worldModeEnv, true, out var mode))
        {
            EngineDebug.Out($"World mode set to {mode} via XRE_WORLD_MODE environment variable.");
            return mode;
        }

        // Default to empty world
        return EWorldMode.Default;
    }

    /// <summary>
    /// Creates a default empty world with basic lighting and a camera.
    /// </summary>
    private static XRWorld CreateDefaultEmptyWorld()
    {
        EditorUnitTests.ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Main Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        // Enable LAN discovery in the default world.
        // This component auto-starts listening when activated.
        rootNode.AddComponent<NetworkDiscoveryComponent>("Network Discovery");

        EditorUnitTests.Toggles.VRPawn = false;
        EditorUnitTests.Toggles.Locomotion = false;

        SceneNode? characterPawnModelParentNode = EditorUnitTests.Pawns.CreatePlayerPawn(true, false, rootNode);

        EditorUnitTests.Lighting.AddDirLight(rootNode);
        EditorUnitTests.Lighting.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
        EditorUnitTests.Models.AddSkybox(rootNode, null);

        AddDefaultGridFloor(rootNode);

        var world = new XRWorld("Default World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static void AddDefaultGridFloor(SceneNode rootNode)
    {
        var gridNode = rootNode.NewChild("GridFloor");
        var debug = gridNode.AddComponent<DebugDrawComponent>()!;

        const float extent = 50.0f;
        const float step = 1.0f;
        const int majorEvery = 10;
        const float y = 0.0f;

        for (float x = -extent; x <= extent; x += step)
        {
            int xi = (int)MathF.Round(x);
            bool isAxis = xi == 0;
            bool isMajor = (xi % majorEvery) == 0;
            var color = isAxis ? ColorF4.White : isMajor ? ColorF4.Gray : ColorF4.DarkGray;
            debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
        }

        for (float z = -extent; z <= extent; z += step)
        {
            int zi = (int)MathF.Round(z);
            bool isAxis = zi == 0;
            bool isMajor = (zi % majorEvery) == 0;
            var color = isAxis ? ColorF4.White : isMajor ? ColorF4.Gray : ColorF4.DarkGray;
            debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
        }
    }

    private static void TargetWorldInstance_AnyTransformWorldMatrixChanged(XRWorldInstance instance, TransformBase tfm, Matrix4x4 mtx)
    {
        if (PlayMode.IsEditing && !instance.TransitioningPlay && instance.PlayState == EPlayState.Playing)
        {
            var sceneNode = tfm.SceneNode;
            if (sceneNode is null)
                return;

            // Many transforms (e.g., skeletal bones) have no components. Refreshing play lifecycle for them
            // is extremely expensive and can cause frame stutter when large hierarchies update.
            if (sceneNode.ComponentsSerialized.Count == 0)
                return;

            // In edit mode, refresh play lifecycle for this node when the world matrix changes
            sceneNode.OnEndPlay();
            sceneNode.OnBeginPlay();
        }
    }

    private static JsonSerializerSettings DefaultJsonSettings() => new()
    {
        Formatting = Formatting.Indented,
        TypeNameHandling = TypeNameHandling.Auto,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        PreserveReferencesHandling = PreserveReferencesHandling.All,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        NullValueHandling = NullValueHandling.Include,
        Converters = [new StringEnumConverter()]
    };

    private static EditorUnitTests.Settings LoadUnitTestingSettings(bool writeBackAfterRead)
    {
        EditorUnitTests.Settings settings;

        string dir = Environment.CurrentDirectory;
        string fileName = UnitTestingWorldSettingsFileName;
        string filePath = Path.Combine(dir, "Assets", fileName);

        if (!File.Exists(filePath))
            File.WriteAllText(filePath, JsonConvert.SerializeObject(settings = new EditorUnitTests.Settings(), Formatting.Indented));
        else
        {
            string? content = File.ReadAllText(filePath);
            if (content is not null)
            {
                settings = JsonConvert.DeserializeObject<EditorUnitTests.Settings>(content) ?? new EditorUnitTests.Settings();
                if (writeBackAfterRead)
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            else
                settings = new EditorUnitTests.Settings();
        }
        return settings;
    }

    static EditorRenderInfo2D RenderInfo2DConstructor(IRenderable owner, RenderCommand[] commands)
        => new(owner, commands);
    static EditorRenderInfo3D RenderInfo3DConstructor(IRenderable owner, RenderCommand[] commands)
        => new(owner, commands);

    private static VRGameStartupSettings<EVRActionCategory, EVRGameAction> CreateEditorStartupSettings()
    {
        int w = 1920;
        int h = 1080;
        float updateHz = EditorUnitTests.Toggles.UpdateFPS;
        float renderHz = EditorUnitTests.Toggles.RenderFPS;
        float fixedHz = EditorUnitTests.Toggles.FixedFPS;

        int primaryX = NativeMethods.GetSystemMetrics(0);
        int primaryY = NativeMethods.GetSystemMetrics(1);
        EngineDebug.Out("Primary monitor size: {0}x{1}", primaryX, primaryY);

        var settings = new VRGameStartupSettings<EVRActionCategory, EVRGameAction>()
        {
            GameName = "XRE EDITOR",
            StartupWindows =
            [
                new()
                {
                    WindowTitle = "XRE Editor",
                    WindowState = EWindowState.Windowed,
                    UseNativeTitleBar = true,
                    X = primaryX / 2 - w / 2,
                    Y = primaryY / 2 - h / 2,
                    Width = w,
                    Height = h,
                }
            ],
            DefaultUserSettings = new UserSettings()
            {
                VSync = EVSyncMode.Off,
                RenderLibrary = EditorUnitTests.Toggles.RenderAPI,
                PhysicsLibrary = EditorUnitTests.Toggles.PhysicsAPI,
            },
            GPURenderDispatch = EditorUnitTests.Toggles.GPURenderDispatch,
            TargetUpdatesPerSecond = updateHz,
            TargetFramesPerSecond = renderHz,
            FixedFramesPerSecond = fixedHz,
            NetworkingType = GameStartupSettings.ENetworkingType.Client,
        };

        // Allow overriding the window title for multi-instance local testing.
        // Example: launch a 2nd client with XRE_WINDOW_TITLE="XRE Editor (Client 2)".
        string? windowTitleOverride = Environment.GetEnvironmentVariable("XRE_WINDOW_TITLE");
        if (!string.IsNullOrWhiteSpace(windowTitleOverride) && settings.StartupWindows.Count > 0)
        {
            settings.StartupWindows[0].WindowTitle = windowTitleOverride;
            EngineDebug.Out($"Window title overridden to '{windowTitleOverride}' via XRE_WINDOW_TITLE.");
        }

        // Apply engine settings
        Engine.Rendering.Settings.OutputVerbosity = EOutputVerbosity.Verbose;

        // Allow overriding networking mode via env var for quick local testing.
        string? netOverride = Environment.GetEnvironmentVariable("XRE_NET_MODE");
        if (!string.IsNullOrWhiteSpace(netOverride) &&
            Enum.TryParse<GameStartupSettings.ENetworkingType>(netOverride, true, out var mode))
        {
            settings.NetworkingType = mode;
            EngineDebug.Out($"Networking mode overridden to {mode} via XRE_NET_MODE.");
        }

        // Allow overriding UDP ports to support multi-instance local testing.
        // Example: launch a 2nd client with XRE_UDP_CLIENT_RECEIVE_PORT=5002.
        if (TryGetIntEnv("XRE_UDP_CLIENT_RECEIVE_PORT", out int udpClientReceivePort))
        {
            settings.UdpClientRecievePort = udpClientReceivePort;
            EngineDebug.Out($"UDP client receive port overridden to {udpClientReceivePort} via XRE_UDP_CLIENT_RECEIVE_PORT.");
        }
        if (TryGetIntEnv("XRE_UDP_SERVER_SEND_PORT", out int udpServerSendPort))
        {
            settings.UdpServerSendPort = udpServerSendPort;
            EngineDebug.Out($"UDP server send port overridden to {udpServerSendPort} via XRE_UDP_SERVER_SEND_PORT.");
        }
        if (TryGetIntEnv("XRE_UDP_MULTICAST_PORT", out int udpMulticastPort))
        {
            settings.UdpMulticastPort = udpMulticastPort;
            EngineDebug.Out($"UDP multicast port overridden to {udpMulticastPort} via XRE_UDP_MULTICAST_PORT.");
        }

        if (EditorUnitTests.Toggles.VRPawn && (!EditorUnitTests.Toggles.EmulatedVRPawn || EditorUnitTests.Toggles.PreviewVRStereoViews))
        {
            settings.RunVRInPlace = true;
            EditorVR.ApplyOpenVRSettings(settings);
            settings.VRRuntime = EditorUnitTests.Toggles.UseOpenXR
                ? EVRRuntime.OpenXR
                : EVRRuntime.OpenVR;
        }
        else
        {
            settings.RunVRInPlace = false;
        }
        return settings;
    }

    private static bool TryGetIntEnv(string name, out int value)
    {
        value = default;
        string? raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out value);
    }

    private static bool ResolveDebugOpaquePipelineSetting()
    {
        string? forceDebugEnv = Environment.GetEnvironmentVariable("XRE_FORCE_DEBUG_OPAQUE_PIPELINE");
        if (!string.IsNullOrWhiteSpace(forceDebugEnv))
        {
            bool forceDebug =
                string.Equals(forceDebugEnv, "1", StringComparison.Ordinal) ||
                string.Equals(forceDebugEnv, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(forceDebugEnv, "yes", StringComparison.OrdinalIgnoreCase);

            EngineDebug.Out($"[DebugPipeline] XRE_FORCE_DEBUG_OPAQUE_PIPELINE={forceDebugEnv} => UseDebugOpaquePipeline={forceDebug}");
            if (forceDebug)
                return true;
        }

        bool useDebug = EditorUnitTests.Toggles.ForceDebugOpaquePipeline;
        EngineDebug.Out($"[DebugPipeline] ForceDebugOpaquePipeline={useDebug}, HasStaticModels={EditorUnitTests.Toggles.HasStaticModelsToImport}, Mode={EditorUnitTests.Toggles.StaticModelMaterialMode}");

        // The debug opaque pipeline is forward-only and does not execute the default deferred/forward+ pass chain.
        // If the unit test is requesting static model material modes that rely on DefaultRenderPipeline passes,
        // force the default pipeline so results are visible and comparable.
        if (EditorUnitTests.Toggles.HasStaticModelsToImport)
        {
            var mode = EditorUnitTests.Toggles.StaticModelMaterialMode;
            if (mode == EditorUnitTests.StaticModelMaterialMode.Deferred ||
                mode == EditorUnitTests.StaticModelMaterialMode.ForwardPlusTextured ||
                mode == EditorUnitTests.StaticModelMaterialMode.ForwardPlusUberShader)
            {
                if (useDebug)
                    EngineDebug.Out($"[UnitTestingWorld] ForceDebugOpaquePipeline disabled because StaticModelMaterialMode={mode} requires DefaultRenderPipeline.");
                useDebug = false;
            }
        }

        return useDebug;
    }

    /// <summary>
    /// Checks command line arguments for project initialization flags and handles them if present.
    /// Returns true if either no initialization was requested or if initialization succeeded, 
    /// or false if initialization arguments were present but invalid (in which case an error message will be printed and the process should exit with failure code).
    /// </summary>
    private static bool TryInitNewProject(string[] args)
    {
        if (!TryParseProjectInitArgs(args, out var projectDirectory, out var projectName, out bool initFlagSeen, out string? error))
        {
            //Failed to parse arguments, but --init-project was present, so print error and exit with failure code
            if (initFlagSeen)
            {
                Console.Error.WriteLine(error ?? "Invalid project initialization arguments.");
                Environment.ExitCode = 1;
                return false;
            }
            else // No initialization arguments were present, continue with normal startup
                return true;
        }

        // Arguments parsed successfully, proceed with project initialization
        // project name and directory is guaranteed to be non-null and valid if TryParse returns true
        bool success = EditorProjectInitializer.InitializeNewProject(projectDirectory!, projectName!, Console.Out, Console.Error);
        Environment.ExitCode = success ? 0 : 1;
        return success;
    }

    /// <summary>
    /// Parses command line arguments to determine if project initialization is requested, 
    /// and extracts relevant parameters.
    /// Returns true if initialization arguments were present and parsed correctly, 
    /// or false if no initialization was requested or if there was an error (in which case the error message will be set).
    /// </summary>
    private static bool TryParseProjectInitArgs(
        string[] args,
        out string? projectDirectory,
        out string? projectName,
        out bool initFlagSeen,
        out string? error)
    {
        projectDirectory = null;
        projectName = null;
        error = null;
        initFlagSeen = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--init-project":
                case "--create-project":
                case "-initproject":
                case "-createproject":
                    initFlagSeen = true;
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing project directory after --init-project.";
                        return false;
                    }
                    projectDirectory = args[++i];
                    break;
                case "--project-name":
                case "--name":
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing project name after --project-name.";
                        return false;
                    }
                    projectName = args[++i];
                    break;
            }
        }

        if (!initFlagSeen)
            return false;

        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            error = "A project directory must be provided after --init-project.";
            return false;
        }

        projectName ??= Path.GetFileName(Path.GetFullPath(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            error = "A project name could not be determined. Use --project-name to specify one explicitly.";
            return false;
        }

        return true;
    }
}
