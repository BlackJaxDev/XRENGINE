using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using XREngine;
using XREngine.Editor;
using XREngine.Native;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;

internal class Program
{
    private const string UnitTestingWorldSettingsFileName = "UnitTestingWorldSettings.json";

    /// <summary>
    /// This project serves as a hardcoded game client for development purposes.
    /// This editor will autogenerate the client exe csproj to compile production games.
    /// </summary>
    /// <param name="args"></param>
    private static void Main(string[] args)
    {
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
        Engine.Run(/*Engine.LoadOrGenerateGameSettings(() => */GetEngineSettings(UnitTestingWorld.CreateUnitTestWorld(true, false)/*), "startup", false*/), Engine.LoadOrGenerateGameState());
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
                    X = primaryX / 2 - w / 2,
                    Y = primaryY / 2 - h / 2,
                    Width = w,
                    Height = h,
                }
            ],
            OutputVerbosity = EOutputVerbosity.Verbose,
            DefaultUserSettings = new UserSettings()
            {
                TargetFramesPerSecond = renderHz,
                VSync = EVSyncMode.Off,
            },
            TargetUpdatesPerSecond = updateHz,
            FixedFramesPerSecond = fixedHz,
            NetworkingType = GameStartupSettings.ENetworkingType.Client,
        };
        if (UnitTestingWorld.Toggles.VRPawn && !UnitTestingWorld.Toggles.EmulatedVRPawn)
            EditorVR.ApplyVRSettings(settings);
        return settings;
    }
}