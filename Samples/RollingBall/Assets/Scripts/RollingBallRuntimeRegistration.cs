using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace RollingBall;

/// <summary>
/// Registers the saved Rolling Ball world as a strict NativeAOT runtime asset.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios",
    Justification = "The game assembly must register its cooked world serializer before the editor cooks content or the launcher loads it.")]
public static class RollingBallRuntimeRegistration
{
    [ModuleInitializer]
    public static void Register()
    {
        // Runtime asset-service composition can replace its owned registrations after
        // module initialization. Reassert this game-owned serializer at the bootstrap
        // boundary while keeping repeated initialization harmless.
        if (PublishedCookedAssetRegistry.IsRegistered(typeof(RollingBallWorldAsset)))
            return;

        PublishedCookedAssetRegistry.Register(
            typeof(RollingBallWorldAsset),
            static (asset, writer) => RollingBallWorldCookedSerializer.Serialize((RollingBallWorldAsset)asset, writer),
            static (payload, assetType) => assetType == typeof(RollingBallWorldAsset)
                ? RollingBallWorldCookedSerializer.Deserialize(payload)
                : null,
            ownerName: null,
            dependencies: static asset => RollingBallWorldCookedSerializer.GetExternalDependencies((RollingBallWorldAsset)asset));
    }
}
