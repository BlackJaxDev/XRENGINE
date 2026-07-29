using MemoryPack;
using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Text;
using XREngine.Core.Files;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class PublishedAssetCookingTests
{
    [Test]
    public void PublishedCook_ExcludesSourceAndLauncherAssetsAndProducesRuntimeBinary()
    {
        string tempRoot = CreateTempRoot();
        string assetsDir = Path.Combine(tempRoot, "Assets");
        string intermediateDir = Path.Combine(tempRoot, "Intermediate");
        Directory.CreateDirectory(assetsDir);

        try
        {
            File.WriteAllText(Path.Combine(assetsDir, "startup.asset"), "launcher config, never content");
            File.WriteAllText(Path.Combine(assetsDir, "state.asset"), "launcher state, never content");

            string scriptsDir = Path.Combine(assetsDir, "Scripts");
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(Path.Combine(scriptsDir, "Game.cs"), "internal sealed class Game { }");

            string dataDir = Path.Combine(assetsDir, "Data");
            Directory.CreateDirectory(dataDir);
            string yaml = AssetManager.Serializer.Serialize(new GameStartupSettings());
            yaml = yaml.Contains("__assetType:", StringComparison.Ordinal)
                ? yaml.Replace("__assetType:", "__type:", StringComparison.Ordinal)
                : $"__type: {typeof(GameStartupSettings).FullName}{Environment.NewLine}{yaml}";
            File.WriteAllText(Path.Combine(dataDir, "round.asset"), yaml, Encoding.UTF8);
            File.WriteAllText(Path.Combine(dataDir, "readme.txt"), "shipping data", Encoding.UTF8);

            string cookedDir = ProjectBuilder.PrepareCookedContentDirectoryForTests(
                assetsDir,
                intermediateDir,
                publishLauncherAsNativeAot: true);

            File.Exists(Path.Combine(cookedDir, "startup.asset")).ShouldBeFalse();
            File.Exists(Path.Combine(cookedDir, "state.asset")).ShouldBeFalse();
            File.Exists(Path.Combine(cookedDir, "Scripts", "Game.cs")).ShouldBeFalse();
            File.Exists(Path.Combine(cookedDir, "Data", "readme.txt")).ShouldBeTrue();

            byte[] bytes = File.ReadAllBytes(Path.Combine(cookedDir, "Data", "round.asset"));
            CookedAssetBlob blob = MemoryPackSerializer.Deserialize<CookedAssetBlob>(bytes);
            blob.Format.ShouldBe(CookedAssetFormat.RuntimeBinaryV1);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Test]
    public void PublishedCook_RejectsUncookableYamlInsteadOfShippingAuthoringText()
    {
        string tempRoot = CreateTempRoot();
        string assetsDir = Path.Combine(tempRoot, "Assets");
        string intermediateDir = Path.Combine(tempRoot, "Intermediate");
        Directory.CreateDirectory(assetsDir);

        try
        {
            File.WriteAllText(Path.Combine(assetsDir, "broken.asset"), "Name: MissingTypeHint");

            Should.Throw<InvalidOperationException>(() =>
                ProjectBuilder.PrepareCookedContentDirectoryForTests(
                    assetsDir,
                    intermediateDir,
                    publishLauncherAsNativeAot: true));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
        => Path.Combine(
            NUnit.Framework.TestContext.CurrentContext.WorkDirectory,
            nameof(PublishedAssetCookingTests),
            Guid.NewGuid().ToString("N"));
}
