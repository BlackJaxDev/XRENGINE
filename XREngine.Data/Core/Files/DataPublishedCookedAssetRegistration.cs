using XREngine.Data;

namespace XREngine.Core.Files;

/// <summary>Installs published serializers for Data-owned settings assets.</summary>
public static class DataPublishedCookedAssetRegistration
{
    public static IDisposable Install()
        => global::XREngine.Generated.GeneratedRuntimeContracts_XREngine_Data.Install();
}
