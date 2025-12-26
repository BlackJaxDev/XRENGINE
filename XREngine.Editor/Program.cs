using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Numerics;
using XREngine;
using XREngine.Editor;
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
    /// This project serves as a hardcoded game client for development purposes.
    /// This editor will autogenerate the client exe csproj to compile production games.
    /// </summary>
    /// <param name="args"></param>
    [STAThread]
    private static void Main(string[] args)
    {
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
        LoadUnitTestingSettings(false);
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
        Engine.Run(/*Engine.LoadOrGenerateGameSettings(() => */GetEngineSettings(UnitTestingWorld.CreateSelectedWorld(true, false)/*), "startup", false*/), Engine.LoadOrGenerateGameState());
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
                TargetFramesPerSecond = renderHz,
                VSync = EVSyncMode.Off,
                RenderLibrary = UnitTestingWorld.Toggles.RenderAPI,
                PhysicsLibrary = UnitTestingWorld.Toggles.PhysicsAPI,
            },
            GPURenderDispatch = UnitTestingWorld.Toggles.GPURenderDispatch,
            TargetUpdatesPerSecond = updateHz,
            FixedFramesPerSecond = fixedHz,
            NetworkingType = GameStartupSettings.ENetworkingType.Client,
        };

        // Apply engine settings
        Engine.Rendering.Settings.UseDebugOpaquePipeline = ResolveDebugOpaquePipelineSetting();
        Engine.Rendering.Settings.OutputVerbosity = EOutputVerbosity.Verbose;

        // Allow overriding networking mode via env var for quick local testing.
        string? netOverride = Environment.GetEnvironmentVariable("XRE_NET_MODE");
        if (!string.IsNullOrWhiteSpace(netOverride) &&
            Enum.TryParse<GameStartupSettings.ENetworkingType>(netOverride, true, out var mode))
        {
            settings.NetworkingType = mode;
            Debug.Out($"Networking mode overridden to {mode} via XRE_NET_MODE.");
        }
        if (UnitTestingWorld.Toggles.VRPawn && !UnitTestingWorld.Toggles.EmulatedVRPawn)
        {
            if (UnitTestingWorld.Toggles.UseOpenXR)
            {
                settings.VRRuntime = EVRRuntime.OpenXR;
                settings.RunVRInPlace = true;
            }
            else
            {
                settings.VRRuntime = EVRRuntime.OpenVR;
                EditorVR.ApplyVRSettings(settings);
            }
        }
        return settings;
    }

    private static bool ResolveDebugOpaquePipelineSetting()
    {
        bool useDebug = UnitTestingWorld.Toggles.ForceDebugOpaquePipeline;

        // The debug opaque pipeline is forward-only and does not execute the default deferred/forward+ pass chain.
        // If the unit test is requesting static model material modes that rely on DefaultRenderPipeline passes,
        // force the default pipeline so results are visible and comparable.
        if (UnitTestingWorld.Toggles.ImportStaticModel)
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
