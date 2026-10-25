using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks ownership between the two virtual-bound field writes.</summary>
[TestFixture]
public sealed class UIListVirtualBoundsWriteTests
{
    private UIListTransform _list = null!;

    [SetUp]
    public void SetUp() => _list = new UIListTransform();

    [TearDown]
    public void TearDown() => _list.Destroy(true);

    [Test]
    public void CurrentOwnerCanSetBothBounds()
    {
        _list.SetVirtualBoundsGuarded(100, 12, 0, static _ => { });
        Assert.That(_list.UpperVirtualBound, Is.EqualTo(100));
        Assert.That(_list.LowerVirtualBound, Is.EqualTo(12));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VetoStopsAtTheRejectedBound(bool vetoUpper)
    {
        _list.SetVirtualBounds(40, 10);
        _list.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == (vetoUpper
                    ? nameof(UIListTransform.UpperVirtualBound)
                    : nameof(UIListTransform.LowerVirtualBound)))
                ((XRPropertyChangingEventArgs<float>)args).AllowChange = false;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetVirtualBoundsGuarded(100, 12, 0, static _ => { }));
        Assert.That(_list.UpperVirtualBound, Is.EqualTo(vetoUpper ? 40 : 100));
        Assert.That(_list.LowerVirtualBound, Is.EqualTo(10));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedBoundIntentCannotBeOverwrittenEvenWhenItsValueMatches(bool roundTrip)
    {
        _list.UpperVirtualBound = 40;
        bool nested = false;
        _list.PropertyChanging += (_, args) =>
        {
            if (nested || args.PropertyName != nameof(UIListTransform.UpperVirtualBound)) return;
            nested = true;
            if (roundTrip) _list.UpperVirtualBound = 20;
            _list.UpperVirtualBound = 40;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetVirtualBoundsGuarded(100, 12, 0, static _ => { }));
        Assert.That(_list.UpperVirtualBound, Is.EqualTo(40));
        Assert.That(_list.LowerVirtualBound, Is.Zero);
    }

    [Test]
    public void UpperChangedCallbackCannotAuthorizeALaterLowerWrite()
    {
        bool current = true;
        _list.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UIListTransform.UpperVirtualBound)) current = false;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetVirtualBoundsGuarded(100, 12, 0, _ =>
            {
                if (!current) throw new InvalidOperationException("Caller changed.");
            }));
        Assert.That(_list.UpperVirtualBound, Is.EqualTo(100));
        Assert.That(_list.LowerVirtualBound, Is.Zero);
    }

    [Test]
    public void OrdinaryBoundsRetainTheirExistingVetoBehavior()
    {
        _list.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(UIListTransform.UpperVirtualBound))
                ((XRPropertyChangingEventArgs<float>)args).AllowChange = false;
        };
        Assert.DoesNotThrow(() => _list.SetVirtualBounds(100, 12));
        Assert.That(_list.UpperVirtualBound, Is.Zero);
        Assert.That(_list.LowerVirtualBound, Is.EqualTo(12));
    }
}
