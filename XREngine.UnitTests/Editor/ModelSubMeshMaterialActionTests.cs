using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class ModelSubMeshMaterialActionTests
{
    private XRMaterial _old = null!, _next = null!, _newer = null!;
    private SubMeshLOD _first = null!, _second = null!;
    private SubMesh _subMesh = null!;
    private Model _model = null!;

    [SetUp]
    public void SetUp()
    {
        Undo.ClearHistory();
        _old = new(); _next = new(); _newer = new();
        _first = new(_old, null, 0);
        _second = new(null, null, 10);
        _subMesh = new(_first, _second);
        _model = new(_subMesh);
        _model.ClearDirty(); _subMesh.ClearDirty();
    }

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        Undo.Untrack(_first); Undo.Untrack(_second);
        _model.Destroy(true); _subMesh.Destroy(true);
        _old.Destroy(true); _next.Destroy(true); _newer.Destroy(true);
    }

    [Test]
    public void AllLodsShareOneHistoryEntryAndDirtyDocumentsOnReplay()
    {
        Assert.That(ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next), Is.True);
        Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
        Assert.That(_first.Material, Is.SameAs(_next));
        Assert.That(_second.Material, Is.SameAs(_next));
        Assert.That(_model.IsDirty && _subMesh.IsDirty, Is.True);
        _model.ClearDirty(); _subMesh.ClearDirty();
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(_first.Material, Is.SameAs(_old));
        Assert.That(_second.Material, Is.Null);
        Assert.That(_model.IsDirty && _subMesh.IsDirty, Is.True);
        _model.ClearDirty(); _subMesh.ClearDirty();
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(_first.Material, Is.SameAs(_next));
        Assert.That(_second.Material, Is.SameAs(_next));
        Assert.That(_model.IsDirty && _subMesh.IsDirty, Is.True);
    }

    [Test]
    public void SharedSubMeshAliasesObserveHistoryAndExplicitSourceDocumentsBecomeDirty()
    {
        Model otherModel = new(_subMesh);
        try
        {
            otherModel.ClearDirty();
            Assert.That(ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next), Is.True);
            Assert.That(otherModel.Meshes[0], Is.SameAs(_subMesh));
            Assert.That(otherModel.Meshes[0].LODs.All(lod => ReferenceEquals(lod.Material, _next)), Is.True);
            Assert.That(_model.IsDirty && _subMesh.IsDirty, Is.True);
            Assert.That(otherModel.IsDirty, Is.False,
                "The unchanged alias document is outside the explicit source documents.");
            _model.ClearDirty(); _subMesh.ClearDirty();
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(otherModel.Meshes[0].LODs.First().Material, Is.SameAs(_old));
            Assert.That(_model.IsDirty && _subMesh.IsDirty, Is.True);
            _model.ClearDirty(); _subMesh.ClearDirty();
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(otherModel.Meshes[0].LODs.All(lod => ReferenceEquals(lod.Material, _next)), Is.True);
            Assert.That(_model.IsDirty && _subMesh.IsDirty, Is.True);
        }
        finally { otherModel.Destroy(true); }
    }

    [Test]
    public void NoopDoesNotCreateHistoryOrDirtyDocuments()
    {
        _first.Material = _next; _second.Material = _next;
        Assert.That(ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next), Is.False);
        Assert.That(Undo.PendingUndo, Is.Empty);
        Assert.That(_model.IsDirty || _subMesh.IsDirty, Is.False);
    }

    [Test]
    public void ObserverFailureCompensatesBothMaterialsWithoutHistory()
    {
        bool failed = false;
        _second.PropertyChanged += (_, change) =>
        {
            if (failed || change.PropertyName != nameof(SubMeshLOD.Material)) return;
            failed = true;
            throw new ApplicationException("After the second material committed.");
        };
        Assert.Throws<ApplicationException>(() => ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next));
        Assert.That(_first.Material, Is.SameAs(_old));
        Assert.That(_second.Material, Is.Null);
        Assert.That(Undo.PendingUndo, Is.Empty);
    }

    [Test]
    public void PrechangeNewerMaterialSurvivesAndLaterLodIsNotWritten()
    {
        bool changed = false;
        _first.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(SubMeshLOD.Material)) return;
            changed = true;
            _first.Material = _newer;
        };
        Assert.Throws<InvalidOperationException>(() => ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next));
        Assert.That(_first.Material, Is.SameAs(_newer));
        Assert.That(_second.Material, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedModelMembershipRejectsBeforeMaterialAssignment(bool replaceCollection)
    {
        bool changed = false;
        _first.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(SubMeshLOD.Material)) return;
            changed = true;
            if (replaceCollection) _model.Meshes = new() { _subMesh };
            else _model.Meshes.Remove(_subMesh);
        };
        Assert.Throws<InvalidOperationException>(() => ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next));
        Assert.That(_model.Meshes.Contains(_subMesh), Is.EqualTo(replaceCollection));
        Assert.That(_first.Material, Is.SameAs(_old));
        Assert.That(_second.Material, Is.Null);
    }

    [Test]
    public void MissingModelMembershipRejectsWithoutHistory()
    {
        _model.Meshes.Remove(_subMesh);
        Assert.That(ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next), Is.False);
        Assert.That(Undo.PendingUndo, Is.Empty);
        Assert.That(_first.Material, Is.SameAs(_old));
    }

    [Test]
    public void InitialAdmissionLossStopsLaterLodsAndDoesNotCompensateTheNewOwner()
    {
        bool current = true;
        _first.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SubMeshLOD.Material)) current = false;
        };
        Assert.Throws<InvalidOperationException>(() =>
            ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next, () => current));
        Assert.That(_first.Material, Is.SameAs(_next));
        Assert.That(_second.Material, Is.Null);
    }

    [Test]
    public void AcceptedHistoryRemainsAssetOwnedAfterInitialAdmissionExpires()
    {
        bool current = true;
        Assert.That(ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next, () => current), Is.True);
        current = false;
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(_first.Material, Is.SameAs(_old));
        Assert.That(_second.Material, Is.Null);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(_second.Material, Is.SameAs(_next));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplacedSourceDocumentIsNotDirtiedWhenTheFirstWriteIsRejected(bool subMeshSource)
    {
        Model oldDocument = new(), newDocument = new();
        try
        {
            if (subMeshSource) _subMesh.SourceAsset = oldDocument;
            else _model.SourceAsset = oldDocument;
            oldDocument.ClearDirty(); newDocument.ClearDirty();
            _first.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(SubMeshLOD.Material)) return;
                if (subMeshSource) _subMesh.SourceAsset = newDocument;
                else _model.SourceAsset = newDocument;
            };
            Assert.Throws<InvalidOperationException>(() => ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next));
            Assert.That(_first.Material, Is.SameAs(_old));
            Assert.That(_second.Material, Is.Null);
            Assert.That(oldDocument.IsDirty, Is.False);
            Assert.That(newDocument.IsDirty, Is.False);
        }
        finally
        {
            _model.SourceAsset = _model; _subMesh.SourceAsset = _subMesh;
            oldDocument.Destroy(true); newDocument.Destroy(true);
        }
    }

    [Test]
    public void ReplacedLodCollectionRejectsHistoryReplay()
    {
        Assert.That(ModelSubMeshMaterialAction.Apply(_model, _subMesh, _next), Is.True);
        _subMesh.LODs = new(_subMesh.LODs, _subMesh.LODs.Comparer);
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(_first.Material, Is.SameAs(_next));
        Assert.That(_second.Material, Is.SameAs(_next));
    }
}
