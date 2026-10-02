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
        AssetImportContext? importContext = null,
        XRAsset? targetAsset = null);
}

public static class RuntimeThirdPartyAssetLoadingServices
{
    private static readonly IRuntimeThirdPartyAssetLoadingServices Default = new ReflectionServices();
    private static readonly object Sync = new();
    private static IRuntimeThirdPartyAssetLoadingServices _current = Default;
    private static InstallationLease? _head;

    public static IRuntimeThirdPartyAssetLoadingServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(IRuntimeThirdPartyAssetLoadingServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (Sync)
        {
            InstallationLease lease = new(services, _head);
            _head = lease;
            Volatile.Write(ref _current, services);
            return lease;
        }
    }

    private sealed class InstallationLease(
        IRuntimeThirdPartyAssetLoadingServices installed,
        InstallationLease? previous) : IDisposable
    {
        public IRuntimeThirdPartyAssetLoadingServices Installed { get; } = installed;
        public InstallationLease? Previous { get; } = previous;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            lock (Sync)
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;
                if (!ReferenceEquals(_head, this))
                    return;
                InstallationLease? next = Previous;
                while (next?.IsDisposed == true)
                    next = next.Previous;
                _head = next;
                Volatile.Write(ref _current, next?.Installed ?? Default);
            }
        }
    }

    private sealed class ReflectionServices : IRuntimeThirdPartyAssetLoadingServices
    {
        public XRAsset? Load(
            string filePath,
            string extension,
            Type assetType,
            object? importOptions = null,
            AssetImportContext? importContext = null,
            XRAsset? targetAsset = null)
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

            XRAsset? asset = targetAsset;
            if (asset is not null && !assetType.IsInstanceOfType(asset))
                throw new ArgumentException($"Target asset '{asset.GetType().FullName}' is not assignable to '{assetType.FullName}'.", nameof(targetAsset));

            if (asset is null)
            {
                asset = Activator.CreateInstance(assetType) as XRAsset
                    ?? throw new InvalidOperationException($"Unable to construct third-party asset type '{assetType.FullName}'.");
            }

            asset.OriginalPath = filePath;
            AssetImportContext context = importContext ?? new AssetImportContext(filePath, cacheDirectory: null);
            return asset.Load3rdParty(filePath, importOptions, context) ? asset : null;
        }
    }
}
