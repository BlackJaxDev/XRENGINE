using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderBindingGenerationTrackerTests
{
    private readonly record struct TestKey(object? Source, float Value);

    [Test]
    public void Advance_KeepsTheGenerationWhileTheKeyIsEqual()
    {
        var tracker = new RenderBindingGenerationTracker<TestKey>();
        object source = new();

        ulong first = tracker.Advance(new TestKey(source, 1.0f));
        first.ShouldNotBe(0UL);
        tracker.Advance(new TestKey(source, 1.0f)).ShouldBe(first);
    }

    [Test]
    public void Advance_AdvancesWhenAValueOrAReferenceChanges()
    {
        var tracker = new RenderBindingGenerationTracker<TestKey>();
        object source = new();
        ulong first = tracker.Advance(new TestKey(source, 1.0f));

        ulong valueChanged = tracker.Advance(new TestKey(source, 2.0f));
        valueChanged.ShouldBeGreaterThan(first);

        ulong referenceChanged = tracker.Advance(new TestKey(new object(), 2.0f));
        referenceChanged.ShouldBeGreaterThan(valueChanged);

        // Going back to an earlier key is a change too.
        tracker.Advance(new TestKey(source, 1.0f)).ShouldBeGreaterThan(referenceChanged);
    }

    [Test]
    public void Advance_AllocatesNoManagedMemoryForAnEqualKey()
    {
        var tracker = new RenderBindingGenerationTracker<TestKey>();
        object source = new();
        TestKey key = new(source, 1.0f);
        tracker.Advance(key);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
            tracker.Advance(key);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }
}
