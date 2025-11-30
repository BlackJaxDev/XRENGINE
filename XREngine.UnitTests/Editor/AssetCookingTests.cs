using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text;
using XREngine;
using XREngine.Core.Engine;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class AssetCookingTests
{
    [Test]
    public void PrepareCookedContentDirectory_CooksAssetsAndCopiesBinary()
    {
        string tempRoot = Path.Combine(NUnit.Framework.TestContext.CurrentContext.WorkDirectory, "AssetCooking", Guid.NewGuid().ToString("N"));
        string assetsDir = Path.Combine(tempRoot, "Assets");
        string intermediateDir = Path.Combine(tempRoot, "Intermediate");
        Directory.CreateDirectory(assetsDir);
        Directory.CreateDirectory(intermediateDir);

        try
        {
            string assetSubdir = Path.Combine(assetsDir, "Data");
            Directory.CreateDirectory(assetSubdir);

            var startup = new GameStartupSettings
            {
                NetworkingType = GameStartupSettings.ENetworkingType.Server,
                LogOutputToFile = false
            };
            string startupYaml = AssetManager.Serializer.Serialize(startup);
            string startupAssetPath = Path.Combine(assetSubdir, "startup.asset");
            File.WriteAllText(startupAssetPath, startupYaml, Encoding.UTF8);
            byte[] originalAssetBytes = File.ReadAllBytes(startupAssetPath);

            string notesPath = Path.Combine(assetSubdir, "notes.txt");
            byte[] rawNoteBytes = Encoding.UTF8.GetBytes("unit-test-note");
            File.WriteAllBytes(notesPath, rawNoteBytes);

            string cookedDir = ProjectBuilder.PrepareCookedContentDirectoryForTests(assetsDir, intermediateDir);

            string cookedAssetPath = Path.Combine(cookedDir, "Data", "startup.asset");
            string cookedNotesPath = Path.Combine(cookedDir, "Data", "notes.txt");

            File.Exists(cookedAssetPath).ShouldBeTrue();
            File.Exists(cookedNotesPath).ShouldBeTrue();

            byte[] cookedAssetBytes = File.ReadAllBytes(cookedAssetPath);
            cookedAssetBytes.Length.ShouldBeGreaterThan(0);
            cookedAssetBytes.SequenceEqual(originalAssetBytes).ShouldBeFalse();

            byte[] cookedNotesBytes = File.ReadAllBytes(cookedNotesPath);
            cookedNotesBytes.ShouldBe(rawNoteBytes);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
