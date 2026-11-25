using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIVisibilityGuardedWriteTests
{
    private SceneNode _root = null!;
    private UIBoundableTransform _target = null!;

    [SetUp]
    public void SetUp()
    {
        _root = new SceneNode("Visibility root", new UICanvasTransform());
        _target = new UIBoundableTransform();
        _ = new SceneNode(_root, "Visibility target", _target);
    }

    [TearDown]
    public void TearDown() => _root.Destroy(true);

    [Test]
    public void AcceptedAndEqualWritesRetainOneIntentPerRequest()
    {
        ulong before = _target.VisibilityWriteIntentRevision;
        Assert.That(_target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }), Is.True);
        Assert.That(_target.Visibility, Is.EqualTo(EVisibility.Hidden));
        Assert.That(_target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }), Is.False);
        Assert.That(_target.VisibilityWriteIntentRevision, Is.EqualTo(before + 2));
    }

    [Test]
    public void TransferredOwnerRejectsTheVisibilityWriteBeforeCommit()
    {
        SceneNode destination = new(_root, "New owner", new UIBoundableTransform());
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UITransform.Visibility))
                _target.SetParent(destination.Transform, false, EParentAssignmentMode.Immediate);
        };
        Assert.Throws<InvalidOperationException>(() =>
            _target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }));
        Assert.That(_target.Parent, Is.SameAs(destination.Transform));
        Assert.That(_target.Visibility, Is.EqualTo(EVisibility.Visible));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ANewerVisibilityRequestWinsEvenWhenItsValueIsEqual(bool equal)
    {
        bool called = false;
        _target.PropertyChanging += (_, change) =>
        {
            if (called || change.PropertyName != nameof(UITransform.Visibility)) return;
            called = true;
            _target.Visibility = equal ? _target.Visibility : EVisibility.Collapsed;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }));
        Assert.That(called, Is.True);
        Assert.That(_target.Visibility, Is.EqualTo(equal ? EVisibility.Visible : EVisibility.Collapsed));
    }

    [Test]
    public void PostchangeCallbackKeepsItsNewerVisibility()
    {
        bool called = false;
        _target.PropertyChanged += (_, change) =>
        {
            if (called || change.PropertyName != nameof(UITransform.Visibility)) return;
            called = true;
            _target.Visibility = EVisibility.Collapsed;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }));
        Assert.That(called, Is.True);
        Assert.That(_target.Visibility, Is.EqualTo(EVisibility.Collapsed));
    }

    [Test]
    public void AVisibilityVetoIsReportedWithoutWritingTheField()
    {
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UITransform.Visibility))
                ((XRPropertyChangingEventArgs<EVisibility>)change).AllowChange = false;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }));
        Assert.That(_target.Visibility, Is.EqualTo(EVisibility.Visible));
    }

    [Test]
    public void CallerAdmissionCannotTransferTheOwnerAndThenWriteIt()
    {
        SceneNode destination = new(_root, "New owner", new UIBoundableTransform());
        Assert.Throws<InvalidOperationException>(() => _target.SetVisibilityGuarded(EVisibility.Hidden,
            (Target: _target, Parent: destination.Transform), static state =>
                state.Target.SetParent(state.Parent, false, EParentAssignmentMode.Immediate)));
        Assert.That(_target.Parent, Is.SameAs(destination.Transform));
        Assert.That(_target.Visibility, Is.EqualTo(EVisibility.Visible));
    }

    [Test]
    public void RegeneratedTransformRejectsItsEarlierWrite()
    {
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UITransform.Visibility))
                _target.Generate();
        };
        Assert.Throws<InvalidOperationException>(() =>
            _target.SetVisibilityGuarded(EVisibility.Hidden, 0, static _ => { }));
        Assert.That(_target.Visibility, Is.EqualTo(EVisibility.Visible));
    }
}
