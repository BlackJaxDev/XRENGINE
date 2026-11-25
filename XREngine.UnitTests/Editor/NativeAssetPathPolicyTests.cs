using NUnit.Framework;
using Shouldly;

namespace XREngine.Editor;

[TestFixture]
public class NativeAssetPathPolicyTests
{
    [Test]
    public void MutationPath_RejectsSiblingAndRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeAssetPathPolicy", "Assets");
        string child = Path.Combine(root, "Nested", "item.asset");
        NativeAssetPathPolicy.RequireUnderRoot(root, child, allowRoot: false).ShouldBe(Path.GetFullPath(child));
        Should.Throw<InvalidOperationException>(() =>
            NativeAssetPathPolicy.RequireUnderRoot(root, root, allowRoot: false));
        Should.Throw<InvalidOperationException>(() =>
            NativeAssetPathPolicy.RequireUnderRoot(root, root + "-other/item.asset", allowRoot: false));
    }

    [Test]
    public void MutationPath_RejectsLinkedDirectory()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "NativeAssetPathPolicy", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(sandbox, "Assets");
        string outside = Path.Combine(sandbox, "Outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(root, "Linked");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Ignore($"Directory links are unavailable: {exception.Message}");
                return;
            }
            Should.Throw<InvalidOperationException>(() =>
                NativeAssetPathPolicy.RequireUnderRoot(root, Path.Combine(link, "item.asset"), allowRoot: false));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Test]
    public void MutationPath_RecheckRejectsDirectoryChangedToLink()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "NativeAssetPathPolicy", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(sandbox, "Assets");
        string outside = Path.Combine(sandbox, "Outside");
        string folder = Path.Combine(root, "Destination");
        string path = Path.Combine(folder, "item.asset");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        try
        {
            NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot: false).ShouldBe(Path.GetFullPath(path));
            Directory.Delete(folder);
            try { Directory.CreateSymbolicLink(folder, outside); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Ignore($"Directory links are unavailable: {exception.Message}");
                return;
            }
            Should.Throw<InvalidOperationException>(() =>
                NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot: false));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Test]
    public void RenameEquality_UsesPlatformCaseRules()
    {
        bool equal = NativeAssetPathPolicy.PathsEqual("/Assets/Item.asset", "/Assets/item.asset");
        equal.ShouldBe(OperatingSystem.IsWindows());
    }

    [TestCase("../outside")]
    [TestCase("foo/bar")]
    [TestCase("file?.asset")]
    public void MutationName_RejectsInvalidPaths(string name)
        => Should.Throw<ArgumentException>(() => NativeAssetPathPolicy.ValidateName(name));
}
