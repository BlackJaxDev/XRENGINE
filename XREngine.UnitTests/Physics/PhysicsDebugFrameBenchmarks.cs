using System.Diagnostics;
using System.Numerics;
using NUnit.Framework;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[Explicit("Performance harness; run only through Tools/Benchmarks/Measure-PhysicsDebugFrames.ps1.")]
public sealed class PhysicsDebugFrameBenchmarks
{
    [TestCase(10_000, false)]
    [TestCase(10_000, true)]
    [TestCase(100_000, false)]
    [TestCase(100_000, true)]
    [TestCase(1_000_000, false)]
    [TestCase(1_000_000, true)]
    public void MeasurePackedPublication(int primitiveCount, bool triangleHeavy)
    {
        int iterations = GetPositiveEnvironmentInt("XRE_PHYSICS_DEBUG_BENCH_ITERATIONS", 20);
        PhysicsDebugFramePublisher publisher = new()
        {
            Budget = new PhysicsDebugBudget(
                primitiveCount,
                primitiveCount,
                primitiveCount,
                int.MaxValue),
        };

        PublishMix(publisher, primitiveCount, triangleHeavy);
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long startTicks = Stopwatch.GetTimestamp();
        for (int iteration = 0; iteration < iterations; iteration++)
            PublishMix(publisher, primitiveCount, triangleHeavy);
        long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        TestContext.Out.WriteLine(
            $"physics-debug-frame,count={primitiveCount},triangleHeavy={triangleHeavy}," +
            $"iterations={iterations},totalMs={elapsedMilliseconds:F4}," +
            $"meanMs={elapsedMilliseconds / iterations:F4},allocatedBytes={allocatedBytes}");
    }

    [TestCase(10_000, false)]
    [TestCase(10_000, true)]
    [TestCase(100_000, false)]
    [TestCase(100_000, true)]
    [TestCase(1_000_000, false)]
    [TestCase(1_000_000, true)]
    public void MeasurePreallocatedPackedBaseline(int primitiveCount, bool triangleHeavy)
    {
        int iterations = GetPositiveEnvironmentInt("XRE_PHYSICS_DEBUG_BENCH_ITERATIONS", 20);
        PhysicsDebugLine[] lines = triangleHeavy ? [] : new PhysicsDebugLine[primitiveCount];
        PhysicsDebugTriangle[] triangles = triangleHeavy ? new PhysicsDebugTriangle[primitiveCount] : [];
        FillPackedBaseline(lines, triangles);

        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long startTicks = Stopwatch.GetTimestamp();
        uint checksum = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
            checksum ^= FillPackedBaseline(lines, triangles);
        long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        TestContext.Out.WriteLine(
            $"normal-packed-baseline,count={primitiveCount},triangleHeavy={triangleHeavy}," +
            $"iterations={iterations},totalMs={elapsedMilliseconds:F4}," +
            $"meanMs={elapsedMilliseconds / iterations:F4},allocatedBytes={allocatedBytes}," +
            $"checksum={checksum}");
    }

    private static void PublishMix(
        PhysicsDebugFramePublisher publisher,
        int primitiveCount,
        bool triangleHeavy)
    {
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.PhysX,
            PhysicsDebugDepthMode.DepthTested)!;
        for (int index = 0; index < primitiveCount; index++)
        {
            Vector3 position = new(index, index & 31, index & 127);
            if (triangleHeavy)
            {
                writer.AddTriangle(new PhysicsDebugTriangle(
                    position,
                    position + Vector3.UnitX,
                    position + Vector3.UnitY,
                    (uint)index));
            }
            else
            {
                writer.AddLine(new PhysicsDebugLine(
                    position,
                    position + Vector3.UnitX,
                    (uint)index));
            }
        }

        writer.CompleteSourceCountsFromPublished();
        writer.Publish();
        publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease);
        lease.Dispose();
    }

    private static uint FillPackedBaseline(
        Span<PhysicsDebugLine> lines,
        Span<PhysicsDebugTriangle> triangles)
    {
        uint checksum = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            Vector3 position = new(index, index & 31, index & 127);
            lines[index] = new PhysicsDebugLine(position, position + Vector3.UnitX, (uint)index);
            checksum ^= lines[index].Color;
        }

        for (int index = 0; index < triangles.Length; index++)
        {
            Vector3 position = new(index, index & 31, index & 127);
            triangles[index] = new PhysicsDebugTriangle(
                position,
                position + Vector3.UnitX,
                position + Vector3.UnitY,
                (uint)index);
            checksum ^= triangles[index].Color;
        }

        return checksum;
    }

    private static int GetPositiveEnvironmentInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
            ? value
            : fallback;
}
