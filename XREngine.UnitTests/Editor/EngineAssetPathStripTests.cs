using System.Text;
using ImageMagick;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.Fbx;
using XREngine.UnitTests.Cutscenes;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// Runs the scan and strip path of a full common-assets publish on the real engine asset folder. The stripped copies of the
/// engine files that hold user profile paths (an EXR sky and four Sponza FBX models) must lose the paths and still load with
/// the engine FBX parser and the EXR decoder. The test reads several GB, so it runs only when it is selected.
/// </summary>
[TestFixture, NonParallelizable, Explicit("Reads and copies several GB of engine LFS assets.")]
public sealed class EngineAssetPathStripTests
{
    private static readonly string[] KnownFiles =
    [
        "Textures/overcast_soil_puresky_4k.exr",
        "Models/main1_sponza/NewSponza_Main_Yup_003.fbx",
        "Models/main1_sponza/NewSponza_Main_Zup_003.fbx",
        "Models/pkg_a_curtains/NewSponza_Curtains_FBX_YUp.fbx",
        "Models/pkg_a_curtains/NewSponza_Curtains_FBX_ZUp.fbx",
    ];

    [Test]
    public void EngineAssetFolder_StripsUserPaths_WithAWarningList_AndTheFilesStillLoad()
    {
        string engine = FindEngineAssets();
        foreach (string known in KnownFiles)
        {
            FileInfo info = new(Path.Combine(engine, known));
            Assume.That(info.Exists && info.Length > 1024 * 1024, $"'{known}' is missing or is a Git LFS pointer.");
        }

        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "EngineAssetPathStrip", Guid.NewGuid().ToString("N"));
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(engine));
            using PublishPathRedactionLog log = PublishPathRedactionLog.Begin();

            Dictionary<string, string> copies = ProjectBuilder.CreateStrippedCopies(engine, Path.Combine(root, "Stripped"));

            string report = log.WriteReport(Path.Combine(root, "Intermediate", "Build", "Logs", "publish-report.txt"));
            string output = Environment.GetEnvironmentVariable("SCAN_OUT") ?? root;
            File.Copy(report, Path.Combine(output, "engine-assets-strip-report.txt"), overwrite: true);
            TestContext.Out.WriteLine(File.ReadAllText(report));

            foreach (string known in KnownFiles)
            {
                string source = Path.GetFullPath(Path.Combine(engine, known));
                copies.ShouldContainKey(source, known);
                string copy = copies[source];
                new FileInfo(copy).Length.ShouldBe(new FileInfo(source).Length, known);
                CookedOutputPathGuard.ContainsMachinePathInCopiedFile(copy, copy).ShouldBeFalse(known);
                log.Entries.ShouldContain(entry => entry.File == known, known);
            }

            foreach ((string source, string copy) in copies)
                CookedOutputPathGuard.ContainsMachinePathInCopiedFile(copy, copy).ShouldBeFalse(source);

            // Every stripped EXR keeps its pixels, and every stripped FBX still loads, not only the known files.
            foreach ((string source, string copy) in copies)
            {
                string extension = Path.GetExtension(source);
                if (string.Equals(extension, ".exr", StringComparison.OrdinalIgnoreCase))
                    AssertExrPixelsUnchanged(source, copy);
                else if (string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    AssertFbxStillLoads(source, copy);
                    AssertFileNamesKept(source, copy, log.Entries);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Checks that the file names of the stripped paths of a file stay: each name occurs as often in the stripped copy as in
    /// the source, so an importer can still find a texture by its name.
    /// </summary>
    private static void AssertFileNamesKept(string source, string copy, IReadOnlyList<(string File, string Item)> entries)
    {
        // A name that is also a folder part of another stripped path (for example an .fbm folder) is overwritten there, so
        // only names that are never a folder part are counted.
        string label = Path.GetFileName(source);
        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<string> folders = new(StringComparer.Ordinal);
        foreach ((string file, string item) in entries)
        {
            if (!file.EndsWith(label, StringComparison.Ordinal))
                continue;
            string[] parts = item.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
                folders.Add(parts[i]);
            if (parts.Length > 0 && parts[^1].Contains('.', StringComparison.Ordinal))
                names.Add(parts[^1]);
        }

        names.ExceptWith(folders);

        names.Count.ShouldBeGreaterThan(0, label);
        byte[][] needles = [.. names.Select(static name => Encoding.UTF8.GetBytes(name))];
        long[] sourceCounts = CountOccurrences(source, needles);
        long[] copyCounts = CountOccurrences(copy, needles);
        copyCounts.ShouldBe(sourceCounts, label);
    }

    private static long[] CountOccurrences(string file, byte[][] needles)
    {
        const int chunk = 4 * 1024 * 1024;
        const int overlap = 4096;
        long[] counts = new long[needles.Length];
        byte[] buffer = new byte[chunk + overlap];
        using FileStream stream = File.OpenRead(file);
        int carried = 0;
        while (true)
        {
            int read = stream.ReadAtLeast(buffer.AsSpan(carried, chunk), chunk, throwOnEndOfStream: false);
            int length = carried + read;
            for (int i = 0; i < needles.Length; i++)
            {
                ReadOnlySpan<byte> span = buffer.AsSpan(0, length);
                int offset = 0;
                int index;
                while ((index = span[offset..].IndexOf(needles[i])) >= 0)
                {
                    // A match that ends in the carried bytes was counted in the chunk before.
                    if (offset + index + needles[i].Length > carried)
                        counts[i]++;
                    offset += index + 1;
                }
            }

            if (read < chunk)
                return counts;
            carried = Math.Min(overlap, length);
            buffer.AsSpan(length - carried, carried).CopyTo(buffer);
        }
    }

    private static void AssertExrPixelsUnchanged(string source, string copy)
    {
        using MagickImage original = new(source);
        using MagickImage stripped = new(copy);
        stripped.Width.ShouldBe(original.Width);
        stripped.Height.ShouldBe(original.Height);
        original.Compare(stripped, ErrorMetric.Absolute).ShouldBe(0d);
    }

    private static void AssertFbxStillLoads(string source, string copy)
    {
        int sourceNodes;
        int sourceProperties;
        using (FbxStructuralDocument original = FbxStructuralParser.ParseFile(source))
        {
            sourceNodes = original.Nodes.Count;
            sourceProperties = original.Properties.Count;
        }

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(copy);
        structural.Nodes.Count.ShouldBe(sourceNodes, copy);
        structural.Properties.Count.ShouldBe(sourceProperties, copy);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        geometry.MeshesByObjectId.Count.ShouldBeGreaterThan(0, copy);
    }

    private static string FindEngineAssets()
    {
        string? directory = TestContext.CurrentContext.WorkDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "Build", "CommonAssets");
            if (Directory.Exists(candidate))
                return candidate;
            directory = Path.GetDirectoryName(directory);
        }

        Assert.Ignore("The engine asset folder was not found.");
        return string.Empty;
    }
}
