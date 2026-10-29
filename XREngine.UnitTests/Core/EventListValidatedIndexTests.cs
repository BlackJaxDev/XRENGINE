using System.Reflection;
using NUnit.Framework;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class EventListValidatedIndexTests
{
    [Test]
    public void GuardedWriteKeepsOrdinaryIndexCallbackOrder()
    {
        EventList<int> list = [1];
        List<string> calls = [];
        list.PreModified += () => { calls.Add("pre modified"); return true; };
        list.PreAdded += _ => { calls.Add("pre added"); return true; };
        list.PreAnythingAdded += _ => { calls.Add("pre anything"); return true; };
        list.PreIndexSet += (_, _) => { calls.Add("pre index"); return true; };
        list.PostAdded += _ => calls.Add("post added");
        list.PostAnythingAdded += _ => calls.Add("post anything");
        list.PostIndexSet += (_, previous) => calls.Add($"post index {previous}");
        list.PostModified += () => calls.Add("post modified");
        list.CollectionChanged += (_, _) => calls.Add("collection");
        try
        {
            Assert.That(list.SetIndexValidated(0, 1, 2, 0, _ => { }), Is.True);
            Assert.That(calls, Is.EqualTo(new[] { "pre modified", "pre added", "pre anything",
                "pre index", "post added", "post anything", "post index 1", "post modified", "collection" }));
        }
        finally { list.Destroy(true); }
    }

    [TestCase("equal")]
    [TestCase("rejected")]
    [TestCase("suppressed")]
    public void NewerNonChangingIntentRejectsTheOlderWrite(string attempt)
    {
        EventList<int> list = new([1], allowDuplicates: false, allowNull: true);
        list.PreIndexSet += (_, _) =>
        {
            if (attempt == "equal") list.AllowDuplicates = false;
            else if (attempt == "rejected") list.Add(1);
            else
            {
                list._updating = true;
                try { list.Remove(99); }
                finally { list._updating = false; }
            }
            return true;
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => list.SetIndexValidated(0, 1, 2, 0, _ => { }));
            Assert.That(list[0], Is.EqualTo(1));
        }
        finally { list.Destroy(true); }
    }

    [Test]
    public void ExhaustedIntentRefusesStorageAndCallbacksWithoutWrapping()
    {
        EventList<int> list = [1];
        FieldInfo field = typeof(EventList<int>).GetField("_mutationIntentRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool called = false;
        list.PreIndexSet += (_, _) => { called = true; return true; };
        field.SetValue(list, ulong.MaxValue);
        try
        {
            Assert.Throws<InvalidOperationException>(() => list.SetIndexValidated(0, 1, 2, 0, _ => { }));
            Assert.That(list.MutationIntentRevision, Is.EqualTo(ulong.MaxValue));
            Assert.That(list[0], Is.EqualTo(1));
            Assert.That(called, Is.False);
        }
        finally { field.SetValue(list, 0UL); list.Destroy(true); }
    }
}
