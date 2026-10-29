using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Platform;

[TestFixture, NonParallelizable]
public sealed class DesktopAssetFileSystemTests
{
    [Test]
    public void MetadataReportsExistingAndMissingFiles()
    {
        string root = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared", "scratch", "asset-metadata-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "source.bin");
            IRuntimeHostFileMetadataBackend files = new DesktopAssetFileSystem();
            Assert.That(files.TryGetFileMetadata(path, out long ticks, out long length), Is.False);
            Assert.That(ticks, Is.Zero);
            Assert.That(length, Is.Zero);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.That(files.TryGetFileMetadata(path, out ticks, out length), Is.True);
            Assert.That(ticks, Is.EqualTo(File.GetLastWriteTimeUtc(path).Ticks));
            Assert.That(length, Is.EqualTo(3));
            File.WriteAllBytes(path, []);
            Assert.That(files.TryGetFileMetadata(path, out _, out length), Is.True);
            Assert.That(length, Is.Zero);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void DiscoveryAndChangeMonitor_UseHostFilesystemAndReleaseWatcher()
    {
        string root = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared", "scratch", "asset-fs-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var fileSystem = new DesktopAssetFileSystem();
            Directory.CreateDirectory(Path.Combine(root, "child"));
            File.WriteAllText(Path.Combine(root, "first.txt"), "first");
            File.WriteAllText(Path.Combine(root, "child", "second.txt"), "second");
            fileSystem.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly).Count().ShouldBe(1);
            fileSystem.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).Count().ShouldBe(2);
            fileSystem.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Count().ShouldBe(1);
            fileSystem.EnumerateFileSystemEntries(root).Count().ShouldBe(2);
            fileSystem.SupportsChangeNotifications.ShouldBeTrue();
            using var created = new ManualResetEventSlim();
            using (IAssetChangeMonitor monitor = fileSystem.CreateChangeMonitor())
            {
                monitor.Path = root;
                monitor.Filter = "*.txt";
                monitor.IncludeSubdirectories = false;
                monitor.NotifyFilter = AssetNotifyFilters.FileName | AssetNotifyFilters.LastWrite;
                monitor.Created += (_, args) => { if (args.Name == "observed.txt") created.Set(); };
                monitor.EnableRaisingEvents = true;
                File.WriteAllText(Path.Combine(root, "observed.txt"), "observed");
                created.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("The desktop adapter must forward native file creation.");
                monitor.EnableRaisingEvents = false;
            }
            Directory.Delete(root, true);
            Directory.Exists(root).ShouldBeFalse();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
