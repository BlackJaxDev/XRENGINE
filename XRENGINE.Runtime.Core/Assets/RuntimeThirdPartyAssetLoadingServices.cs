using XREngine.Core.Files;
using XREngine.Data;
using System.Reflection;

namespace XREngine;

/// <summary>Feature-composed third-party asset loading used by the runtime asset owner.</summary>
public interface IRuntimeThirdPartyAssetLoadingServices
{
    XRAsset? Load(
        string filePath,
        string extension,
        Type assetType,
        object? importOptions = null,
        AssetImportContext? importContext = null);
}

public static class RuntimeThirdPartyAssetLoadingServices
{
    private static readonly IRuntimeThirdPartyAssetLoadingServices Default = new ReflectionServices();
    private static IRuntimeThirdPartyAssetLoadingServices _current = Default;

    public static IRuntimeThirdPartyAssetLoadingServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(IRuntimeThirdPartyAssetLoadingServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeThirdPartyAssetLoadingServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IRuntimeThirdPartyAssetLoadingServices installed,
        IRuntimeThirdPartyAssetLoadingServices previous) : IDisposable
    {
        private IRuntimeThirdPartyAssetLoadingServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeThirdPartyAssetLoadingServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class ReflectionServices : IRuntimeThirdPartyAssetLoadingServices
    {
        public XRAsset? Load(
            string filePath,
            string extension,
            Type assetType,
            object? importOptions = null,
            AssetImportContext? importContext = null)
        {
            XR3rdPartyExtensionsAttribute? attribute =
                assetType.GetCustomAttribute<XR3rdPartyExtensionsAttribute>();
            (string ext, bool staticLoad)? match = attribute?.Extensions.FirstOrDefault(
                entry => string.Equals(entry.ext, extension, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"Asset type '{assetType.FullName}' does not declare third-party extension '.{extension}' " +
                    $"for '{filePath}'.");
            }

            if (match.Value.staticLoad)
            {
                MethodInfo? method = assetType.GetMethod(
                    "Load3rdPartyStatic",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: [typeof(string)],
                    modifiers: null);
                if (method is null)
                {
                    throw new MissingMethodException(
                        assetType.FullName,
                        "Load3rdPartyStatic(string)");
                }

                XRAsset? loaded = method.Invoke(null, [filePath]) as XRAsset;
                if (loaded is null)
                    return null;

                loaded.OriginalPath = filePath;
                return loaded;
            }

            if (Activator.CreateInstance(assetType) is not XRAsset asset)
                throw new InvalidOperationException($"Unable to construct third-party asset type '{assetType.FullName}'.");

            asset.OriginalPath = filePath;
            AssetImportContext context = importContext ?? new AssetImportContext(filePath, cacheDirectory: null);
            return asset.Load3rdParty(filePath, importOptions, context) ? asset : null;
        }
    }
}
