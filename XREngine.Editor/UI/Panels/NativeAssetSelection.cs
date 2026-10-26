using XREngine.Core.Files;

namespace XREngine.Editor;

/// <summary>Loads one selected asset for the native inspector.</summary>
public static class NativeAssetSelection
{
    private static int _revision;
    private static ulong _publicationRevision;

    public static string? SelectedPath { get; private set; }
    public static object? SelectedTarget { get; private set; }
    internal static ulong PublicationRevision => _publicationRevision;
    public static event Action<object?>? SelectionChanged;

    public static void Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        int revision = ++_revision;
        SelectedPath = fullPath;

        Type? type = EditorImGuiUI.ResolveNativeAssetType(fullPath);
        if (type is null)
        {
            Publish(new NativeAssetSelectionStatus(fullPath, "No registered asset type matches this file."));
            return;
        }

        if (!string.Equals(Path.GetExtension(fullPath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
        {
            Publish(new ThirdPartyImportSelection(fullPath, type));
            return;
        }

        var assets = Engine.Assets;
        if (assets is null)
        {
            Publish(new NativeAssetSelectionStatus(fullPath, "The asset manager is unavailable."));
            return;
        }
        if (assets.TryGetAssetByPath(fullPath, out XRAsset? cached))
        {
            Publish(cached);
            return;
        }

        Publish(new NativeAssetSelectionStatus(fullPath, "Loading asset..."));
        _ = LoadAsync(assets, fullPath, type, revision);
    }

    public static void Clear()
    {
        _revision++;
        SelectedPath = null;
        Publish(null);
    }

    private static async Task LoadAsync(AssetManager assets, string path, Type type, int revision)
    {
        XRAsset? asset = null;
        string? failure = null;
        try
        {
            asset = await assets.LoadAsync(path, type).ConfigureAwait(false);
            if (asset is null)
                failure = "The asset loader returned no asset.";
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            Debug.LogException(exception, $"Failed to load selected asset '{path}'.");
        }

        DispatchLoadedAsset(path, revision, asset, failure);
    }

    internal static void DispatchLoadedAsset(string path, int revision, XRAsset? asset, string? failure)
    {
        _ = Engine.InvokeOnMainThread(() =>
        {
            if (revision != _revision || SelectedPath is null || !NativeAssetPathPolicy.PathsEqual(SelectedPath, path))
                return;
            Publish(asset is not null
                ? asset
                : new NativeAssetSelectionStatus(path, failure ?? "Asset loading failed."));
        }, "Native asset selection ready", executeNowIfAlreadyMainThread: true);
    }

    private static void Publish(object? target)
    {
        SelectedTarget = target;
        _publicationRevision = unchecked(_publicationRevision + 1);
        SelectionChanged?.Invoke(target);
    }
}
