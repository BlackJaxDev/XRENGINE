using NUnit.Framework;
using Shouldly;
using XREngine.Editor.UI.Components;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class ConsoleRowCacheTests
{
    [Test]
    public void RepeatedRefreshKeepsIdsAndFormattedTextUntilEntryChanges()
    {
        ConsoleRowCache cache = new();
        LogEntry first = new("first", ELogCategory.General, new DateTime(2026, 1, 2, 3, 4, 5));
        LogEntry second = new("second", ELogCategory.UI, new DateTime(2026, 1, 2, 3, 4, 6));
        LogEntry[] source = [first, second];

        cache.Update(source, null, string.Empty).ShouldBeTrue();
        ulong firstId = cache.VisibleRows[0].Id;
        ulong secondId = cache.VisibleRows[1].Id;
        string firstText = cache.VisibleRows[0].Text;

        cache.Update(source, null, string.Empty).ShouldBeFalse();
        cache.VisibleRows[0].Id.ShouldBe(firstId);
        cache.VisibleRows[1].Id.ShouldBe(secondId);
        cache.VisibleRows[0].Text.ShouldBeSameAs(firstText);

        typeof(LogEntry).GetProperty(nameof(LogEntry.RepeatCount))!.SetValue(first, 3);
        cache.Update(source, null, string.Empty).ShouldBeTrue();
        cache.VisibleRows[0].Text.ShouldContain("(x3)");
        cache.VisibleRows[0].Text.ShouldNotBeSameAs(firstText);
        cache.VisibleRows[1].Id.ShouldBe(secondId);
    }

    [Test]
    public void FilterAndRetirementKeepOnlyLiveVisibleEntryIds()
    {
        ConsoleRowCache cache = new();
        LogEntry first = new("alpha", ELogCategory.General, DateTime.UnixEpoch);
        LogEntry second = new("beta", ELogCategory.UI, DateTime.UnixEpoch);
        cache.Update([first, second], null, string.Empty).ShouldBeTrue();
        ulong firstId = cache.VisibleRows[0].Id;
        ulong secondId = cache.VisibleRows[1].Id;

        cache.Update([first, second], ELogCategory.UI, "BET").ShouldBeTrue();
        cache.TotalCount.ShouldBe(2);
        cache.FilteredCount.ShouldBe(1);
        cache.VisibleRows[0].Id.ShouldBe(secondId);
        cache.TryGetVisibleEntry(firstId, out _).ShouldBeFalse();
        cache.TryGetVisibleEntry(secondId, out LogEntry? selected).ShouldBeTrue();
        selected.ShouldBeSameAs(second);

        cache.Update([first], null, string.Empty).ShouldBeTrue();
        cache.TryGetVisibleEntry(secondId, out _).ShouldBeFalse();
        cache.VisibleRows[0].Id.ShouldBe(firstId);
        LogEntry replacement = new("beta", ELogCategory.UI, DateTime.UnixEpoch);
        cache.Update([first, replacement], null, string.Empty).ShouldBeTrue();
        cache.VisibleRows[1].Id.ShouldNotBe(secondId);
        cache.TryGetVisibleEntry(secondId, out _).ShouldBeFalse();
    }

    [Test]
    public void EqualButDistinctLogEntriesKeepSeparateIdentity()
    {
        ConsoleRowCache cache = new();
        EqualConsoleLogEntry first = new("same");
        EqualConsoleLogEntry second = new("same");
        first.Equals(second).ShouldBeTrue();

        cache.Update([first, second], null, string.Empty).ShouldBeTrue();

        cache.VisibleRows.Count.ShouldBe(2);
        cache.VisibleRows[0].Id.ShouldNotBe(cache.VisibleRows[1].Id);
        cache.TryGetVisibleEntry(cache.VisibleRows[0].Id, out LogEntry? found).ShouldBeTrue();
        found.ShouldBeSameAs(first);
    }
}
