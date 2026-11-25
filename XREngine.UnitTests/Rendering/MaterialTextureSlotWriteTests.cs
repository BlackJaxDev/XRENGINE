using NUnit.Framework;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaterialTextureSlotWriteTests
{
    private XRMaterial _material = null!;
    private XRTexture2D _old = null!, _next = null!, _other = null!;
    private EventList<XRTexture?> _list = null!;

    [SetUp]
    public void SetUp()
    {
        _old = new(); _next = new(); _other = new();
        _list = [_old, null];
        _material = new() { Textures = _list };
    }

    [TearDown]
    public void TearDown()
    {
        _material.Destroy(true);
        _old.Destroy(true); _next.Destroy(true); _other.Destroy(true);
        _list.Destroy(true);
    }

    private bool Write() => _material.SetTextureSlotGuarded(_list, 0, _old, _next, 0, _ => { });

    [Test]
    public void OrdinaryWriteKeepsTheListAndOtherSlots()
    {
        ulong before = _list.MutationIntentRevision;
        Assert.That(Write(), Is.True);
        Assert.That(_material.Textures, Is.SameAs(_list));
        Assert.That(_list[0], Is.SameAs(_next));
        Assert.That(_list[1], Is.Null);
        Assert.That(_list.MutationIntentRevision, Is.EqualTo(before + 1));
    }

    [Test]
    public void VetoConsumesOneIntentWithoutWriting()
    {
        ulong before = _list.MutationIntentRevision;
        _list.PreIndexSet += (_, _) => false;
        Assert.That(Write(), Is.False);
        Assert.That(_list[0], Is.SameAs(_old));
        Assert.That(_list.MutationIntentRevision, Is.EqualTo(before + 1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedSlotIntentCancelsTheOlderWrite(bool changeAndRestore)
    {
        bool entered = false;
        _list.PreIndexSet += (_, _) =>
        {
            if (entered) return true;
            entered = true;
            if (changeAndRestore) _list[0] = _other;
            _list[0] = _old;
            return true;
        };
        Assert.Throws<InvalidOperationException>(() => Write());
        Assert.That(_list[0], Is.SameAs(_old));
    }

    [Test]
    public void EqualMaterialListAssignmentCancelsTheOlderSlotWrite()
    {
        _list.PreIndexSet += (_, _) => { _material.Textures = _list; return true; };
        Assert.Throws<InvalidOperationException>(() => Write());
        Assert.That(_list[0], Is.SameAs(_old));
    }

    [Test]
    public void ReplacementListSurvivesTheOlderSlotWrite()
    {
        EventList<XRTexture?> replacement = [_other];
        try
        {
            _list.PreIndexSet += (_, _) => { _material.Textures = replacement; return true; };
            Assert.Throws<InvalidOperationException>(() => Write());
            Assert.That(_material.Textures, Is.SameAs(replacement));
            Assert.That(replacement[0], Is.SameAs(_other));
            Assert.That(_list[0], Is.SameAs(_old));
        }
        finally { _material.Destroy(true); replacement.Destroy(true); }
    }

    [Test]
    public void PostCallbackPeerWriteIsPreservedAndReported()
    {
        bool entered = false;
        _list.PostIndexSet += (_, _) =>
        {
            if (entered) return;
            entered = true;
            _list[1] = _other;
        };
        Assert.Throws<InvalidOperationException>(() => Write());
        Assert.That(_list[0], Is.SameAs(_next));
        Assert.That(_list[1], Is.SameAs(_other));
    }

    [Test]
    public void PostCallbackExceptionLeavesAcceptedValueForCallerCompensation()
    {
        _list.PostIndexSet += (_, _) => throw new ApplicationException("Observer failed.");
        Assert.Throws<ApplicationException>(() => Write());
        Assert.That(_list[0], Is.SameAs(_next));
    }

    [Test]
    public void QueuedTextureDestructionRejectsBeforeStorage()
    {
        _list.PreIndexSet += (_, _) => { _next.Destroy(); return true; };
        Assert.Throws<InvalidOperationException>(() => Write());
        Assert.That(_list[0], Is.SameAs(_old));
    }

    [Test]
    public void AdmissionIsCheckedBetweenIndividualPreCallbacks()
    {
        bool current = true, secondCalled = false;
        _list.PreIndexSet += (_, _) => { current = false; return true; };
        _list.PreIndexSet += (_, _) => { secondCalled = true; return true; };
        Assert.Throws<InvalidOperationException>(() => _material.SetTextureSlotGuarded(_list, 0, _old,
            _next, 0, _ => { if (!current) throw new InvalidOperationException("Owner changed."); }));
        Assert.That(secondCalled, Is.False);
        Assert.That(_list[0], Is.SameAs(_old));
    }

    [Test]
    public void SuppressedListNotificationsStillRequireOwner()
    {
        _list._updating = true;
        Assert.Throws<InvalidOperationException>(() => _material.SetTextureSlotGuarded(_list, 0, _old,
            _next, 0, _ => throw new InvalidOperationException("Owner changed.")));
        Assert.That(_list[0], Is.SameAs(_old));
    }
}
