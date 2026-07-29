using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeRuntimeIdentifierPackagingTests
{
    [Test]
    public void NativeAotRuntimeDependencies_CopyOnlyRequestedRuntimeIdentifier()
    {
        string tempRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            nameof(NativeRuntimeIdentifierPackagingTests),
            Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(tempRoot, "Engine");
        string windowsNativeDirectory = Path.Combine(sourceDirectory, "runtimes", "win-x64", "native");
        string linuxNativeDirectory = Path.Combine(sourceDirectory, "runtimes", "linux-x64", "native");
        string destinationDirectory = Path.Combine(tempRoot, "Game", "Binaries");
        Directory.CreateDirectory(windowsNativeDirectory);
        Directory.CreateDirectory(linuxNativeDirectory);

        try
        {
            File.WriteAllText(Path.Combine(windowsNativeDirectory, "windows-only.dll"), "windows runtime");
            File.WriteAllText(Path.Combine(linuxNativeDirectory, "linux-only.so"), "linux runtime");

            XREngine.Editor.ProjectBuilder.CopyRuntimeDependenciesForTests(
                sourceDirectory,
                destinationDirectory,
                includePdb: false,
                runtimeIdentifier: "win-x64");

            File.ReadAllText(Path.Combine(
                    destinationDirectory,
                    "runtimes",
                    "win-x64",
                    "native",
                    "windows-only.dll"))
                .ShouldBe("windows runtime");
            Directory.Exists(Path.Combine(destinationDirectory, "runtimes", "linux-x64"))
                .ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
