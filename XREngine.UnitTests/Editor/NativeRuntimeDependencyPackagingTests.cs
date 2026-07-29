using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeRuntimeDependencyPackagingTests
{
    [Test]
    public void NativeAotRuntimeDependencies_PreserveRuntimePathsAndExcludeSymbols()
    {
        string tempRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            nameof(NativeRuntimeDependencyPackagingTests),
            Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(tempRoot, "Engine");
        string nativeDirectory = Path.Combine(sourceDirectory, "runtimes", "win-x64", "native");
        string destinationDirectory = Path.Combine(tempRoot, "Game", "Binaries");
        Directory.CreateDirectory(nativeDirectory);

        try
        {
            File.WriteAllText(Path.Combine(nativeDirectory, "libmagicphysx.dll"), "native dependency");
            File.WriteAllText(Path.Combine(nativeDirectory, "libmagicphysx.pdb"), "symbols");

            XREngine.Editor.ProjectBuilder.CopyRuntimeDependenciesForTests(
                sourceDirectory,
                destinationDirectory,
                includePdb: false);

            string packagedNativeDirectory = Path.Combine(
                destinationDirectory,
                "runtimes",
                "win-x64",
                "native");
            File.ReadAllText(Path.Combine(packagedNativeDirectory, "libmagicphysx.dll"))
                .ShouldBe("native dependency");
            File.Exists(Path.Combine(packagedNativeDirectory, "libmagicphysx.pdb"))
                .ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    [Test]
    public void NativeAotHostDependencies_CopyKnownNativeLibrariesOnly()
    {
        string tempRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            nameof(NativeRuntimeDependencyPackagingTests),
            Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(tempRoot, "Engine");
        string destinationDirectory = Path.Combine(tempRoot, "Game", "Binaries");
        Directory.CreateDirectory(sourceDirectory);

        try
        {
            File.WriteAllText(Path.Combine(sourceDirectory, "openvr_api.dll"), "openvr");
            File.WriteAllText(Path.Combine(sourceDirectory, "OVRLipSync.dll"), "lip sync");
            File.WriteAllText(Path.Combine(sourceDirectory, "RestirGI.Native.dll"), "restir");
            File.WriteAllText(Path.Combine(sourceDirectory, "ManagedLibrary.dll"), "managed");

            XREngine.Editor.ProjectBuilder.CopyNativeHostDependenciesForTests(
                sourceDirectory,
                destinationDirectory);

            File.ReadAllText(Path.Combine(destinationDirectory, "openvr_api.dll"))
                .ShouldBe("openvr");
            File.ReadAllText(Path.Combine(destinationDirectory, "OVRLipSync.dll"))
                .ShouldBe("lip sync");
            File.ReadAllText(Path.Combine(destinationDirectory, "RestirGI.Native.dll"))
                .ShouldBe("restir");
            File.Exists(Path.Combine(destinationDirectory, "ManagedLibrary.dll"))
                .ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
