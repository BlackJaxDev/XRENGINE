using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MeshOptimizerNativeLibraryProbeTests
{
    private string _root = string.Empty;

    [SetUp]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "xr-meshopt-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void FindNativeLibraryFile_RidSpecificPublishLayout_ReturnsAppRootFile()
    {
        string file = Touch(Path.Combine(_root, "meshoptimizer.dll"));

        NativeMeshOptimizer.FindNativeLibraryFile(_root, _root, "win-x64", "meshoptimizer.dll").ShouldBe(file);
    }

    [Test]
    public void FindNativeLibraryFile_RuntimesLayout_ReturnsNativeFolderFile()
    {
        string file = Touch(Path.Combine(_root, "runtimes", "win-x64", "native", "meshoptimizer.dll"));

        NativeMeshOptimizer.FindNativeLibraryFile(_root, _root, "win-x64", "meshoptimizer.dll").ShouldBe(file);
    }

    [Test]
    public void FindNativeLibraryFile_BothLayouts_PrefersAppRoot()
    {
        string root = Touch(Path.Combine(_root, "meshoptimizer.dll"));
        Touch(Path.Combine(_root, "runtimes", "win-x64", "native", "meshoptimizer.dll"));

        NativeMeshOptimizer.FindNativeLibraryFile(_root, _root, "win-x64", "meshoptimizer.dll").ShouldBe(root);
    }

    [Test]
    public void FindNativeLibraryFile_NoFile_ReturnsNull()
        => NativeMeshOptimizer.FindNativeLibraryFile(_root, _root, "win-x64", "meshoptimizer.dll").ShouldBeNull();

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }
}
