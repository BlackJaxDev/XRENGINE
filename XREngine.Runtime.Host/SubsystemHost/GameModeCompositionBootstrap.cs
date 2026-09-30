
namespace XREngine;

/// <summary>
/// Registers scene-composition game modes without introducing higher-layer dependencies into Runtime.Core.
/// </summary>
public static class GameModeCompositionBootstrap
{
    public static void RegisterBuiltInGameModes()
    {
        GameModeBootstrapRegistry.Register(
            GameModeBootstrapRegistry.FlyingCameraBootstrapId,
            static () => new FlyingCameraGameMode(),
            typeof(FlyingCameraGameMode),
            nameof(FlyingCameraGameMode),
            typeof(FlyingCameraGameMode).FullName!);
        GameModeBootstrapRegistry.Register(
            GameModeBootstrapRegistry.LocomotionBootstrapId,
            static () => new LocomotionGameMode(),
            typeof(LocomotionGameMode),
            nameof(LocomotionGameMode),
            typeof(LocomotionGameMode).FullName!);
        GameModeBootstrapRegistry.Register(
            GameModeBootstrapRegistry.VrBootstrapId,
            static () => new VRGameMode(),
            typeof(VRGameMode),
            nameof(VRGameMode),
            typeof(VRGameMode).FullName!);
    }
}
