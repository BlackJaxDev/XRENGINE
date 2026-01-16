using Newtonsoft.Json;
using OpenVR.NET.Manifest;
using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Reflection.Emit;
using XREngine.Native;
using XREngine.Scene;

namespace XREngine.VRClient
{
    internal class Program
    {
        static void Main(string[] args)
        {
            IVRGameStartupSettings settings = GenerateSettings();

            // Check if this is already running
            var exists = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()?.Location)).Length > 1;
            if (exists)
            {
                Debug.LogWarning("VR client is already running.");
                Console.In.ReadLine();
                return;
            }

            // Find or start the main game process
            if (!VerifyMainGameRunning(null, settings, out string? gamePath))
            {
                Debug.LogWarning("Could not find main game process.");
                Console.In.ReadLine();
                return;
            }

            // Initialize VR
            if (settings.ActionManifest is null)
            {
                Debug.LogWarning("VR settings not initialized correctly; missing ActionManifest.");
                Console.In.ReadLine();
            }
            else if (settings.VRManifest is null)
            {
                Debug.LogWarning("VR settings not initialized correctly; missing VRManifest.");
                Console.In.ReadLine();
            }
            else
                Engine.VRState.IninitializeClient(settings.ActionManifest, settings.VRManifest).GetAwaiter().GetResult();

            // Run the game
            // We don't need to load a game state because this app only sends inputs to the game and receives renders
            Engine.Run((GameStartupSettings)settings, new GameState());
        }

        private static IVRGameStartupSettings GenerateSettings()
        {
            ModuleBuilder moduleBuilder = MakeDynamicAssemblyModule("DynamicEnums");

            //TODO: read from game init file
            string[] actionCategoryNames = ["Global", "OneHanded", "QuickMenu", "Menu", "AvatarMenu"];
            string[] gameActionNames = ["Interact", "Jump", "ToggleMute", "Grab", "PlayspaceDragLeft", "PlayspaceDragRight", "ToggleQuickMenu", "ToggleMenu", "ToggleAvatarMenu", "LeftHandPose", "RightHandPose", "Locomote", "Turn"];

            var actionCategoryType = CreateEnumType(moduleBuilder, "EActionCategory", actionCategoryNames);
            var gameActionType = CreateEnumType(moduleBuilder, "EGameAction", gameActionNames);

            return (IVRGameStartupSettings)typeof(Program).
                GetMethod(nameof(GenerateGameSettings), BindingFlags.NonPublic | BindingFlags.Static)!.
                MakeGenericMethod([actionCategoryType, gameActionType]).
                Invoke(null, null)!;
        }

        private static Type CreateEnumType(ModuleBuilder moduleBuilder, string typeName, string[] names)
        {
            EnumBuilder enumBuilder = moduleBuilder.DefineEnum(typeName, TypeAttributes.Public, typeof(int));
            for (int i = 0; i < names.Length; i++)
                enumBuilder.DefineLiteral(names[i], i);
            return enumBuilder.CreateType();
        }

        private static ModuleBuilder MakeDynamicAssemblyModule(string dynamicAssemblyName)
            => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(dynamicAssemblyName), AssemblyBuilderAccess.Run).DefineDynamicModule(dynamicAssemblyName);

        private static bool VerifyMainGameRunning(Process[]? processes, IVRGameStartupSettings settings, out string? resultPath)
        {
            resultPath = null;
            string name = settings.GameName;
            string fileName = $"{name}.exe";

            var gameProcess = Process.GetProcessesByName(name).FirstOrDefault();
            if (gameProcess != null)
                return true;
            
            var searchPaths = settings.GameSearchPaths;
            foreach (var (folder, relativePath) in searchPaths)
            {
                string rootDir = Environment.GetFolderPath(folder);
                string fullPath = Path.Combine(rootDir, relativePath, fileName);
                if (File.Exists(fullPath))
                {
                    resultPath = fullPath;
                    Process.Start(fullPath);
                    return true;
                }
            }
            resultPath = null;
            return false;
        }

        private static string? GetMainModuleFilepath(int processId)
        {
            string wmiQueryString = $"SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE ProcessId = {processId}";
            using var searcher = new ManagementObjectSearcher(wmiQueryString);
            using var results = searcher.Get();
            ManagementObject? mo = results.Cast<ManagementObject>().FirstOrDefault();
            return mo is not null ? (string?)mo["ExecutablePath"] : null;
        }

        private static VRGameStartupSettings<TActionCategory, TGameAction> GenerateGameSettings<TActionCategory, TGameAction>()
            where TActionCategory : struct, Enum
            where TGameAction : struct, Enum
        {
            int w = 1920;
            int h = 1080;
            float update = 90.0f;
            float render = 90.0f;

            int primaryX = NativeMethods.GetSystemMetrics(0);
            int primaryY = NativeMethods.GetSystemMetrics(1);

            var settings = new VRGameStartupSettings<TActionCategory, TGameAction>()
            {
                GameName = "XREngine Game",
                GameSearchPaths =
                [
                    (Environment.SpecialFolder.ProgramFiles, "MyGameFolder")
                ],
                VRManifest = new VrManifest()
                {
                    AppKey = "XRE.VRClient.Test",
                    IsDashboardOverlay = false,
                    WindowsPath = Environment.ProcessPath,
                    WindowsArguments = "",
                },
                StartupWindows =
                [
                    new GameWindowStartupSettings()
                    {
                        WindowTitle = "XREngine VRClient",
                        TargetWorld = CreateFillerWorld(),
                        WindowState = EWindowState.Windowed,
                        X = primaryX / 2 - w / 2,
                        Y = primaryY / 2 - h / 2,
                        Width = w,
                        Height = h,
                    }
                ],
                OutputVerbosityOverride = new XREngine.Data.Core.OverrideableSetting<EOutputVerbosity>(EOutputVerbosity.Verbose, true),
                UseIntegerWeightingIdsOverride = new XREngine.Data.Core.OverrideableSetting<bool>(true, true),
                DefaultUserSettings = new UserSettings()
                {
                    VSync = EVSyncMode.Off,
                },
                TargetUpdatesPerSecond = update,
                TargetFramesPerSecond = render,
                ActionManifest = new ActionManifest<TActionCategory, TGameAction>()
                {
                    Actions = GetActions<TActionCategory, TGameAction>(),
                },
            };

            // Allow pairing the VRClient with a specific running game process (e.g., XREngine.Editor).
            string? gameNameOverride = Environment.GetEnvironmentVariable("XRE_VRCLIENT_GAMENAME");
            if (!string.IsNullOrWhiteSpace(gameNameOverride))
                settings.GameName = gameNameOverride;

            return settings;
        }

        private static List<OpenVR.NET.Manifest.Action<TActionCategory, TGameAction>> GetActions<TActionCategory, TGameAction>()
            where TActionCategory : struct, Enum
            where TGameAction : struct, Enum
        {
            return [];
        }

        private static XRWorld CreateFillerWorld()
        {
            var world = new XRWorld();
            var scene = new XRScene() { Name = "FillerScene" };
            world.Scenes.Add(scene);
            return world;
        }
    }
}
