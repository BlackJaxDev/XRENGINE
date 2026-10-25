using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks that retained scroll authority follows the exact Rows owner.</summary>
[TestFixture, NonParallelizable]
public sealed class NativePanelScrollOwnerReceiptTests
{
    private SceneNode _panel = null!;
    private SceneNode _wrapper = null!;
    private SceneNode _rows = null!;
    private SceneNode _foreign = null!;
    private UIListTransform _list = null!;

    [SetUp]
    public void SetUp()
    {
        _panel = new("Panel", new UIBoundableTransform());
        _wrapper = _panel.NewChildWithTransform<UIBoundableTransform>(out _, "Content");
        _rows = _wrapper.NewChildWithTransform(out _list, "Rows");
        _foreign = new("Foreign panel", new UIBoundableTransform());
    }

    [TearDown]
    public void TearDown()
    {
        _panel.Destroy(true);
        _foreign.Destroy(true);
    }

    private NativePanelScrollOwnerReceipt Capture()
        => NativePanelScrollOwnerReceipt.Capture(_list, _panel.Transform)!.Value;

    [Test]
    public void CaptureAcceptsNestedOwnershipAndRejectsAnotherPanel()
    {
        Assert.That(Capture().IsCurrent(_list, _panel.Transform), Is.True);
        Assert.That(NativePanelScrollOwnerReceipt.Capture(_list, _foreign.Transform), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MovingTheListOrItsAncestorRevokesTheReceipt(bool moveAncestor)
    {
        NativePanelScrollOwnerReceipt receipt = Capture();
        (moveAncestor ? _wrapper.Transform : _list).SetParent(_foreign.Transform, false,
            EParentAssignmentMode.Immediate);
        Assert.That(receipt.IsCurrent(_list, _panel.Transform), Is.False);
        Assert.That(NativePanelScrollOwnerReceipt.Capture(_list, _panel.Transform), Is.Null);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void IdentityRoundTripCannotReuseTheCapturedOwner(int part)
    {
        NativePanelScrollOwnerReceipt receipt = Capture();
        XRObjectBase owner = part switch
        {
            0 => _list,
            1 => _rows,
            2 => _wrapper.Transform,
            _ => _panel.Transform,
        };
        Guid original = owner.ID;
        owner.AdoptPersistentID(Guid.NewGuid());
        owner.AdoptPersistentID(original);
        Assert.That(receipt.IsCurrent(_list, _panel.Transform), Is.False);
    }

    [Test]
    public void GuardedScrollPreservesListReparentedByItsChangingCallback()
    {
        NativePanelScrollOwnerReceipt receipt = Capture();
        _list.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UIListTransform.ContentScrollOffset))
                _list.SetParent(_foreign.Transform, false, EParentAssignmentMode.Immediate);
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetContentScrollOffsetGuarded(24, receipt, static owner =>
            {
                if (!owner.IsCurrent(owner.List, owner.PanelTransform))
                    throw new InvalidOperationException("The Rows owner changed.");
            }));
        Assert.Multiple(() =>
        {
            Assert.That(_list.ContentScrollOffset, Is.Zero);
            Assert.That(_list.Parent, Is.SameAs(_foreign.Transform));
            Assert.That(_rows.IsDestroyed, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GuardedBoundsStopBeforeWritingOnAReparentedList(bool moveDuringUpper)
    {
        NativePanelScrollOwnerReceipt receipt = Capture();
        _list.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == (moveDuringUpper
                    ? nameof(UIListTransform.UpperVirtualBound)
                    : nameof(UIListTransform.LowerVirtualBound)))
                _list.SetParent(_foreign.Transform, false, EParentAssignmentMode.Immediate);
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetVirtualBoundsGuarded(100, 12, receipt, static owner =>
            {
                if (!owner.IsCurrent(owner.List, owner.PanelTransform))
                    throw new InvalidOperationException("The Rows owner changed.");
            }));
        Assert.Multiple(() =>
        {
            Assert.That(_list.UpperVirtualBound, Is.EqualTo(moveDuringUpper ? 0 : 100));
            Assert.That(_list.LowerVirtualBound, Is.Zero);
            Assert.That(_list.Parent, Is.SameAs(_foreign.Transform));
            Assert.That(_rows.IsDestroyed, Is.False);
        });
    }
}
