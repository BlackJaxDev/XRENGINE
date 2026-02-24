using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using XREngine.Core.Files;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class CommonAssetsArchiveTests
{
    private const string ArchiveOutputEnvVar = "XRE_COOK_COMMONASSETS_ARCHIVE";

    [Test]
    public void CookBuildCommonAssets_ToGameContentArchive()
    {
        string repoRoot = FindRepositoryRoot();
        string sourceDirectory = Path.Combine(repoRoot, "Build", "CommonAssets");
        Directory.Exists(sourceDirectory).ShouldBeTrue($"Source directory not found: {sourceDirectory}");

        int sourceFileCount = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Count();
        sourceFileCount.ShouldBeGreaterThan(0, "Build/CommonAssets does not contain any files to cook.");

        string archiveOutputPath = ResolveArchiveOutputPath(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(archiveOutputPath)!);

        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "CommonAssetsCook", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string cookedDirectory = ProjectBuilder.PrepareCookedContentDirectoryForTests(sourceDirectory, tempRoot);

            if (File.Exists(archiveOutputPath))
                File.Delete(archiveOutputPath);

            AssetPacker.Pack(cookedDirectory, archiveOutputPath);

            File.Exists(archiveOutputPath).ShouldBeTrue($"Cooked archive was not created: {archiveOutputPath}");
            new FileInfo(archiveOutputPath).Length.ShouldBeGreaterThan(0, "Cooked archive is empty.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    private static string ResolveArchiveOutputPath(string repoRoot)
    {
        string? configured = Environment.GetEnvironmentVariable(ArchiveOutputEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(repoRoot, "Build", "Game", "Content", "GameContent.pak");

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(repoRoot, configured));
    }

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);

        while (true)
        {
            if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");
    }
}
