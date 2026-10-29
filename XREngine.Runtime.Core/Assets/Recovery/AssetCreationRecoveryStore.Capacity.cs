using XREngine.Core.Files;

namespace XREngine.Assets.Recovery;

public sealed partial class AssetCreationRecoveryStore
{
    private IAssetFileSystem? _capacityFileSystem;
    private IAssetRecoveryCapacityBackend? _capacityBackend;
    private long _capacityBackendGeneration;

    private void CaptureCapacityBackend()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!AssetFileSystemServices.TryCapture(out IAssetFileSystem? fileSystem, out long generation) ||
            fileSystem is not IAssetRecoveryCapacityBackend capacity)
            throw new IOException("AssetRecovery.CapacityUnavailable: The installed file system cannot inspect recovery capacity.");
        _capacityFileSystem = fileSystem;
        _capacityBackend = capacity;
        _capacityBackendGeneration = generation;
        RequireCapacityBackend();
    }

    private void RequireCapacityBackend()
    {
        if (OperatingSystem.IsWindows() && (_capacityBackend is null ||
            !AssetFileSystemServices.IsCurrent(_capacityFileSystem, _capacityBackendGeneration)))
            throw new IOException("AssetRecovery.CapacityChanged: The recovery file-system installation changed.");
    }

    private (long Bytes, int Files) CountRetainedFiles()
    {
        RequireCapacityBackend();
        if (!Directory.Exists(Root))
            return (0, 0);
        if (OperatingSystem.IsWindows())
        {
            // Opening an over-quota store must still expose its recovery records.
            // Prepare applies the configured quota after this fresh count.
            var totals = _capacityBackend!.Count(Root, 0, 0, long.MaxValue, int.MaxValue);
            RequireCapacityBackend();
            return totals;
        }

        long bytes = 0;
        int count = 0;
        Stack<string> pending = new();
        pending.Push(Root);
        while (pending.TryPop(out string? directory))
        {
            RequireUnlinked(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RequireUnlinked(entry);
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else
                {
                    bytes = checked(bytes + new FileInfo(entry).Length);
                    count = checked(count + 1);
                }
            }
        }
        return (bytes, count);
    }
}
