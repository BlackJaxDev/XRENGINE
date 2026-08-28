using System.Runtime.CompilerServices;
using XREngine.Scene.Prefabs;
using YamlDotNet.Serialization;

namespace XREngine.Editor.Importers.SerializedAssets;

/// <summary>
/// Retains Unity authoring manifests outside the runtime-neutral prefab asset and
/// publishes them as a sidecar only after the prefab root is safely available.
/// </summary>
public static class SerializedPrefabImportManifestStore
{
    private const string SidecarSuffix = ".unity-import.yaml";
    private static readonly ConditionalWeakTable<XRPrefabSource, Holder> Manifests = new();
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Associates editor-only import evidence with a loaded prefab.</summary>
    public static void Set(XRPrefabSource prefab, SerializedPrefabImportManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        Manifests.Remove(prefab);
        if (manifest is not null)
            Manifests.Add(prefab, new Holder(manifest));
    }

    /// <summary>Gets the editor-only import evidence associated with a prefab.</summary>
    public static bool TryGet(XRPrefabSource prefab, out SerializedPrefabImportManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        if (Manifests.TryGetValue(prefab, out Holder? holder))
        {
            manifest = holder.Manifest;
            return true;
        }

        manifest = null;
        return false;
    }

    /// <summary>Returns the deterministic sidecar path for a prefab root asset.</summary>
    public static string GetSidecarPath(string rootAssetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAssetPath);
        return Path.GetFullPath(rootAssetPath) + SidecarSuffix;
    }

    /// <summary>
    /// Loads persisted editor evidence after the runtime-neutral prefab root has loaded.
    /// </summary>
    public static bool TryLoadAfterRoot(XRPrefabSource prefab, string rootAssetPath)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        string sidecarPath = GetSidecarPath(rootAssetPath);
        if (!File.Exists(sidecarPath))
            return false;

        using var reader = File.OpenText(sidecarPath);
        SerializedPrefabImportManifest manifest = Deserializer.Deserialize<SerializedPrefabImportManifest>(reader)
            ?? throw new InvalidDataException($"Unity prefab manifest sidecar '{sidecarPath}' was empty.");
        Set(prefab, manifest);
        return true;
    }

    /// <summary>
    /// Saves the manifest only after the prefab root has been published. The temporary
    /// file is created beside the sidecar so replacement is volume-local and atomic.
    /// </summary>
    public static void SaveAfterRootPublication(XRPrefabSource prefab, string rootAssetPath)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        string normalizedRootPath = Path.GetFullPath(rootAssetPath);
        if (!File.Exists(normalizedRootPath))
        {
            throw new InvalidOperationException(
                $"Unity prefab manifest sidecars may be saved only after root publication: '{normalizedRootPath}'.");
        }
        if (!TryGet(prefab, out SerializedPrefabImportManifest? manifest) || manifest is null)
            return;

        manifest.OutputAssetPath = normalizedRootPath;
        string sidecarPath = GetSidecarPath(normalizedRootPath);
        string? directory = Path.GetDirectoryName(sidecarPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Unity prefab sidecar '{sidecarPath}' has no parent directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(sidecarPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new StreamWriter(temporaryPath))
                Serializer.Serialize(writer, manifest);

            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Moves a sidecar only after its destination root has been published, preventing
    /// a live root from temporarily losing its matching editor evidence.
    /// </summary>
    public static void MoveAfterRootPublication(string previousRootAssetPath, string currentRootAssetPath)
    {
        string currentRoot = Path.GetFullPath(currentRootAssetPath);
        if (!File.Exists(currentRoot))
        {
            throw new InvalidOperationException(
                $"Unity prefab manifest sidecars may be moved only after destination root publication: '{currentRoot}'.");
        }

        string previousSidecar = GetSidecarPath(previousRootAssetPath);
        if (!File.Exists(previousSidecar))
            return;

        string currentSidecar = GetSidecarPath(currentRoot);
        File.Move(previousSidecar, currentSidecar, overwrite: true);
    }

    /// <summary>
    /// Deletes a sidecar only after its root was removed, preventing removal of
    /// editor evidence while a live root may still require it.
    /// </summary>
    public static void DeleteAfterRootRemoval(string rootAssetPath)
    {
        string normalizedRootPath = Path.GetFullPath(rootAssetPath);
        if (File.Exists(normalizedRootPath))
        {
            throw new InvalidOperationException(
                $"Unity prefab manifest sidecars may be deleted only after root removal: '{normalizedRootPath}'.");
        }

        string sidecarPath = GetSidecarPath(normalizedRootPath);
        if (File.Exists(sidecarPath))
            File.Delete(sidecarPath);
    }

    private sealed record Holder(SerializedPrefabImportManifest Manifest);
}
