namespace XREngine.Editor.HotReload;

/// <summary>
/// Prevents a collectible generation from binding a process-resident native library
/// with the same name but different bits. Native libraries cannot be unloaded safely.
/// </summary>
internal static class RendererNativeDependencyRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, NativeIdentity> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static void ValidateAndRecord(string path)
    {
        string name = Path.GetFileName(path);
        string hash = RendererBackendModuleLoader.ComputeFileHash(path);
        lock (Sync)
        {
            if (Loaded.TryGetValue(name, out NativeIdentity? existing))
            {
                if (!string.Equals(existing.Hash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileLoadException(
                        $"Native renderer dependency '{name}' is already process-resident from '{existing.Path}' with hash {existing.Hash}; candidate '{path}' has incompatible hash {hash}. Restart the editor to change native binaries.");
                }

                return;
            }

            Loaded.Add(name, new(Path.GetFullPath(path), hash));
        }
    }

    private sealed record NativeIdentity(string Path, string Hash);
}
