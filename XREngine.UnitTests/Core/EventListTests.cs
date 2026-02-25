using System.Collections.Generic;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using XREngine.Core;

namespace XREngine.UnitTests.Core;

[TestFixture]
public class EventListTests
{
    [Test]
    public void Insert_RaisesPostAnythingAdded_NotRemoved()
    {
        var list = new EventList<int>();
        int addedCount = 0;
        int removedCount = 0;

        list.PostAnythingAdded += _ => addedCount++;
        list.PostAnythingRemoved += _ => removedCount++;

        list.Insert(0, 42);

        Assert.That(addedCount, Is.EqualTo(1));
        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list[0], Is.EqualTo(42));
    }

    [Test]
    public void InsertRange_RaisesCollectionChanged_AddAction()
    {
        var list = new EventList<int> { 1 };
        ECollectionChangedAction? action = null;

        list.CollectionChanged += (_, args) => action = args.Action;

        list.InsertRange(1, [2, 3]);

        Assert.That(action, Is.EqualTo(ECollectionChangedAction.Add));
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void RemoveRange_PreAnythingRemoved_VetoesIndividualItems()
    {
        var list = new EventList<int>([1, 2, 3, 4]);
        List<int>? postRemoved = null;

        list.PreAnythingRemoved += item => item % 2 == 0;
        list.PostRemovedRange += items => postRemoved = items.ToList();

        list.RemoveRange(0, 4);

        Assert.That(postRemoved, Is.EqualTo(new[] { 2, 4 }));
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public void RemoveAll_PreAnythingRemoved_VetoesIndividualItems()
    {
        var list = new EventList<int>([1, 2, 3, 4, 5, 6]);
        List<int>? postRemoved = null;

        list.PreAnythingRemoved += item => item != 4;
        list.PostRemovedRange += items => postRemoved = items.ToList();

        list.RemoveAll(item => item % 2 == 0);

        Assert.That(postRemoved, Is.EqualTo(new[] { 2, 6 }));
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 1, 3, 4, 5 }));
    }

    [Test]
    public void AddRange_DisallowDuplicates_FiltersExistingAndInputDuplicates()
    {
        var list = new EventList<int>(allowDuplicates: false, allowNull: true) { 1 };

        list.AddRange([1, 2, 2, 3, 3, 3]);

        Assert.That(list.ToArray(), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void IList_ObjectPath_RespectsPolicies_AndTypeChecks()
    {
        IList list = new EventList<int>(allowDuplicates: false, allowNull: true);

        int firstIndex = list.Add(7);
        int duplicateIndex = list.Add(7);
        list.Insert(0, 7); // should be ignored because duplicates are disallowed

        Assert.That(firstIndex, Is.EqualTo(0));
        Assert.That(duplicateIndex, Is.EqualTo(-1));
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That((int)list[0]!, Is.EqualTo(7));

        Assert.Throws<ArgumentException>(() => list.Add("not-an-int"));
    }
}
