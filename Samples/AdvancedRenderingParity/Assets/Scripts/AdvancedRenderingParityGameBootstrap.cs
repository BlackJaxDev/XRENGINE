using XREngine;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace AdvancedRenderingParity;

/// <summary>Loads the same saved world through desktop and browser asset services.</summary>
public sealed class AdvancedRenderingParityGameBootstrap : IGameLaunchBootstrap
{
    public RuntimeApplicationProfile ApplicationProfile => RuntimeApplicationProfile.DesktopClient;

    public void InitializeRegistrations() => AdvancedRenderingParityRuntimeRegistration.Register();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);
        XRWorld world = Engine.Assets.LoadGameAsset<XRWorld>("Worlds", "AdvancedRenderingParityWorld.asset")
            ?? throw new InvalidOperationException("The saved advanced rendering world could not be loaded.");
        if (world.DefaultGameMode is not AdvancedRenderingParityGameMode { FixtureMarker: "advanced-static-pbr-v1" })
            throw new InvalidOperationException("The saved advanced rendering game mode did not hydrate.");
        cookedSettings.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = "Advanced Rendering Parity",
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

    public GameState CreateInitialGameState() => new() { Name = "Advanced Rendering Inspection" };
}
