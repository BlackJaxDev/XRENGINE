using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// The incremental archive repack removes the separate LOD entries (<c>lod/&lt;key&gt;</c>) that no cooked file refers to.
/// The test transform acts as a sub-mesh cook: each source file lists the LOD keys that its cooked form refers to.
/// </summary>
[TestFixture]
public sealed class IncrementalArchiveLodEntryTests
{
    private string _root = string.Empty;
    private string _source = string.Empty;
    private string _intermediate = string.Empty;
    private string _archive = string.Empty;
    private DateTime _nextWriteTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "IncrementalArchiveLod", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "Assets");
        _intermediate = Path.Combine(_root, "Intermediate");
        _archive = Path.Combine(_root, "Out", "Content.pak");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_intermediate);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Copies the file and writes one LOD entry for each key that the file lists, as a sub-mesh cook does.</summary>
    private static void CookWithLodEntries(string sourcePath, string destinationPath)
    {
        File.Copy(sourcePath, destinationPath, overwrite: true);
        SubMeshLodPayloadCookSink sink = SubMeshLodPayloadCookSink.Current!;
        foreach (string key in File.ReadAllText(sourcePath).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string entryName = SubMeshLodDescriptor.EntryPrefix + key;
            sink.Write(entryName, [1, 2, 3]);
            sink.RecordReference(entryName);
        }
    }

    private void WriteSource(string name, string keys)
    {
        string path = Path.Combine(_source, name);
        File.WriteAllText(path, keys);

        // Each write gets a new time, so the repack always sees the change.
        File.SetLastWriteTimeUtc(path, _nextWriteTime);
        _nextWriteTime = _nextWriteTime.AddMinutes(1);
    }

    private void Repack()
        => ProjectBuilder.RepackArchiveIncrementalForTests(_source, _archive, _intermediate, CookWithLodEntries);

    private string[] LodEntries()
    {
        List<string> entries = [];
        foreach (var entry in AssetPacker.ReadArchiveInfo(_archive).Entries)
            if (entry.Path.StartsWith(SubMeshLodDescriptor.EntryPrefix, StringComparison.Ordinal))
                entries.Add(entry.Path);
        entries.Sort(StringComparer.Ordinal);
        return [.. entries];
    }

    [Test]
    public void Repack_RemovesTheLodEntriesThatNoCookedFileRefersTo()
    {
        WriteSource("a.mesh", "k1,k2");
        WriteSource("b.mesh", "k2");
        Repack();
        LodEntries().ShouldBe(["lod/k1", "lod/k2"]);

        // The key of a LOD changes: the old entry goes, and the entry that another file shares stays.
        WriteSource("a.mesh", "k3");
        Repack();
        LodEntries().ShouldBe(["lod/k2", "lod/k3"]);

        // A deleted file takes its entries with it.
        File.Delete(Path.Combine(_source, "b.mesh"));
        Repack();
        LodEntries().ShouldBe(["lod/k3"]);
    }

    [Test]
    public void Repack_WithNoLedger_KeepsAllLodEntries()
    {
        WriteSource("a.mesh", "k1");
        WriteSource("b.mesh", "k2");
        Repack();

        File.Delete(ProjectBuilder.GetRepackLedgerPathForTests(_intermediate));
        WriteSource("a.mesh", "k3");
        Repack();

        // Without the ledger, the build cannot know the entries of b.mesh, so it removes no LOD entry.
        LodEntries().ShouldBe(["lod/k1", "lod/k2", "lod/k3"]);
    }
}
