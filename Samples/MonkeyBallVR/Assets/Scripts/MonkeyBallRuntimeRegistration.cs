using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace MonkeyBallVR;

/// <summary>
/// Registers the saved MonkeyBall world as a strict NativeAOT runtime asset.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios",
    Justification = "The game assembly must register its cooked world serializer before the editor cooks content or the launcher loads it.")]
internal static class MonkeyBallRuntimeRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        // Runtime asset-service composition can replace its owned registrations after
        // module initialization. Reassert this game-owned serializer at the bootstrap
        // boundary while keeping repeated initialization harmless.
        if (PublishedCookedAssetRegistry.IsRegistered(typeof(MonkeyBallWorldAsset)))
            return;

        PublishedCookedAssetRegistry.Register(
            typeof(MonkeyBallWorldAsset),
            static asset => MonkeyBallWorldCookedSerializer.Serialize((MonkeyBallWorldAsset)asset),
            static (payload, assetType) => assetType == typeof(MonkeyBallWorldAsset)
                ? MonkeyBallWorldCookedSerializer.Deserialize(payload)
                : null);
    }
}
