using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks caller and list ownership across offset notifications.</summary>
[TestFixture]
public sealed class UIListScrollWriteTests
{
    private UIListTransform _list = null!;

    [SetUp]
    public void SetUp() => _list = new UIListTransform();

    [TearDown]
    public void TearDown() => _list.Destroy(true);

    [Test]
    public void RejectedCallerCannotChangeTheOffset()
    {
        InvalidOperationException failure = new("Caller is no longer current.");
        Assert.That(Assert.Throws<InvalidOperationException>(() =>
            _list.SetContentScrollOffsetGuarded(20, failure, static error => throw error)),
            Is.SameAs(failure));
        Assert.That(_list.ContentScrollOffset, Is.Zero);
    }

    [Test]
    public void CallerChangeDuringPropertyChangingStopsTheWrite()
    {
        bool current = true;
        _list.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(UIListTransform.ContentScrollOffset))
                current = false;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetContentScrollOffsetGuarded(20, 0, _ =>
            {
                if (!current) throw new InvalidOperationException("Caller changed.");
            }));
        Assert.That(_list.ContentScrollOffset, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedChangingWriteCannotBeOverwrittenEvenWhenItRestoresTheOldValue(bool restoreOld)
    {
        _list.ContentScrollOffset = 10;
        bool nested = false;
        _list.PropertyChanging += (_, args) =>
        {
            if (nested || args.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            nested = true;
            _list.ContentScrollOffset = 7;
            if (restoreOld) _list.ContentScrollOffset = 10;
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetContentScrollOffsetGuarded(20, 0, static _ => { }));
        Assert.That(_list.ContentScrollOffset, Is.EqualTo(restoreOld ? 10 : 7));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedCallbackKeepsItsValueAndInvalidatesOnlyTheOwnedContinuation(bool guarded)
    {
        bool nested = false;
        _list.PropertyChanged += (_, args) =>
        {
            if (nested || args.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            nested = true;
            _list.ContentScrollOffset = 7;
        };
        if (guarded)
            Assert.Throws<InvalidOperationException>(() =>
                _list.SetContentScrollOffsetGuarded(20, 0, static _ => { }));
        else
            Assert.DoesNotThrow(() => _list.ContentScrollOffset = 20);
        Assert.That(_list.ContentScrollOffset, Is.EqualTo(7));
    }

    [Test]
    public void CallerValidationCannotSilentlyStartAnotherListWrite()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetContentScrollOffsetGuarded(20, _list, static list => list.ContentScrollOffset = 7));
        Assert.That(_list.ContentScrollOffset, Is.EqualTo(7));
    }

    [Test]
    public void IdentityRoundTripDuringNotificationRejectsTheOldWrite()
    {
        _list.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            Guid original = _list.ID;
            _list.AdoptPersistentID(Guid.NewGuid());
            _list.AdoptPersistentID(original);
        };
        Assert.Throws<InvalidOperationException>(() =>
            _list.SetContentScrollOffsetGuarded(20, 0, static _ => { }));
        Assert.That(_list.ContentScrollOffset, Is.Zero);
    }

    [Test]
    public void SuppressedNotificationsStillRequireCallerOwnership()
    {
        using (XRBase.SuppressPropertyNotifications())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _list.SetContentScrollOffsetGuarded(20, 0,
                    static _ => throw new InvalidOperationException("Caller changed.")));
            Assert.That(_list.ContentScrollOffset, Is.Zero);
            Assert.That(_list.SetContentScrollOffsetGuarded(30, 0, static _ => { }), Is.True);
        }
        Assert.That(_list.ContentScrollOffset, Is.EqualTo(30));
    }

    [Test]
    public void NoChangeAttemptStillInvalidatesAnEarlierIntentReceipt()
    {
        ulong before = _list.ContentScrollIntentRevision;
        Assert.That(_list.SetContentScrollOffsetGuarded(0, 0, static _ => { }), Is.False);
        Assert.That(_list.ContentScrollIntentRevision, Is.GreaterThan(before));
    }

    [Test]
    public void QueuedListCannotAcceptAnOffsetWrite()
    {
        _list.Destroy(false);
        Assert.Throws<InvalidOperationException>(() => _list.ContentScrollOffset = 20);
        Assert.That(_list.ContentScrollOffset, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VetoKeepsTheOffsetAndStillChecksCallerOwnership(bool invalidateCaller)
    {
        bool current = true;
        _list.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            ((XRPropertyChangingEventArgs<float>)args).AllowChange = false;
            current = !invalidateCaller;
        };
        bool Assign() => _list.SetContentScrollOffsetGuarded(20, 0, _ =>
        {
            if (!current) throw new InvalidOperationException("Caller changed during veto.");
        });
        if (invalidateCaller)
            Assert.Throws<InvalidOperationException>(() => Assign());
        else
            Assert.That(Assign(), Is.False);
        Assert.That(_list.ContentScrollOffset, Is.Zero);
    }
}
