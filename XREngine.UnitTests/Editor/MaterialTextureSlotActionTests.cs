using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class MaterialTextureSlotActionTests
{
    private XRTexture2D _old = null!, _next = null!, _foreign = null!;
    private XRMaterial _material = null!;

    [SetUp]
    public void SetUp()
    {
        Undo.ClearHistory();
        _old = new(); _next = new(); _foreign = new();
        _material = new XRMaterial([_old, null], Array.Empty<XRShader>());
        _material.ClearDirty();
    }

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        _material.Destroy(true);
        _old.Destroy(true); _next.Destroy(true); _foreign.Destroy(true);
    }

    [Test]
    public void ApplyUndoAndRedoShareOneActionAndDirtyTheMaterial()
    {
        Assert.That(MaterialTextureSlotAction.Apply(_material, 0, _next), Is.True);
        Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
        Assert.That(_material.Textures[0], Is.SameAs(_next));
        Assert.That(_material.IsDirty, Is.True);

        _material.ClearDirty();
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(_material.Textures[0], Is.SameAs(_old));
        Assert.That(_material.IsDirty, Is.True);

        _material.ClearDirty();
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(_material.Textures[0], Is.SameAs(_next));
        Assert.That(_material.IsDirty, Is.True);
    }

    [Test]
    public void SameTextureRecordsNothing()
    {
        Assert.That(MaterialTextureSlotAction.Apply(_material, 0, _old), Is.False);
        Assert.That(Undo.PendingUndo, Is.Empty);
    }

    [Test]
    public void InvalidSlotThrowsWithoutHistory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterialTextureSlotAction.Apply(_material, 5, _next));
        Assert.That(Undo.PendingUndo, Is.Empty);
        Assert.That(_material.Textures[0], Is.SameAs(_old));
    }

    [Test]
    public void UndoRejectsANewerForeignSlotValue()
    {
        Assert.That(MaterialTextureSlotAction.Apply(_material, 0, _next), Is.True);
        _material.Textures[0] = _foreign;

        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(_material.Textures[0], Is.SameAs(_foreign));
    }

    [Test]
    public void RefusedInitialAdmissionChangesNothing()
    {
        Assert.That(MaterialTextureSlotAction.Apply(_material, 0, _next, initialAdmission: () => false), Is.False);
        Assert.That(_material.Textures[0], Is.SameAs(_old));
        Assert.That(Undo.PendingUndo, Is.Empty);
    }
}
