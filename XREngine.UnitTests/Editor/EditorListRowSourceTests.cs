using NUnit.Framework;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorListRowSourceTests
{
    [Test]
    public void MillionRowSourceAssignsOnlyRequestedIdentities()
    {
        EditorListRowSource source = new();
        source.Reset(1_000_000);
        ulong nearEnd = source.GetItemId(999_999);
        ulong first = source.GetItemId(0);
        Assert.That(source.TryGetIndexOfItemId(nearEnd, out int oldIndex), Is.True);
        Assert.That(oldIndex, Is.EqualTo(999_999));
        source.Insert(0);
        Assert.That(source.TryGetIndexOfItemId(nearEnd, out int shifted), Is.True);
        Assert.That(shifted, Is.EqualTo(1_000_000));
        Assert.That(source.GetItemId(1), Is.EqualTo(first));
    }

    [Test]
    public void DuplicateValuesKeepDistinctOccurrenceIdsThroughRemoveAndRestore()
    {
        EditorListRowSource source = new();
        source.Reset(3);
        ulong first = source.GetItemId(0);
        ulong middle = source.GetItemId(1);
        ulong last = source.GetItemId(2);
        Assert.That(first, Is.Not.EqualTo(middle));
        ulong removed = source.Remove(1);
        Assert.That(removed, Is.EqualTo(middle));
        Assert.That(source.GetItemId(1), Is.EqualTo(last));
        source.Insert(1, removed);
        Assert.That(source.GetItemId(0), Is.EqualTo(first));
        Assert.That(source.GetItemId(1), Is.EqualTo(middle));
        Assert.That(source.GetItemId(2), Is.EqualTo(last));
    }
}
