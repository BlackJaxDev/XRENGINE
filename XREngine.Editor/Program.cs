using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Numerics;
using System.Text;
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (TryHandleCommandLine(args))
            return;

        //ConsoleHelper.EnsureConsoleAttached();
        Undo.Initialize();
        Debug.Out("XREngine Editor starting...");
        EditorFileDropHandler.Initialize();
        RenderInfo2D.ConstructorOverride = RenderInfo2DConstructor;
        RenderInfo3D.ConstructorOverride = RenderInfo3DConstructor;
        CodeManager.Instance.CompileOnChange = false;
        JsonConvert.DefaultSettings = DefaultJsonSettings;
        EditorPlayModeController.Initialize();
        McpServerHost? mcpServer = McpServerHost.TryStartFromArgs(args);

        // Determine world mode from command line or environment variable
        EWorldMode worldMode = ResolveWorldMode(args);

        // Note: engine startup settings (render API, update rates, etc.) are sourced from UnitTestingWorld.Toggles
        // via GetEngineSettings(). Load the JSON settings for both Default and UnitTesting modes so defaults don't
        // accidentally pick unsupported/undesired values and render a black screen.
        LoadUnitTestingSettings(false);
        XRWorld targetWorld;

        if (worldMode == EWorldMode.UnitTesting)
        {
            targetWorld = UnitTestingWorld.CreateSelectedWorld(true, false);
            Debug.Out("Loading Unit Testing World...");
        }
        else
        {
            targetWorld = CreateDefaultEmptyWorld();
            Debug.Out("Loading Default Empty World...");
        }

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
        var startupSettings = GetEngineSettings(targetWorld);
        var gameState = Engine.LoadOrGenerateGameState();

        if (UnitTestingWorld.Toggles.StartInPlayModeWithoutTransitions)
        {
            Engine.PlayMode.ForcePlayWithoutTransitions = true;
            void StartPlayOnce()
            {
                Time.Timer.PostUpdateFrame -= StartPlayOnce;
                _ = Engine.PlayMode.EnterPlayModeAsync();
            }
            Time.Timer.PostUpdateFrame += StartPlayOnce;
        }

        Engine.Run(startupSettings, gameState);
        mcpServer?.Dispose();
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
            Debug.Out($"World mode set to {mode} via XRE_WORLD_MODE environment variable.");
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
        UnitTestingWorld.ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Main Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        // Enable LAN discovery in the default world.
        // This component auto-starts listening when activated.
        rootNode.AddComponent<NetworkDiscoveryComponent>("Network Discovery");

        UnitTestingWorld.Toggles.VRPawn = false;
        UnitTestingWorld.Toggles.Locomotion = false;

        SceneNode? characterPawnModelParentNode = UnitTestingWorld.Pawns.CreatePlayerPawn(true, false, rootNode);

        UnitTestingWorld.Lighting.AddDirLight(rootNode);
        UnitTestingWorld.Lighting.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
        UnitTestingWorld.Models.AddSkybox(rootNode, null);

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

    private static void LoadUnitTestingSettings(bool writeBackAfterRead)
    {
        string dir = Environment.CurrentDirectory;
        string fileName = UnitTestingWorldSettingsFileName;
        string filePath = Path.Combine(dir, "Assets", fileName);
        if (!File.Exists(filePath))
            File.WriteAllText(filePath, JsonConvert.SerializeObject(UnitTestingWorld.Toggles, Formatting.Indented));
        else
        {
            string? content = File.ReadAllText(filePath);
            if (content is not null)
            {
                UnitTestingWorld.Toggles = JsonConvert.DeserializeObject<UnitTestingWorld.Settings>(content) ?? new UnitTestingWorld.Settings();
                if (writeBackAfterRead)
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(UnitTestingWorld.Toggles, Formatting.Indented));
            }
        }
    }

    static EditorRenderInfo2D RenderInfo2DConstructor(IRenderable owner, RenderCommand[] commands)
        => new(owner, commands);
    static EditorRenderInfo3D RenderInfo3DConstructor(IRenderable owner, RenderCommand[] commands)
        => new(owner, commands);

    private static VRGameStartupSettings<EVRActionCategory, EVRGameAction> GetEngineSettings(XRWorld targetWorld)
    {
        int w = 1920;
        int h = 1080;
        float updateHz = UnitTestingWorld.Toggles.UpdateFPS;
        float renderHz = UnitTestingWorld.Toggles.RenderFPS;
        float fixedHz = UnitTestingWorld.Toggles.FixedFPS;

        int primaryX = NativeMethods.GetSystemMetrics(0);
        int primaryY = NativeMethods.GetSystemMetrics(1);
        Debug.Out("Primary monitor size: {0}x{1}", primaryX, primaryY);

        var settings = new VRGameStartupSettings<EVRActionCategory, EVRGameAction>()
        {
            GameName = "XRE EDITOR",
            StartupWindows =
            [
                new()
                {
                    WindowTitle = "XRE Editor",
                    TargetWorld = targetWorld,
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
                RenderLibrary = UnitTestingWorld.Toggles.RenderAPI,
                PhysicsLibrary = UnitTestingWorld.Toggles.PhysicsAPI,
            },
            GPURenderDispatch = UnitTestingWorld.Toggles.GPURenderDispatch,
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
            Debug.Out($"Window title overridden to '{windowTitleOverride}' via XRE_WINDOW_TITLE.");
        }

        // Apply engine settings
        Engine.EditorPreferences.Debug.UseDebugOpaquePipeline = ResolveDebugOpaquePipelineSetting();
        Engine.Rendering.Settings.OutputVerbosity = EOutputVerbosity.Verbose;

        // Allow overriding networking mode via env var for quick local testing.
        string? netOverride = Environment.GetEnvironmentVariable("XRE_NET_MODE");
        if (!string.IsNullOrWhiteSpace(netOverride) &&
            Enum.TryParse<GameStartupSettings.ENetworkingType>(netOverride, true, out var mode))
        {
            settings.NetworkingType = mode;
            Debug.Out($"Networking mode overridden to {mode} via XRE_NET_MODE.");
        }

        // Allow overriding UDP ports to support multi-instance local testing.
        // Example: launch a 2nd client with XRE_UDP_CLIENT_RECEIVE_PORT=5002.
        if (TryGetIntEnv("XRE_UDP_CLIENT_RECEIVE_PORT", out int udpClientReceivePort))
        {
            settings.UdpClientRecievePort = udpClientReceivePort;
            Debug.Out($"UDP client receive port overridden to {udpClientReceivePort} via XRE_UDP_CLIENT_RECEIVE_PORT.");
        }
        if (TryGetIntEnv("XRE_UDP_SERVER_SEND_PORT", out int udpServerSendPort))
        {
            settings.UdpServerSendPort = udpServerSendPort;
            Debug.Out($"UDP server send port overridden to {udpServerSendPort} via XRE_UDP_SERVER_SEND_PORT.");
        }
        if (TryGetIntEnv("XRE_UDP_MULTICAST_PORT", out int udpMulticastPort))
        {
            settings.UdpMulticastPort = udpMulticastPort;
            Debug.Out($"UDP multicast port overridden to {udpMulticastPort} via XRE_UDP_MULTICAST_PORT.");
        }

        if (UnitTestingWorld.Toggles.VRPawn && (!UnitTestingWorld.Toggles.EmulatedVRPawn || UnitTestingWorld.Toggles.PreviewVRStereoViews))
        {
            settings.RunVRInPlace = true;
            EditorVR.ApplyOpenVRSettings(settings);
            settings.VRRuntime = UnitTestingWorld.Toggles.UseOpenXR
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
        bool useDebug = UnitTestingWorld.Toggles.ForceDebugOpaquePipeline;

        // The debug opaque pipeline is forward-only and does not execute the default deferred/forward+ pass chain.
        // If the unit test is requesting static model material modes that rely on DefaultRenderPipeline passes,
        // force the default pipeline so results are visible and comparable.
        if (UnitTestingWorld.Toggles.HasStaticModelsToImport)
        {
            var mode = UnitTestingWorld.Toggles.StaticModelMaterialMode;
            if (mode == UnitTestingWorld.StaticModelMaterialMode.Deferred ||
                mode == UnitTestingWorld.StaticModelMaterialMode.ForwardPlusTextured ||
                mode == UnitTestingWorld.StaticModelMaterialMode.ForwardPlusUberShader)
            {
                if (useDebug)
                    Debug.Out($"[UnitTestingWorld] ForceDebugOpaquePipeline disabled because StaticModelMaterialMode={mode} requires DefaultRenderPipeline.");
                useDebug = false;
            }
        }

        return useDebug;
    }

    private static bool TryHandleCommandLine(string[] args)
    {
        if (!TryParseProjectInitArgs(args, out var projectDirectory, out var projectName, out bool initFlagSeen, out string? error))
        {
            if (initFlagSeen)
            {
                Console.Error.WriteLine(error ?? "Invalid project initialization arguments.");
                Environment.ExitCode = 1;
                return true;
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(projectDirectory))
            return false;

        bool success = EditorProjectInitializer.InitializeNewProject(projectDirectory, projectName!, Console.Out, Console.Error);
        Environment.ExitCode = success ? 0 : 1;
        return true;
    }

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
