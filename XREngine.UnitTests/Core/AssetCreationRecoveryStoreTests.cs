using System.Text;
using System.Text.Json;
using NUnit.Framework;
using XREngine.Assets.Recovery;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class AssetCreationRecoveryStoreTests
{
    private string _project = null!;
    private string _asset = null!;
    private string _meta = null!;

    [SetUp]
    public void SetUp()
    {
        _project = Path.Combine(Path.GetTempPath(), "xre-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_project, "Assets"));
        _asset = Path.Combine(_project, "Assets", "New.asset");
        _meta = Path.Combine(_project, "Metadata", "New.asset.meta");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_project, recursive: true); }
        catch (IOException) { }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private AssetCreationRecoveryFile[] Pair(string asset = "asset", string meta = "meta")
        => [new(_asset, Bytes(asset)), new(_meta, Bytes(meta))];

    [Test]
    public void PublishRetainAndRepublishPreserveExactBytes()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());

        Assert.That(File.Exists(_asset), Is.False);
        store.Publish(operation);
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("asset"));
        Assert.That(File.ReadAllText(_meta), Is.EqualTo("meta"));
        Assert.That(operation.State, Is.EqualTo(EAssetCreationRecoveryState.Published));

        store.Retain(operation);
        Assert.That(File.Exists(_asset) || File.Exists(_meta), Is.False);
        Assert.That(operation.State, Is.EqualTo(EAssetCreationRecoveryState.Retained));

        store.Publish(operation);
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("asset"));
        Assert.That(File.ReadAllText(_meta), Is.EqualTo("meta"));
        Assert.That(File.ReadAllText(operation.OriginalPath(0)), Is.EqualTo("asset"),
            "The immutable copy is never consumed.");
    }

    [Test]
    public void ReopenedStoreReadsTheSameState()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Publish(operation);
        store.Retain(operation);

        var reopened = AssetCreationRecoveryStore.Open(_project);
        var read = reopened.GetOperations().Single();

        Assert.That(read.Id, Is.EqualTo(operation.Id));
        Assert.That(read.State, Is.EqualTo(EAssetCreationRecoveryState.Retained));
        Assert.That(read.Sequence, Is.EqualTo(operation.Sequence));
        Assert.That(read.PublicPaths, Is.EqualTo(new[] { _asset, _meta }));
    }

    [Test]
    public void PrepareRefusesAnExistingPublicPath()
    {
        File.WriteAllText(_asset, "user file");
        var store = AssetCreationRecoveryStore.Open(_project);

        Assert.Throws<IOException>(() => store.Prepare(Guid.NewGuid(), Pair()));
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("user file"));
        Assert.That(store.GetOperations(), Is.Empty);
    }

    [Test]
    public void PrepareRefusesPathsOutsideTheProjectAndInsideTheStore()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        string outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".asset");
        string inside = Path.Combine(_project, AssetCreationRecoveryStore.RootDirectoryName, "x.asset");

        Assert.Throws<ArgumentException>(() => store.Prepare(Guid.NewGuid(), [new(outside, Bytes("a"))]));
        Assert.Throws<ArgumentException>(() => store.Prepare(Guid.NewGuid(), [new(inside, Bytes("a"))]));
        Assert.That(File.Exists(outside), Is.False);
    }

    [Test]
    public void RepublishRefusesAnOccupiedPathAndKeepsTheReceipt()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Publish(operation);
        store.Retain(operation);
        File.WriteAllText(_asset, "newer user file");

        Assert.Throws<IOException>(() => store.Publish(operation));
        Assert.That(operation.State, Is.EqualTo(EAssetCreationRecoveryState.Retained));
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("newer user file"));
        Assert.That(File.Exists(_meta), Is.False);
    }

    [Test]
    public void ForeignChangeBeforeRetainReleasesTheReceiptAndKeepsFiles()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Publish(operation);
        File.WriteAllText(_asset, "edited");

        Assert.Throws<IOException>(() => store.Retain(operation));
        Assert.That(operation.State, Is.EqualTo(EAssetCreationRecoveryState.Released));
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("edited"));
        Assert.That(File.ReadAllText(_meta), Is.EqualTo("meta"));
    }

    [Test]
    public void InterruptedPublishRequiresRecoveryAndProtectsPathsWithoutWrites()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        AppendRecord(operation, EAssetCreationRecoveryState.PublishPending);
        File.Move(operation.WorkingPath(0), _asset);
        string journal = File.ReadAllText(Path.Combine(operation.Directory, "journal.jsonl"));

        var reopened = AssetCreationRecoveryStore.Open(_project);
        var read = reopened.GetOperations().Single();

        Assert.That(read.State, Is.EqualTo(EAssetCreationRecoveryState.RecoveryRequired));
        Assert.That(read.Problem, Does.Contain("PublishPending"));
        Assert.That(reopened.IsProtected(_asset), Is.True);
        Assert.That(reopened.IsProtected(_meta), Is.True);
        Assert.That(reopened.IsProtected(Path.Combine(_project, "Assets")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(operation.Directory, "journal.jsonl")), Is.EqualTo(journal),
            "Opening a store writes nothing.");
        Assert.Throws<InvalidOperationException>(() => reopened.Publish(read));
        Assert.Throws<InvalidOperationException>(() => reopened.Retain(read));
        Assert.That(reopened.RemoveReleased(read), Is.False);
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("asset"));
    }

    [Test]
    public void AcceptedRecoveryReleasesProtectionAndAllowsCleanup()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        AppendRecord(operation, EAssetCreationRecoveryState.PublishPending);
        var reopened = AssetCreationRecoveryStore.Open(_project);
        var read = reopened.GetOperations().Single();

        reopened.AcceptRecovery(read, "Kept the staged files.");

        Assert.That(read.State, Is.EqualTo(EAssetCreationRecoveryState.Released));
        Assert.That(reopened.IsProtected(_asset), Is.False);
        Assert.That(reopened.RemoveReleased(read), Is.True);
        Assert.That(Directory.Exists(read.Directory), Is.False);
    }

    [Test]
    public void MalformedManifestRequiresRecoveryAndCannotAuthorizeWrites()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        File.WriteAllText(Path.Combine(operation.Directory, "manifest.json"), "{ not json");

        var reopened = AssetCreationRecoveryStore.Open(_project);
        var read = reopened.GetOperations().Single();

        Assert.That(read.State, Is.EqualTo(EAssetCreationRecoveryState.RecoveryRequired));
        Assert.That(read.Manifest, Is.Null);
        Assert.Throws<InvalidOperationException>(() => reopened.Publish(read));
        Assert.Throws<InvalidOperationException>(() => reopened.AcceptRecovery(read, "accept"));
        Assert.That(Directory.Exists(read.Directory), Is.True);
    }

    [Test]
    public void ChangedImmutableCopyRequiresRecovery()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Publish(operation);
        File.WriteAllText(operation.OriginalPath(1), "tampered");

        var read = AssetCreationRecoveryStore.Open(_project).GetOperations().Single();

        Assert.That(read.State, Is.EqualTo(EAssetCreationRecoveryState.RecoveryRequired));
        Assert.That(read.Problem, Does.Contain("immutable"));
    }

    [Test]
    public void NormalEditReleasesPublishedReceipt()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Publish(operation);

        Assert.That(store.ReleasePublishedFor(Path.Combine(_project, "Assets"), "Moved"), Is.EqualTo(1));
        Assert.That(operation.State, Is.EqualTo(EAssetCreationRecoveryState.Released));
        Assert.Throws<InvalidOperationException>(() => store.Retain(operation));
        Assert.That(File.ReadAllText(_asset), Is.EqualTo("asset"));
    }

    [Test]
    public void RetainedPathCannotBeClaimedByAnotherCreation()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Publish(operation);
        store.Retain(operation);

        Assert.Throws<IOException>(() => store.Prepare(Guid.NewGuid(), Pair("other", "other meta")));
        store.Release(operation);
        Assert.That(() => store.Prepare(Guid.NewGuid(), Pair("other", "other meta")), Throws.Nothing);
    }

    [Test]
    public void QuotaCountsReservationsAndExistingFiles()
    {
        var store = AssetCreationRecoveryStore.Open(_project, maximumBytes: 6000, maximumFiles: 64);
        Assert.Throws<IOException>(() => store.Prepare(Guid.NewGuid(), [new(_asset, new byte[1000])]),
            "Two copies plus the journal reserve exceed the byte limit.");
        Assert.That(File.Exists(_asset), Is.False);

        var files = AssetCreationRecoveryStore.Open(_project, maximumBytes: 1 << 20, maximumFiles: 5);
        Assert.Throws<IOException>(() => files.Prepare(Guid.NewGuid(), Pair()), "Six files exceed the file limit.");
    }

    [Test]
    public void CleanupKeepsADirectoryWithUnknownEntries()
    {
        var store = AssetCreationRecoveryStore.Open(_project);
        var operation = store.Prepare(Guid.NewGuid(), Pair());
        store.Release(operation);
        File.WriteAllText(Path.Combine(operation.Directory, "unknown.txt"), "keep");

        Assert.That(store.RemoveReleased(operation), Is.False);
        Assert.That(Directory.Exists(operation.Directory), Is.True);
    }

    private static void AppendRecord(AssetCreationRecoveryOperation operation, EAssetCreationRecoveryState state)
    {
        string line = JsonSerializer.Serialize(
            new AssetCreationRecoveryRecord(operation.Sequence + 1, state, DateTime.UtcNow),
            AssetCreationRecoveryJsonContext.Default.AssetCreationRecoveryRecord);
        File.AppendAllText(Path.Combine(operation.Directory, "journal.jsonl"), line + "\n");
    }
}
