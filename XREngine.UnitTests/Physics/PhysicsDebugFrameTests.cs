using System.Numerics;
using System.Runtime.InteropServices;
using MagicPhysX;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Colors;
using XREngine.Scene.Physics.DebugVisualization;
using XREngine.Scene.Physics.Physx;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsDebugFrameTests
{
    [Test]
    public void PackedPrimitiveLayouts_MatchCompressedShaderStorageRecords()
    {
        Marshal.SizeOf<PhysicsDebugPoint>().ShouldBe(16);
        Marshal.SizeOf<PhysicsDebugLine>().ShouldBe(28);
        Marshal.SizeOf<PhysicsDebugTriangle>().ShouldBe(40);
    }

    [Test]
    public unsafe void PhysxAdapter_CopiesSyntheticNativeBufferExactly()
    {
        PxDebugPoint* points = stackalloc PxDebugPoint[1];
        points[0].pos = new PxVec3 { x = 1, y = 2, z = 3 };
        points[0].color = 0x44332211u;
        PxDebugLine* lines = stackalloc PxDebugLine[1];
        lines[0].pos0 = new PxVec3 { x = 4, y = 5, z = 6 };
        lines[0].pos1 = new PxVec3 { x = 7, y = 8, z = 9 };
        lines[0].color0 = 0x88776655u;
        PxDebugTriangle* triangles = stackalloc PxDebugTriangle[1];
        triangles[0].pos0 = new PxVec3 { x = 10, y = 11, z = 12 };
        triangles[0].pos1 = new PxVec3 { x = 13, y = 14, z = 15 };
        triangles[0].pos2 = new PxVec3 { x = 16, y = 17, z = 18 };
        triangles[0].color0 = 0xCCBBAA99u;

        PhysicsDebugFramePublisher publisher = new();
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.PhysX,
            PhysicsDebugDepthMode.DepthTested,
            1,
            1,
            1)!;
        PhysxDebugFrameAdapter.Copy(points, 1, lines, 1, triangles, 1, writer);
        writer.Publish();

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease).ShouldBeTrue();
        using (lease)
        {
            lease.Frame.Points[0].ShouldBe(
                new PhysicsDebugPoint(new Vector3(1, 2, 3), 0x44332211u));
            lease.Frame.Lines[0].ShouldBe(
                new PhysicsDebugLine(new Vector3(4, 5, 6), new Vector3(7, 8, 9), 0x88776655u));
            lease.Frame.Triangles[0].ShouldBe(
                new PhysicsDebugTriangle(
                    new Vector3(10, 11, 12),
                    new Vector3(13, 14, 15),
                    new Vector3(16, 17, 18),
                    0xCCBBAA99u));
        }
    }

    [TestCase(PhysicsDebugSource.PhysX)]
    [TestCase(PhysicsDebugSource.Jolt)]
    public void PublishedFrame_PreservesPackedPrimitiveData(PhysicsDebugSource source)
    {
        PhysicsDebugFramePublisher publisher = new();
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(source, PhysicsDebugDepthMode.DepthTested)!;
        uint red = PhysicsDebugColor.Pack(ColorF4.Red);
        uint green = PhysicsDebugColor.Pack(ColorF4.Green);
        uint blue = PhysicsDebugColor.Pack(ColorF4.Blue);

        writer.AddPoint(new PhysicsDebugPoint(new Vector3(1, 2, 3), red)).ShouldBeTrue();
        writer.AddLine(new PhysicsDebugLine(new Vector3(4, 5, 6), new Vector3(7, 8, 9), green)).ShouldBeTrue();
        writer.AddTriangle(new PhysicsDebugTriangle(
            new Vector3(10, 11, 12),
            new Vector3(13, 14, 15),
            new Vector3(16, 17, 18),
            blue)).ShouldBeTrue();
        writer.CompleteSourceCountsFromPublished();
        writer.Publish();

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease).ShouldBeTrue();
        using (lease)
        {
            PhysicsDebugFrame frame = lease.Frame;
            frame.Source.ShouldBe(source);
            frame.PointCount.ShouldBe(1);
            frame.LineCount.ShouldBe(1);
            frame.TriangleCount.ShouldBe(1);
            frame.Points[0].ShouldBe(new PhysicsDebugPoint(new Vector3(1, 2, 3), red));
            frame.Lines[0].ShouldBe(new PhysicsDebugLine(new Vector3(4, 5, 6), new Vector3(7, 8, 9), green));
            frame.Triangles[0].Color.ShouldBe(blue);
            frame.Telemetry.PublishedByteCount.ShouldBe(16 + 28 + 40);
        }
    }

    [Test]
    public void Triangle_RemainsOneTriangleAndDoesNotCreateLines()
    {
        PhysicsDebugFramePublisher publisher = new();
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.PhysX,
            PhysicsDebugDepthMode.DepthTested)!;
        writer.AddTriangle(new PhysicsDebugTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, uint.MaxValue));
        writer.CompleteSourceCountsFromPublished();
        writer.Publish();

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease).ShouldBeTrue();
        using (lease)
        {
            lease.Frame.TriangleCount.ShouldBe(1);
            lease.Frame.LineCount.ShouldBe(0);
        }
    }

    [Test]
    public void DisabledFrame_IsPublishedEmptyWithoutAllocatingPrimitiveStorage()
    {
        PhysicsDebugFramePublisher publisher = new();
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.PhysX,
            PhysicsDebugDepthMode.DepthTested)!;
        writer.Publish();

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease).ShouldBeTrue();
        using (lease)
        {
            lease.Frame.IsEmpty.ShouldBeTrue();
            lease.Frame.Points.Length.ShouldBe(0);
            lease.Frame.Lines.Length.ShouldBe(0);
            lease.Frame.Triangles.Length.ShouldBe(0);
        }
    }

    [Test]
    public void MultipleConsumers_ObserveOneGeneration()
    {
        PhysicsDebugFramePublisher publisher = PublishSinglePointFrame();

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease first).ShouldBeTrue();
        publisher.TryAcquireLatest(out PhysicsDebugFrameLease second).ShouldBeTrue();
        using (first)
        using (second)
        {
            first.Frame.Generation.ShouldBe(second.Frame.Generation);
            first.Frame.Points[0].ShouldBe(second.Frame.Points[0]);
        }
    }

    [Test]
    public void PinnedOldGeneration_IsNotMutatedByLaterPublications()
    {
        PhysicsDebugFramePublisher publisher = PublishSinglePointFrame();
        publisher.TryAcquireLatest(out PhysicsDebugFrameLease pinned).ShouldBeTrue();
        long pinnedGeneration = pinned.Frame.Generation;
        PhysicsDebugPoint pinnedPoint = pinned.Frame.Points[0];

        PublishSinglePoint(publisher, new Vector3(2, 0, 0));
        PublishSinglePoint(publisher, new Vector3(3, 0, 0));

        pinned.Frame.Generation.ShouldBe(pinnedGeneration);
        pinned.Frame.Points[0].ShouldBe(pinnedPoint);
        pinned.Dispose();
    }

    [Test]
    public void PrimitiveAndByteBudgets_PreserveDeterministicPrefixAndReportOverflow()
    {
        PhysicsDebugFramePublisher publisher = new()
        {
            Budget = new PhysicsDebugBudget(2, 2, 2, 32),
        };
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.Jolt,
            PhysicsDebugDepthMode.DepthTested)!;

        writer.AddPoint(new PhysicsDebugPoint(Vector3.Zero, 1)).ShouldBeTrue();
        writer.AddPoint(new PhysicsDebugPoint(Vector3.One, 2)).ShouldBeTrue();
        writer.AddPoint(new PhysicsDebugPoint(Vector3.UnitX, 3)).ShouldBeFalse();
        writer.CompleteSourceCountsFromPublished();
        writer.Publish();

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease).ShouldBeTrue();
        using (lease)
        {
            lease.Frame.Points.ToArray().ShouldBe(
            [
                new PhysicsDebugPoint(Vector3.Zero, 1),
                new PhysicsDebugPoint(Vector3.One, 2),
            ]);
            lease.Frame.Telemetry.DroppedPointCount.ShouldBe(1);
        }
    }

    [Test]
    public void UnpublishedWrites_AreNeverVisible()
    {
        PhysicsDebugFramePublisher publisher = PublishSinglePointFrame();
        publisher.TryAcquireLatest(out PhysicsDebugFrameLease before).ShouldBeTrue();
        long publishedGeneration = before.Frame.Generation;
        before.Dispose();

        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.PhysX,
            PhysicsDebugDepthMode.DepthTested)!;
        writer.AddPoint(new PhysicsDebugPoint(new Vector3(99), 99));

        publisher.TryAcquireLatest(out PhysicsDebugFrameLease during).ShouldBeTrue();
        using (during)
            during.Frame.Generation.ShouldBe(publishedGeneration);

        writer.Publish();
    }

    private static PhysicsDebugFramePublisher PublishSinglePointFrame()
    {
        PhysicsDebugFramePublisher publisher = new();
        PublishSinglePoint(publisher, Vector3.One);
        return publisher;
    }

    private static void PublishSinglePoint(PhysicsDebugFramePublisher publisher, Vector3 position)
    {
        PhysicsDebugFrameWriter writer = publisher.BeginWrite(
            PhysicsDebugSource.PhysX,
            PhysicsDebugDepthMode.DepthTested)!;
        writer.AddPoint(new PhysicsDebugPoint(position, uint.MaxValue));
        writer.CompleteSourceCountsFromPublished();
        writer.Publish();
    }
}
