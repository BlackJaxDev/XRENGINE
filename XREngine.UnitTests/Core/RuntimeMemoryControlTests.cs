using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Runtime.Ecs;
using XREngine.Data.Runtime.Memory;
using XREngine.Networking;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class RuntimeMemoryControlTests
{
    [Test]
    public unsafe void FrameScratch_AlignsTracksOverflowAndRejectsStaleLease()
    {
        using FrameScratchAllocator allocator = new(64);
        Span<int> ints = allocator.Allocate<int>(4, 16);
        fixed (int* ptr = ints)
            (((nuint)ptr) % 16u).ShouldBe(0u);

        using FrameScratchLease<int> lease = allocator.Rent<int>(2);
        allocator.Allocate<byte>(128, 16);
        FrameScratchStatistics stats = allocator.Statistics;
        stats.OverflowCount.ShouldBe(1);
        stats.FallbackAllocationCount.ShouldBe(1);
        stats.HighWaterBytes.ShouldBeGreaterThan(0);

        allocator.Reset();
        bool rejectedStaleLease = false;
        try
        {
            Span<int> stale = lease.Span;
            _ = stale.Length;
        }
        catch (InvalidOperationException)
        {
            rejectedStaleLease = true;
        }

        rejectedStaleLease.ShouldBeTrue();
    }

    [Test]
    public void PooledArray_ReturnsAndTracksLeases()
    {
        PooledArrayStatistics before = PooledArray.GetStatistics();
        using (PooledArray<int> rented = PooledArray.Rent<int>(32, clearOnReturn: true))
        {
            rented.Length.ShouldBe(32);
            rented.Span[0] = 42;
        }

        PooledArrayStatistics after = PooledArray.GetStatistics();
        after.RentCount.ShouldBe(before.RentCount + 1);
        after.ReturnCount.ShouldBe(before.ReturnCount + 1);
    }

    [Test]
    public void HotPathObjectPool_RentReleaseDoesNotAllocateAfterPrewarm()
    {
        HotPathObjectPool<PooledBox> pool = new(static () => new PooledBox(), localCapacity: 16);
        pool.Prewarm(16);
        PooledBox warmup = pool.Rent();
        pool.Release(warmup);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            PooledBox item = pool.Rent();
            item.Value = i;
            pool.Release(item);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0);
        pool.Statistics.RentMissCount.ShouldBe(0);
    }

    [Test]
    public void RuntimeEntityWorld_TracksDirtyRangesAndSyntheticSystemsStayAllocationFree()
    {
        const int entityCount = 1024;
        RuntimeEntityWorld world = new();
        world.EnsureEntityCapacity(entityCount);

        RuntimeComponentStore<AvatarPoseComponent> poses = world.Store<AvatarPoseComponent>();
        RuntimeComponentStore<AvatarInterpolationComponent> interpolation = world.Store<AvatarInterpolationComponent>();
        RuntimeComponentStore<AvatarRenderComponent> render = world.Store<AvatarRenderComponent>();
        poses.EnsureCapacity(entityCount);
        interpolation.EnsureCapacity(entityCount);
        render.EnsureCapacity(entityCount);
        poses.EnsureEntityCapacity(entityCount);
        interpolation.EnsureEntityCapacity(entityCount);
        render.EnsureEntityCapacity(entityCount);

        for (int i = 0; i < entityCount; i++)
        {
            RuntimeEntity entity = world.CreateEntity();
            poses.Set(entity, new AvatarPoseComponent(new Vector3(i, 1, 2), Quaternion.Identity));
            interpolation.Set(entity, new AvatarInterpolationComponent(Vector3.Zero));
            render.Set(entity, new AvatarRenderComponent(Vector3.Zero));
        }

        poses.HasDirtyRange.ShouldBeTrue();
        poses.DirtyRangeStart.ShouldBe(0);
        poses.DirtyRangeEndExclusive.ShouldBe(entityCount);
        poses.ClearDirty();
        poses.HasDirtyRange.ShouldBeFalse();

        using FrameScratchAllocator scratch = new(128 * 1024);
        var network = new AvatarNetworkReceiveSystem();
        var interp = new AvatarInterpolationSystem();
        var renderPublish = new AvatarRenderPublishSystem();
        RuntimeSystemAllocationStats networkStats = default;
        RuntimeSystemAllocationStats interpStats = default;
        RuntimeSystemAllocationStats renderStats = default;

        for (int i = 0; i < 8; i++)
        {
            scratch.Reset();
            RuntimeSystemRunner.Execute(world, network, scratch, ref networkStats);
            RuntimeSystemRunner.Execute(world, interp, scratch, ref interpStats);
            RuntimeSystemRunner.Execute(world, renderPublish, scratch, ref renderStats);
        }

        networkStats = default;
        interpStats = default;
        renderStats = default;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            scratch.Reset();
            RuntimeSystemRunner.Execute(world, network, scratch, ref networkStats);
            RuntimeSystemRunner.Execute(world, interp, scratch, ref interpStats);
            RuntimeSystemRunner.Execute(world, renderPublish, scratch, ref renderStats);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0);
        networkStats.OverBudgetCount.ShouldBe(0);
        interpStats.OverBudgetCount.ShouldBe(0);
        renderStats.OverBudgetCount.ShouldBe(0);
        world.EntityCapacityGrowthCount.ShouldBe(1);
        poses.CapacityGrowthCount.ShouldBe(1);
    }

    [Test]
    public void RuntimeRangePartitioner_ProducesNonOverlappingRanges()
    {
        Span<RuntimeRange> ranges = stackalloc RuntimeRange[8];
        int count = RuntimeRangePartitioner.Partition(100, 8, 16, ranges);
        count.ShouldBe(7);

        int expectedStart = 0;
        for (int i = 0; i < count; i++)
        {
            ranges[i].Start.ShouldBe(expectedStart);
            ranges[i].End.ShouldBeGreaterThan(ranges[i].Start);
            expectedStart = ranges[i].End;
        }

        expectedStart.ShouldBe(100);
    }

    [Test]
    public void HumanoidPoseSpanCodec_EncodeDecodeIsAllocationFreeAfterWarmup()
    {
        FixedQuantizedHumanoidPose baseline = HumanoidPoseCodec.QuantizeFixed(CreatePose(0));
        FixedQuantizedHumanoidPose current = HumanoidPoseCodec.QuantizeFixed(CreatePose(1));
        byte[] buffer = new byte[256];
        HumanoidPoseAvatarHeader header = default;
        FixedQuantizedHumanoidPose decoded = default;
        int bytes = 0;

        for (int i = 0; i < 16; i++)
        {
            HumanoidPoseCodec.TryWriteBaselineAvatar(buffer, 7, baseline, 1, out bytes, baselineSequence: 12).ShouldBeTrue();
            HumanoidPoseCodec.TryReadBaselineAvatarFixed(buffer.AsSpan(0, bytes), out header, out decoded, out _).ShouldBeTrue();
            HumanoidPoseCodec.TryWriteDeltaAvatar(buffer, 7, current, baseline, 1, out bytes).ShouldBeTrue();
            HumanoidPoseCodec.TryReadDeltaAvatar(buffer.AsSpan(0, bytes), baseline, out header, out decoded, out _).ShouldBeTrue();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = true;
        for (int i = 0; i < 1024; i++)
        {
            ok &= HumanoidPoseCodec.TryWriteBaselineAvatar(buffer, 7, baseline, 1, out bytes, baselineSequence: 12);
            ok &= HumanoidPoseCodec.TryReadBaselineAvatarFixed(buffer.AsSpan(0, bytes), out header, out decoded, out _);
            ok &= HumanoidPoseCodec.TryWriteDeltaAvatar(buffer, 7, current, baseline, 1, out bytes);
            ok &= HumanoidPoseCodec.TryReadDeltaAvatar(buffer.AsSpan(0, bytes), baseline, out header, out decoded, out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ok.ShouldBeTrue();
        allocated.ShouldBe(0);
        header.EntityId.ShouldBe((ushort)7);
        decoded.Head.ShouldBe(current.Head);
    }

    [Test]
    public void HumanoidPoseSpanPacketWriter_BatchesAndCursorReadsWithoutAllocations()
    {
        const int avatarCount = 256;
        FixedQuantizedHumanoidPose[] poses = new FixedQuantizedHumanoidPose[avatarCount];
        for (int i = 0; i < poses.Length; i++)
            poses[i] = HumanoidPoseCodec.QuantizeFixed(CreatePose(i));

        byte[] packet = new byte[avatarCount * HumanoidPoseCodec.BaselineAvatarBytes];
        WarmupPacketWriter(packet, poses);

        long before = GC.GetAllocatedBytesForCurrentThread();
        HumanoidPoseSpanPacketWriter writer = new(packet);
        writer.BeginFrame(HumanoidPosePacketKind.Baseline, 33);
        bool ok = true;
        for (int i = 0; i < poses.Length; i++)
            ok &= writer.TryAddBaselineAvatar((ushort)i, poses[i]);

        HumanoidPosePacketCursor cursor = new(writer.Payload);
        int read = 0;
        while (cursor.HasRemaining)
        {
            ok &= cursor.TryReadNextBaseline(out HumanoidPoseAvatarHeader header, out FixedQuantizedHumanoidPose pose);
            ok &= header.EntityId == read;
            ok &= pose.Hip == poses[read].Hip;
            read++;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ok.ShouldBeTrue();
        read.ShouldBe(avatarCount);
        allocated.ShouldBe(0);
    }

    [Test]
    public void RealtimeSendRingAndReceiveSlabPool_AreBoundedAndAllocationFreeAfterConstruction()
    {
        RealtimePacketSendRing ring = new(capacity: 4, slotSizeBytes: 128);
        PersistentReceiveSlabPool slabs = new(capacity: 2, slabSizeBytes: 128);
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        ring.TryEnqueue(payload).ShouldBeTrue();
        ring.TryDequeue(out _).ShouldBeTrue();
        slabs.TryRent(out PersistentReceiveSlab slab).ShouldBeTrue();
        slabs.Return(slab);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = true;
        for (int i = 0; i < 256; i++)
        {
            ok &= ring.TryEnqueue(payload);
            ok &= ring.TryDequeue(out ReadOnlyMemory<byte> dequeued);
            ok &= dequeued.Length == payload.Length;
            ok &= slabs.TryRent(out slab);
            payload.AsSpan().CopyTo(slab.WritableSpan);
            slab.Length = payload.Length;
            ok &= slab.Span[0] == payload[0];
            slabs.Return(slab);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ok.ShouldBeTrue();
        allocated.ShouldBe(0);

        ring.TryEnqueue(payload).ShouldBeTrue();
        ring.TryEnqueue(payload).ShouldBeTrue();
        ring.TryEnqueue(payload).ShouldBeTrue();
        ring.TryEnqueue(payload).ShouldBeTrue();
        ring.TryEnqueue(payload).ShouldBeFalse();
        ring.Stats.DropCount.ShouldBe(1);

        slabs.TryRent(out PersistentReceiveSlab first).ShouldBeTrue();
        slabs.TryRent(out PersistentReceiveSlab second).ShouldBeTrue();
        slabs.TryRent(out _).ShouldBeFalse();
        slabs.Stats.RentMissCount.ShouldBe(1);
        slabs.Return(first);
        slabs.Return(second);
    }

    [Test]
    public void NativeMemoryPressureLease_PairsAddAndRemove()
    {
        NativeMemoryPressureSnapshot before = NativeMemoryPressureTracker.Snapshot;
        using (NativeMemoryPressureLease lease = NativeMemoryPressureTracker.Add(4096))
        {
            lease.Bytes.ShouldBe(4096);
            NativeMemoryPressureTracker.Snapshot.ActiveBytes.ShouldBe(before.ActiveBytes + 4096);
        }

        NativeMemoryPressureSnapshot after = NativeMemoryPressureTracker.Snapshot;
        after.ActiveBytes.ShouldBe(before.ActiveBytes);
        after.AddCount.ShouldBe(before.AddCount + 1);
        after.RemoveCount.ShouldBe(before.RemoveCount + 1);
    }

    private static void WarmupPacketWriter(byte[] packet, FixedQuantizedHumanoidPose[] poses)
    {
        HumanoidPoseSpanPacketWriter writer = new(packet);
        writer.BeginFrame(HumanoidPosePacketKind.Baseline, 1);
        for (int i = 0; i < poses.Length; i++)
            writer.TryAddBaselineAvatar((ushort)i, poses[i]);

        HumanoidPosePacketCursor cursor = new(writer.Payload);
        while (cursor.HasRemaining)
            cursor.TryReadNextBaseline(out _, out _);
    }

    private static HumanoidPoseSample CreatePose(int offset)
        => new(
            new Vector3(offset * 0.1f, 1.7f, offset * -0.05f),
            offset * 0.01f,
            new Vector3(0, 0.9f, 0),
            new Vector3(0, 1.7f, 0),
            new Vector3(-0.45f, 1.3f, 0.1f + offset * 0.001f),
            new Vector3(0.45f, 1.3f, -0.1f - offset * 0.001f),
            new Vector3(-0.2f, 0.05f, 0),
            new Vector3(0.2f, 0.05f, 0));

    private sealed class PooledBox
    {
        public int Value;
    }

    private readonly record struct AvatarPoseComponent(Vector3 Position, Quaternion Rotation) : IRuntimeComponent;
    private readonly record struct AvatarInterpolationComponent(Vector3 PreviousPosition) : IRuntimeComponent;
    private readonly record struct AvatarRenderComponent(Vector3 PublishedPosition) : IRuntimeComponent;

    private sealed class AvatarNetworkReceiveSystem : IRuntimeSystem
    {
        public RuntimeSystemPhase Phase => RuntimeSystemPhase.NetworkReceive;
        public long AllocationBudgetBytes => 0;

        public void Execute(ref RuntimeSystemContext context)
        {
            RuntimeComponentStore<AvatarPoseComponent> poses = context.World.Store<AvatarPoseComponent>();
            Span<RuntimeRange> ranges = context.Scratch.Allocate<RuntimeRange>(16);
            RuntimeRangePartitioner.Partition(poses.Count, ranges.Length, 128, ranges);
            Span<AvatarPoseComponent> components = poses.Components;
            for (int i = 0; i < components.Length; i++)
                components[i] = components[i] with { Position = components[i].Position + new Vector3(0.001f, 0, 0) };
        }
    }

    private sealed class AvatarInterpolationSystem : IRuntimeSystem
    {
        public RuntimeSystemPhase Phase => RuntimeSystemPhase.Interpolation;
        public long AllocationBudgetBytes => 0;

        public void Execute(ref RuntimeSystemContext context)
        {
            RuntimeComponentStore<AvatarPoseComponent> poses = context.World.Store<AvatarPoseComponent>();
            RuntimeComponentStore<AvatarInterpolationComponent> interpolation = context.World.Store<AvatarInterpolationComponent>();
            Span<AvatarPoseComponent> poseComponents = poses.Components;
            Span<AvatarInterpolationComponent> interpComponents = interpolation.Components;
            for (int i = 0; i < poseComponents.Length; i++)
                interpComponents[i] = new AvatarInterpolationComponent(Vector3.Lerp(interpComponents[i].PreviousPosition, poseComponents[i].Position, 0.25f));
        }
    }

    private sealed class AvatarRenderPublishSystem : IRuntimeSystem
    {
        public RuntimeSystemPhase Phase => RuntimeSystemPhase.RenderPrepare;
        public long AllocationBudgetBytes => 0;

        public void Execute(ref RuntimeSystemContext context)
        {
            RuntimeComponentStore<AvatarInterpolationComponent> interpolation = context.World.Store<AvatarInterpolationComponent>();
            RuntimeComponentStore<AvatarRenderComponent> render = context.World.Store<AvatarRenderComponent>();
            Span<AvatarInterpolationComponent> interpComponents = interpolation.Components;
            Span<AvatarRenderComponent> renderComponents = render.Components;
            for (int i = 0; i < renderComponents.Length; i++)
                renderComponents[i] = new AvatarRenderComponent(interpComponents[i].PreviousPosition);
        }
    }
}
