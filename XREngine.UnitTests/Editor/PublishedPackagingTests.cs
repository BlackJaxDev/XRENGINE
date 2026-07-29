using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Text;
using XREngine.Core.Files;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class PublishedPackagingTests
{
    [Test]
    public void RuntimeCommonAssets_PackagesCompleteShaderTreeAndManifest()
    {
        string tempRoot = CreateTempRoot();
        string engineAssetsDirectory = Path.Combine(tempRoot, "EngineAssets");
        string intermediateDirectory = Path.Combine(tempRoot, "Intermediate");
        string contentDirectory = Path.Combine(tempRoot, "Content");
        string includeDirectory = Path.Combine(engineAssetsDirectory, "Shaders", "Common");
        Directory.CreateDirectory(includeDirectory);
        File.WriteAllText(Path.Combine(engineAssetsDirectory, "Shaders", "Scene.fs"), "void main() {}");
        File.WriteAllText(Path.Combine(includeDirectory, "Math.glsl"), "float saturate(float value) { return clamp(value, 0.0, 1.0); }");

        try
        {
            string archivePath = ProjectBuilder.PackageRuntimeShaderAssetsForTests(
                engineAssetsDirectory,
                intermediateDirectory,
                contentDirectory);

            File.Exists(archivePath).ShouldBeTrue();
            string[] assetPaths = [.. AssetPacker.GetAssetPaths(archivePath)];
            assetPaths.ShouldContain("manifest.json");
            assetPaths.ShouldContain("Shaders/Scene.fs");
            assetPaths.ShouldContain("Shaders/Common/Math.glsl");

            string manifest = Encoding.UTF8.GetString(AssetPacker.GetAsset(archivePath, "manifest.json"));
            manifest.ShouldContain("\"scope\":\"runtime-shaders\"");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    [Test]
    public void NativeAotArtifacts_CopyRuntimeDependenciesAndExcludeDiagnostics()
    {
        string tempRoot = CreateTempRoot();
        string sourceDirectory = Path.Combine(tempRoot, "Publish");
        string destinationDirectory = Path.Combine(tempRoot, "Game", "Binaries");
        Directory.CreateDirectory(sourceDirectory);

        try
        {
            string sourceExePath = Path.Combine(sourceDirectory, "GeneratedLauncher.exe");
            File.WriteAllText(sourceExePath, "native executable");
            File.WriteAllText(Path.Combine(sourceDirectory, "runtime-native.dll"), "native dependency");
            File.WriteAllText(Path.Combine(sourceDirectory, "GeneratedLauncher.pdb"), "symbols");
            File.WriteAllText(Path.Combine(sourceDirectory, "aot-publish.log"), "diagnostics");
            File.WriteAllText(Path.Combine(sourceDirectory, "aot-publish-warnings.md"), "diagnostics");
            File.WriteAllText(Path.Combine(sourceDirectory, "runtime.license.txt"), "license");

            string nativeLibraryDirectory = Path.Combine(sourceDirectory, "lib", "x64");
            Directory.CreateDirectory(nativeLibraryDirectory);
            File.WriteAllText(Path.Combine(nativeLibraryDirectory, "nested-native.dll"), "nested dependency");

            ProjectBuilder.CopyLauncherArtifactsForTests(
                sourceExePath,
                destinationDirectory,
                "MonkeyBallVR.exe",
                includePdb: false,
                isNativeAot: true);

            File.ReadAllText(Path.Combine(destinationDirectory, "MonkeyBallVR.exe")).ShouldBe("native executable");
            File.Exists(Path.Combine(destinationDirectory, "GeneratedLauncher.exe")).ShouldBeFalse();
            File.Exists(Path.Combine(destinationDirectory, "runtime-native.dll")).ShouldBeTrue();
            File.Exists(Path.Combine(destinationDirectory, "lib", "x64", "nested-native.dll")).ShouldBeTrue();
            File.Exists(Path.Combine(destinationDirectory, "runtime.license.txt")).ShouldBeTrue();
            File.Exists(Path.Combine(destinationDirectory, "GeneratedLauncher.pdb")).ShouldBeFalse();
            File.Exists(Path.Combine(destinationDirectory, "aot-publish.log")).ShouldBeFalse();
            File.Exists(Path.Combine(destinationDirectory, "aot-publish-warnings.md")).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
        => Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            nameof(PublishedPackagingTests),
            Guid.NewGuid().ToString("N"));
}
