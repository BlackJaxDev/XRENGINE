using XREngine;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace RenderingParity;

/// <summary>Loads the same saved world through the desktop and browser asset services.</summary>
public sealed class RenderingParityGameBootstrap : IGameLaunchBootstrap
{
    public RuntimeApplicationProfile ApplicationProfile => RuntimeApplicationProfile.DesktopClient;

    public void InitializeRegistrations() => RenderingParityRuntimeRegistration.Register();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);
        XRWorld world = Engine.Assets.LoadGameAsset<XRWorld>("Worlds", "RenderingParityWorld.asset")
            ?? throw new InvalidOperationException("The saved rendering world could not be loaded.");
        if (world.DefaultGameMode is not RenderingParityGameMode { FixtureMarker: "lit-textured-deformed-v1" })
            throw new InvalidOperationException("The saved rendering game mode did not hydrate.");
        cookedSettings.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = "Rendering Parity",
                Width = 1280,
                Height = 720,
                VSync = false,
                TargetWorld = world,
            }
        ];
        cookedSettings.RunVRInPlace = false;
        cookedSettings.NetworkingType = ENetworkingType.Local;
        return cookedSettings;
    }

    public GameState CreateInitialGameState() => new() { Name = "Rendering Inspection" };
}
