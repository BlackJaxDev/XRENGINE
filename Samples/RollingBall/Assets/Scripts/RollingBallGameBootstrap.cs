using XREngine;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace RollingBall;

/// <summary>
/// NativeAOT-safe standalone composition root for the Rolling Ball sample.
/// </summary>
public sealed class RollingBallGameBootstrap :
    IGameLaunchBootstrap,
    IGameLaunchRuntimeSmokeBootstrap
{
    private bool _runtimeSmoke;

    public RuntimeApplicationProfile ApplicationProfile
        => RollingBallHostRegistration.PlatformHost?.ApplicationProfile
            ?? RuntimeApplicationProfile.DesktopClient;

    public void InitializeRegistrations()
        => RollingBallRuntimeRegistration.Register();

    public void ConfigureRuntimeSmoke()
    {
        _runtimeSmoke = true;
        RollingBallRuntimeValidation.ConfigureRuntimeSmoke();
    }

    public void CompleteRuntimeSmoke()
        => RollingBallRuntimeValidation.CompleteRuntimeSmoke();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);

        RollingBallRuntimeRegistration.Register();
        RollingBallWorldAsset world = CreateWorld();
        GameStartupSettings startup = RollingBallHostRegistration.PlatformHost?
            .CreateStartupSettings(world, _runtimeSmoke) ?? new GameStartupSettings();

        startup.Name = "Rolling Ball Startup";
        startup.RunVRInPlace = RollingBallHostRegistration.PlatformHost is not null;
        startup.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = _runtimeSmoke ? "Rolling Ball Runtime Smoke" : "Rolling Ball",
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
        => new() { Name = "Rolling Ball Session" };

    private static RollingBallWorldAsset CreateWorld()
        => Engine.Assets.LoadGameAsset<RollingBallWorldAsset>(
            "Worlds",
            "RollingBallWorld.asset")
        ?? throw new InvalidOperationException(
            "The cooked Rolling Ball world asset 'Worlds/RollingBallWorld.asset' could not be loaded.");
}
