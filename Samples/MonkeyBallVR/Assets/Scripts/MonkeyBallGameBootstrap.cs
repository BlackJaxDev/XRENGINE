using XREngine;
using XREngine.Scene;

namespace MonkeyBallVR;

/// <summary>
/// NativeAOT-safe standalone composition root for the MonkeyBall VR sample.
/// </summary>
public sealed class MonkeyBallGameBootstrap : IGameLaunchBootstrap
{
    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);

        XRWorld world = CreateWorld();
        return new VRGameStartupSettings<MonkeyBallActionSet, MonkeyBallAction>
        {
            Name = "MonkeyBall VR Startup",
            GameName = "MonkeyBall VR",
            VRRuntime = EVRRuntime.Auto,
            RunVRInPlace = true,
            VRManifest = MonkeyBallVrManifest.CreateApplicationManifest(),
            ActionManifest = MonkeyBallVrManifest.CreateActionManifest(),
            StartupWindows =
            [
                new GameWindowStartupSettings
                {
                    WindowTitle = "MonkeyBall VR",
                    Width = 1600,
                    Height = 900,
                    VSync = false,
                    TargetWorld = world,
                }
            ],
            DefaultUserSettings = cookedSettings.DefaultUserSettings ?? new UserSettings(),
            BuildSettings = cookedSettings.BuildSettings,
            NetworkingType = ENetworkingType.Local,
            LogOutputToFile = cookedSettings.LogOutputToFile,
            TargetUpdatesPerSecond = 90.0f,
            TargetFramesPerSecond = 90.0f,
            FixedFramesPerSecond = 90.0f,
            LayerNames = cookedSettings.LayerNames,
        };
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
