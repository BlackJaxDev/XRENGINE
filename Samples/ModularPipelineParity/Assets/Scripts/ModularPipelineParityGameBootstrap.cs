using XREngine;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace ModularPipelineParity;

/// <summary>Loads the saved world through ordinary game assets during browser launch.</summary>
public sealed class ModularPipelineParityGameBootstrap : IGameLaunchBootstrap
{
    public RuntimeApplicationProfile ApplicationProfile => RuntimeApplicationProfile.DesktopClient;

    public void InitializeRegistrations() => ModularPipelineParityRuntimeRegistration.Register();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);
        XRWorld world = Engine.Assets.LoadGameAsset<XRWorld>("Worlds", "ModularPipelineParityWorld.asset")
            ?? throw new InvalidOperationException("The saved modular pipeline world could not be loaded.");
        if (world.DefaultGameMode is not ModularPipelineParityGameMode { FixtureMarker: "modular-clear-quad-v1" })
            throw new InvalidOperationException("The saved modular pipeline game mode did not hydrate.");
        ModularPipelineParityWorldContract.Validate(world);
        ModularPipelineParityWorldContract.ValidateSceneParts(world);
        cookedSettings.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = "Modular Pipeline Parity",
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

    public GameState CreateInitialGameState() => new() { Name = "Modular Pipeline Inspection" };
}
