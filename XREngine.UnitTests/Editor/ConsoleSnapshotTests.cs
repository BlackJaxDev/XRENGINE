using System.Reflection;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class ConsoleSnapshotTests
{
    [Test]
    public void UnchangedSnapshotPollingReusesCallerStorageWithoutAllocating()
    {
        object gate = typeof(Debug).GetField("ConsoleEntriesLock",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        List<ConsoleEntrySnapshot> entries = [];
        // Hold the existing producer gate so unrelated log activity cannot change this sample.
        // The test does not add, remove, or replace any global log entry.
        lock (gate)
        {
            ulong revision = Debug.CopyConsoleEntriesTo(entries, 0);
            int count = entries.Count;
            Debug.CopyConsoleEntriesTo(entries, revision);
            long before = GC.GetAllocatedBytesForCurrentThread();
            ulong observed = revision;
            for (int index = 0; index < 64; index++)
                observed = Debug.CopyConsoleEntriesTo(entries, observed);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            allocated.ShouldBe(0);
            observed.ShouldBe(revision);
            entries.Count.ShouldBe(count);
        }
    }
}
