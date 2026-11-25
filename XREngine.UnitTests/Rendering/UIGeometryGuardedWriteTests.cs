using System.Numerics;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks exact owner and write intent for guarded height and translation.</summary>
[TestFixture, NonParallelizable]
public sealed class UIGeometryGuardedWriteTests
{
    private SceneNode _root = null!;
    private UIBoundableTransform _target = null!;

    [SetUp]
    public void SetUp()
    {
        _root = new("Geometry root", new UICanvasTransform());
        _target = new() { Height = 10, Translation = Vector2.One };
        _ = new SceneNode(_root, "Geometry target", _target);
    }

    [TearDown]
    public void TearDown() => _root.Destroy(true);

    [TestCase(false)]
    [TestCase(true)]
    public void AcceptedAndEqualRequestsAdvanceTheirOwnIntent(bool height)
    {
        ulong before = Intent(height);
        Assert.That(Write(height), Is.True);
        Assert.That(Write(height), Is.False);
        Assert.That(Intent(height), Is.EqualTo(before + 2));
        Assert.That(Value(height), Is.EqualTo(height ? (object)30f : new Vector2(3, 4)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PropertyChangingTransferRejectsBeforeTheOldValueCommits(bool height)
    {
        object? before = Value(height);
        SceneNode destination = new(_root, "Transferred owner", new UIBoundableTransform());
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == Property(height))
                _target.SetParent(destination.Transform, false, EParentAssignmentMode.Immediate);
        };
        Assert.Throws<InvalidOperationException>(() => Write(height));
        Assert.That(_target.Parent, Is.SameAs(destination.Transform));
        Assert.That(Value(height), Is.EqualTo(before));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VetoIsReportedAndLeavesTheFieldUnchanged(bool height)
    {
        object? before = Value(height);
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName != Property(height)) return;
            if (height) ((XRPropertyChangingEventArgs<float?>)change).AllowChange = false;
            else ((XRPropertyChangingEventArgs<Vector2>)change).AllowChange = false;
        };
        Assert.Throws<InvalidOperationException>(() => Write(height));
        Assert.That(Value(height), Is.EqualTo(before));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void NewerOrdinaryRequestWinsIncludingEqualValueIntent(bool height, bool equal)
    {
        object? before = Value(height);
        bool entered = false;
        _target.PropertyChanging += (_, change) =>
        {
            if (entered || change.PropertyName != Property(height)) return;
            entered = true;
            if (height) _target.Height = equal ? _target.Height : 60;
            else _target.Translation = equal ? _target.Translation : new Vector2(8, 9);
        };
        Assert.Throws<InvalidOperationException>(() => Write(height));
        Assert.That(entered, Is.True);
        Assert.That(Value(height), Is.EqualTo(equal ? before : height ? (object)60f : new Vector2(8, 9)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ThrowingPostChangeObserverLeavesTheCompletedWriteVisible(bool height)
    {
        bool threw = false;
        _target.PropertyChanged += (_, change) =>
        {
            if (threw || change.PropertyName != Property(height)) return;
            threw = true;
            throw new ApplicationException("After geometry commit.");
        };
        Assert.Throws<ApplicationException>(() => Write(height));
        Assert.That(Value(height), Is.EqualTo(height ? (object)30f : new Vector2(3, 4)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RegeneratedTransformCannotCommitItsEarlierRequest(bool height)
    {
        object? before = Value(height);
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == Property(height)) _target.Generate();
        };
        Assert.Throws<InvalidOperationException>(() => Write(height));
        Assert.That(Value(height), Is.EqualTo(before));
    }

    private bool Write(bool height) => height
        ? _target.SetHeightGuarded(30, 0, static _ => { })
        : _target.SetTranslationGuarded(new Vector2(3, 4), 0, static _ => { });
    private object? Value(bool height) => height ? _target.Height : _target.Translation;
    private ulong Intent(bool height) => height ? _target.HeightWriteIntentRevision : _target.TranslationWriteIntentRevision;
    private static string Property(bool height) => height ? nameof(UIBoundableTransform.Height) : nameof(UITransform.Translation);
}
