using XREngine;
using XREngine.Scene;

namespace MonkeyBallVR;

/// <summary>
/// NativeAOT-safe standalone composition root for the MonkeyBall VR sample.
/// </summary>
public sealed class MonkeyBallGameBootstrap :
    IGameLaunchBootstrap,
    IGameLaunchRuntimeSmokeBootstrap
{
    private bool _runtimeSmoke;

    public void ConfigureRuntimeSmoke()
    {
        _runtimeSmoke = true;
        MonkeyBallRuntimeValidation.ConfigureRuntimeSmoke();
    }

    public void CompleteRuntimeSmoke()
        => MonkeyBallRuntimeValidation.CompleteRuntimeSmoke();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);

        MonkeyBallRuntimeRegistration.Register();
        XRWorld world = CreateWorld();
        GameStartupSettings startup = _runtimeSmoke
            ? new GameStartupSettings()
            : new VRGameStartupSettings<MonkeyBallActionSet, MonkeyBallAction>
            {
                GameName = "MonkeyBall VR",
                VRRuntime = EVRRuntime.Auto,
                VRManifest = MonkeyBallVrManifest.CreateApplicationManifest(),
                ActionManifest = MonkeyBallVrManifest.CreateActionManifest(),
            };

        startup.Name = "MonkeyBall VR Startup";
        startup.RunVRInPlace = true;
        startup.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = _runtimeSmoke ? "MonkeyBall VR Runtime Smoke" : "MonkeyBall VR",
                Width = 1600,
                Height = 900,
                VSync = false,
                TargetWorld = world,
            }
        ];
        startup.DefaultUserSettings = cookedSettings.DefaultUserSettings ?? new UserSettings();
        startup.BuildSettings = cookedSettings.BuildSettings;
        startup.NetworkingType = ENetworkingType.Local;
        startup.LogOutputToFile = cookedSettings.LogOutputToFile;
        startup.TargetUpdatesPerSecond = 90.0f;
        startup.TargetFramesPerSecond = 90.0f;
        startup.FixedFramesPerSecond = 120.0f;
        startup.LayerNames = cookedSettings.LayerNames;
        return startup;
    }

    public GameState CreateInitialGameState()
        => new() { Name = "MonkeyBall VR Session" };

    private static XRWorld CreateWorld()
        => Engine.Assets.LoadGameAsset<MonkeyBallWorldAsset>(
            "Worlds",
            "MonkeyBallWorld.asset")
        ?? throw new InvalidOperationException(
            "The cooked MonkeyBall world asset 'Worlds/MonkeyBallWorld.asset' could not be loaded.");
}
