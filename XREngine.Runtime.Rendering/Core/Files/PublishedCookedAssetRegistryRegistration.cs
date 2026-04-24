using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Core.Files;

[SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios", Justification = "Published cooked asset serializers must register when the runtime-rendering assembly loads.")]
internal static class PublishedCookedAssetRegistryRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(XRMesh), static () => new XRMesh());
        RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(XRTexture2D), static () => new XRTexture2D());

        PublishedCookedAssetRegistry.Register(
            typeof(XRMesh),
            static asset => RuntimeCookedBinarySerializer.Serialize((XRMesh)asset),
            static (payload, assetType) => RuntimeCookedBinarySerializer.Deserialize(assetType, payload));

        PublishedCookedAssetRegistry.Register(
            typeof(XRTexture2D),
            static asset => RuntimeCookedBinarySerializer.Serialize((XRTexture2D)asset),
            static (payload, assetType) => RuntimeCookedBinarySerializer.Deserialize(assetType, payload));
    }
}
