using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace StaticMeshletParity;

/// <summary>Loads the saved static panel and selects its authored mesh submission profile.</summary>
public sealed class StaticMeshletParityGameBootstrap : IGameLaunchBootstrap
{
    public RuntimeApplicationProfile ApplicationProfile => RuntimeApplicationProfile.DesktopClient;

    public void InitializeRegistrations() => StaticMeshletParityRuntimeRegistration.Register();

    public GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings)
    {
        ArgumentNullException.ThrowIfNull(cookedSettings);
        XRWorld world = Engine.Assets.LoadGameAsset<XRWorld>("Worlds", "StaticMeshletParityWorld.asset")
            ?? throw new InvalidOperationException("The saved static meshlet world could not be loaded.");
        if (world.DefaultGameMode is not StaticMeshletParityGameMode { FixtureMarker: "mapped-static-meshlet-v1" })
            throw new InvalidOperationException("The saved static meshlet game mode did not hydrate.");

        StaticMeshletParityWorldContract.Validate(world);
        if (cookedSettings.DefaultUserSettings.GPURenderDispatchOverride is not { HasOverride: true } dispatch)
            throw new InvalidDataException("StaticMeshletParity.Startup: the saved GPU dispatch override is required.");
        EMeshSubmissionStrategy requested = dispatch.Value
            ? EMeshSubmissionStrategy.GpuMeshletZeroReadback : EMeshSubmissionStrategy.CpuDirect;
        Console.WriteLine($"StaticMeshletParity requested={requested} " +
            $"material={StaticMeshletParityWorldContract.MaterialId} mesh={StaticMeshletParityWorldContract.MeshId} " +
            "semantic=StandardLitTextureV1 payload=owner-validated");

        cookedSettings.StartupWindows =
        [
            new GameWindowStartupSettings
            {
                WindowTitle = "Static Meshlet Parity",
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

    public GameState CreateInitialGameState() => new() { Name = "Static Meshlet Inspection" };
}
