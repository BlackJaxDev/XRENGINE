using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

/// <summary>Checks packed readback sizes and allocation-free size calculation.</summary>
[TestFixture]
public sealed class PhysicsChainReadbackLayoutTests
{
    [TestCase(PhysicsChainReadbackFields.Particles, 3, 3, 72)]
    [TestCase(PhysicsChainReadbackFields.Bones, 3, 3, 144)]
    [TestCase(PhysicsChainReadbackFields.Sockets, 3, 3, 144)]
    [TestCase(PhysicsChainReadbackFields.Bounds, 3, 3, 72)]
    [TestCase(PhysicsChainReadbackFields.CollisionEvents, 3, 3, 120)]
    [TestCase(PhysicsChainReadbackFields.FullTransformMirror, 3, 3, 144)]
    [TestCase(PhysicsChainReadbackFields.Particles | PhysicsChainReadbackFields.Bones |
        PhysicsChainReadbackFields.Bounds, 2, 6, 192)]
    [TestCase((PhysicsChainReadbackFields)63, 2, 12, 464)]
    public void SizesMatchPackedContract(
        PhysicsChainReadbackFields fields,
        int selections,
        int expectedElements,
        int expectedBytes)
    {
        PhysicsChainReadbackLayout.TryCalculate(fields, selections, out int elements, out int bytes)
            .ShouldBeTrue();
        elements.ShouldBe(expectedElements);
        bytes.ShouldBe(expectedBytes);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void NonpositiveSelectionCountReturnsZeroOutputs(int selections)
    {
        PhysicsChainReadbackLayout.TryCalculate(
            (PhysicsChainReadbackFields)63, selections, out int elements, out int bytes).ShouldBeFalse();
        elements.ShouldBe(0);
        bytes.ShouldBe(0);
    }

    [TestCase(PhysicsChainReadbackFields.None)]
    [TestCase((PhysicsChainReadbackFields)0x8000)]
    public void NoKnownFieldsReturnsZeroOutputs(PhysicsChainReadbackFields fields)
    {
        PhysicsChainReadbackLayout.TryCalculate(fields, 2, out int elements, out int bytes).ShouldBeFalse();
        elements.ShouldBe(0);
        bytes.ShouldBe(0);
    }

    [Test]
    public void UnknownBitsDoNotChangeKnownFieldSizes()
    {
        PhysicsChainReadbackFields fields = PhysicsChainReadbackFields.Particles |
            (PhysicsChainReadbackFields)0x8000;
        PhysicsChainReadbackLayout.TryCalculate(fields, 2, out int elements, out int bytes).ShouldBeTrue();
        elements.ShouldBe(2);
        bytes.ShouldBe(48);
    }

    [TestCase(PhysicsChainReadbackFields.Particles, 1, 24)]
    [TestCase((PhysicsChainReadbackFields)63, 6, 232)]
    public void ByteLimitAcceptsLastWholeSelectionAndRejectsTheNext(
        PhysicsChainReadbackFields fields,
        int elementsPerSelection,
        int bytesPerSelection)
    {
        int lastSelection = int.MaxValue / bytesPerSelection;
        PhysicsChainReadbackLayout.TryCalculate(fields, lastSelection, out int elements, out int bytes)
            .ShouldBeTrue();
        elements.ShouldBe(lastSelection * elementsPerSelection);
        bytes.ShouldBe(lastSelection * bytesPerSelection);

        PhysicsChainReadbackLayout.TryCalculate(fields, lastSelection + 1, out elements, out bytes)
            .ShouldBeFalse();
        elements.ShouldBe(0);
        bytes.ShouldBe(0);
    }

    [Test]
    public void CombinedFieldSizeCalculationDoesNotAllocateAfterWarmup()
    {
        const PhysicsChainReadbackFields fields = PhysicsChainReadbackFields.Particles |
            PhysicsChainReadbackFields.Bones | PhysicsChainReadbackFields.Bounds;
        for (int iteration = 0; iteration < 64; ++iteration)
            PhysicsChainReadbackLayout.TryCalculate(fields, 2, out _, out _);

        bool succeeded = true;
        long totalElements = 0;
        long totalBytes = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1024; ++iteration)
        {
            succeeded &= PhysicsChainReadbackLayout.TryCalculate(fields, 2, out int elements, out int bytes);
            totalElements += elements;
            totalBytes += bytes;
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        succeeded.ShouldBeTrue();
        totalElements.ShouldBe(6L * 1024);
        totalBytes.ShouldBe(192L * 1024);
        allocatedBytes.ShouldBe(0L);
    }
}
