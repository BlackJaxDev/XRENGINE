using NUnit.Framework;
using XREngine.Core.Files;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XREngine.UnitTests.Core;

/// <summary>Checks the staged Windows recovery quota walk at exact boundaries.</summary>
[TestFixture, NonParallelizable]
public sealed class NativeAssetRecoveryCapacityWindowsTests
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 1024;

    [Test]
    public void MetadataOnlyLockEntryCountsWhileItsWriterHandleIsExclusive()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The handle-relative walk is Windows-only.");
        using var scope = new NativeAssetRecoveryTestScope();
        string store = Store(scope);
        Directory.CreateDirectory(store);
        string lockPath = Path.Combine(store, ".writer.lock");
        using FileStream writer = new(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        writer.Flush(true);

        (long bytes, int files) = ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, 0, 0, MaximumBytes, MaximumFiles);

        Assert.That(bytes, Is.Zero);
        Assert.That(files, Is.EqualTo(1));
        Assert.That(writer.Length, Is.Zero);
        Assert.That(File.Exists(lockPath), Is.True);
    }

    [Test]
    public void UnknownFilesCountAtExactFileAndByteQuotaBoundaries()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The handle-relative walk is Windows-only.");
        using var scope = new NativeAssetRecoveryTestScope();
        string store = Store(scope);
        Directory.CreateDirectory(store);
        string unknown = Path.Combine(store, "unknown.data");
        File.WriteAllBytes(unknown, [0x5A]);

        (long bytes, int files) = ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, MaximumBytes - 1, MaximumFiles - 1, MaximumBytes, MaximumFiles);
        Assert.That(bytes, Is.EqualTo(1));
        Assert.That(files, Is.EqualTo(1));

        Assert.Throws<IOException>(() => ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, MaximumBytes, MaximumFiles - 1, MaximumBytes, MaximumFiles));
        Assert.Throws<IOException>(() => ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, MaximumBytes - 1, MaximumFiles, MaximumBytes, MaximumFiles));
        Assert.That(File.ReadAllBytes(unknown), Is.EqualTo(new byte[] { 0x5A }));
    }

    [Test]
    public void CountsAFullPageOfUnknownFilesBeforeRejectingOneMoreReservation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The handle-relative walk is Windows-only.");
        using var scope = new NativeAssetRecoveryTestScope();
        string store = Store(scope);
        string unknown = Path.Combine(store, "unknown-folder");
        Directory.CreateDirectory(unknown);
        for (int index = 0; index < MaximumFiles; index++)
            File.WriteAllBytes(Path.Combine(unknown, $"entry-{index:D4}.bin"), []);

        (long bytes, int files) = ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, 0, 0, MaximumBytes, MaximumFiles);
        Assert.That(bytes, Is.Zero);
        Assert.That(files, Is.EqualTo(MaximumFiles));
        Assert.Throws<IOException>(() => ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, 0, 1, MaximumBytes, MaximumFiles));
    }

    [Test]
    public void RefusesAnEntryWhoseDirectoryHandleCannotBeOpened()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The handle-relative walk is Windows-only.");
        using var scope = new NativeAssetRecoveryTestScope();
        string store = Store(scope);
        string blocked = Path.Combine(store, "blocked-directory");
        Directory.CreateDirectory(blocked);
        using SafeFileHandle held = CreateFileW(blocked, 0x00000001, 0, IntPtr.Zero,
            3, 0x02000000, IntPtr.Zero);
        Assert.That(held.IsInvalid, Is.False, "The isolated directory must be held before the quota check.");

        Assert.Throws<IOException>(() => ((IAssetRecoveryCapacityBackend)AssetFileSystemServices.Required).Count(
            store, 0, 0, MaximumBytes, MaximumFiles));
        Assert.That(Directory.Exists(blocked), Is.True);
    }

    private static string Store(NativeAssetRecoveryTestScope scope)
        => Path.Combine(scope.ProjectRoot, ".xr-native-asset-recovery", "v1");

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
