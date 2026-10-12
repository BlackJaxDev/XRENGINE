using XREngine;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace BrowserUiParity;

/// <summary>Loads the same saved UI world through the desktop and browser asset services.</summary>
public sealed class BrowserUiParityGameBootstrap : IGameLaunchBootstrap
{
    public RuntimeApplicationProfile ApplicationProfile => RuntimeApplicationProfile.DesktopClient;

    public void InitializeRegistrations() => BrowserUiParityRuntimeRegistration.Register();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);
        XRWorld world = Engine.Assets.LoadGameAsset<XRWorld>("Worlds", "BrowserUiParityWorld.asset")
            ?? throw new InvalidOperationException("The saved UI world could not be loaded.");
        if (world.DefaultGameMode is not BrowserUiParityGameMode { FixtureMarker: "shared-ui-offscreen-input-v1" })
            throw new InvalidOperationException("The saved UI game mode did not hydrate.");
        cookedSettings.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = "Browser UI Parity",
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

    public GameState CreateInitialGameState() => new() { Name = "Shared UI Inspection" };
}
