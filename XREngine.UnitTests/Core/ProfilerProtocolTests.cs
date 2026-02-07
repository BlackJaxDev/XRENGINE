using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;

namespace XREngine.UnitTests.Core;

/// <summary>
/// Verifies MemoryPack serialize → deserialize round-trips for every profiler packet type,
/// and tests the wire-protocol framing helpers in <see cref="ProfilerProtocol"/>.
/// </summary>
[TestFixture]
public sealed class ProfilerProtocolTests
{
    // ── ProfilerFramePacket ────────────────────────────────────────────

    [Test]
    public void ProfilerFramePacket_RoundTrip()
    {
        var original = new ProfilerFramePacket
        {
            FrameTime = 12.345f,
            Threads =
            [
                new ProfilerThreadData
                {
                    ThreadId = 1,
                    TotalTimeMs = 3.5f,
                    RootNodes =
                    [
                        new ProfilerNodeData
                        {
                            Name = "Update",
                            ElapsedMs = 2.0f,
                            Children =
                            [
                                new ProfilerNodeData { Name = "Physics", ElapsedMs = 1.5f, Children = [] }
                            ]
                        },
                        new ProfilerNodeData { Name = "Render", ElapsedMs = 1.5f, Children = [] }
                    ]
                },
                new ProfilerThreadData
                {
                    ThreadId = 7,
                    TotalTimeMs = 1.0f,
                    RootNodes = [new ProfilerNodeData { Name = "Audio", ElapsedMs = 1.0f, Children = [] }]
                }
            ],
            ThreadHistory = new Dictionary<int, float[]>
            {
                [1] = [1.0f, 2.0f, 3.0f],
                [7] = [0.5f, 0.8f]
            }
        };

        var clone = RoundTrip(original);

        clone.FrameTime.ShouldBe(original.FrameTime);
        clone.Threads.Length.ShouldBe(2);
        clone.Threads[0].ThreadId.ShouldBe(1);
        clone.Threads[0].TotalTimeMs.ShouldBe(3.5f);
        clone.Threads[0].RootNodes.Length.ShouldBe(2);
        clone.Threads[0].RootNodes[0].Name.ShouldBe("Update");
        clone.Threads[0].RootNodes[0].Children.Length.ShouldBe(1);
        clone.Threads[0].RootNodes[0].Children[0].Name.ShouldBe("Physics");
        clone.Threads[1].ThreadId.ShouldBe(7);
        clone.ThreadHistory.Count.ShouldBe(2);
        clone.ThreadHistory[1].ShouldBe(new[] { 1.0f, 2.0f, 3.0f });
    }

    [Test]
    public void ProfilerFramePacket_EmptyThreads_RoundTrip()
    {
        var original = new ProfilerFramePacket { FrameTime = 0.0f, Threads = [], ThreadHistory = [] };
        var clone = RoundTrip(original);
        clone.Threads.Length.ShouldBe(0);
        clone.ThreadHistory.Count.ShouldBe(0);
    }

    // ── RenderStatsPacket ──────────────────────────────────────────────

    [Test]
    public void RenderStatsPacket_RoundTrip()
    {
        var original = new RenderStatsPacket
        {
            DrawCalls = 1234,
            MultiDrawCalls = 56,
            TrianglesRendered = 9_876_543,
            AllocatedVRAMBytes = 512 * 1024 * 1024L,
            AllocatedBufferBytes = 128 * 1024 * 1024L,
            AllocatedTextureBytes = 256 * 1024 * 1024L,
            AllocatedRenderBufferBytes = 64 * 1024 * 1024L,
            FBOBandwidthBytes = 100_000_000,
            FBOBindCount = 42,
            RenderMatrixStatsReady = true,
            RenderMatrixApplied = 300,
            RenderMatrixSetCalls = 150,
            RenderMatrixListenerInvocations = 900,
            RenderMatrixListenerCounts =
            [
                new RenderMatrixListenerEntry { Name = "MeshRenderer", Count = 500 },
                new RenderMatrixListenerEntry { Name = "LightProbe", Count = 200 }
            ],
            OctreeStatsReady = true,
            OctreeAddCount = 10,
            OctreeMoveCount = 20,
            OctreeRemoveCount = 5,
            OctreeSkippedMoveCount = 3,
        };

        var clone = RoundTrip(original);

        clone.DrawCalls.ShouldBe(1234);
        clone.MultiDrawCalls.ShouldBe(56);
        clone.TrianglesRendered.ShouldBe(9_876_543);
        clone.AllocatedVRAMBytes.ShouldBe(512 * 1024 * 1024L);
        clone.FBOBandwidthBytes.ShouldBe(100_000_000);
        clone.FBOBindCount.ShouldBe(42);
        clone.RenderMatrixStatsReady.ShouldBeTrue();
        clone.RenderMatrixApplied.ShouldBe(300);
        clone.RenderMatrixListenerCounts.Length.ShouldBe(2);
        clone.RenderMatrixListenerCounts[0].Name.ShouldBe("MeshRenderer");
        clone.RenderMatrixListenerCounts[0].Count.ShouldBe(500);
        clone.OctreeStatsReady.ShouldBeTrue();
        clone.OctreeAddCount.ShouldBe(10);
    }

    // ── ThreadAllocationsPacket ────────────────────────────────────────

    [Test]
    public void ThreadAllocationsPacket_RoundTrip()
    {
        var original = new ThreadAllocationsPacket
        {
            Render = new AllocationSlice { LastBytes = 1024, AverageBytes = 900.5, MaxBytes = 2048, Samples = 120, Capacity = 240 },
            CollectSwap = new AllocationSlice { LastBytes = 512, AverageBytes = 400.0, MaxBytes = 1024, Samples = 60, Capacity = 240 },
            Update = new AllocationSlice { LastBytes = 2048, AverageBytes = 1800.0, MaxBytes = 4096, Samples = 200, Capacity = 240 },
            FixedUpdate = new AllocationSlice { LastBytes = 256, AverageBytes = 200.0, MaxBytes = 512, Samples = 30, Capacity = 240 },
        };

        var clone = RoundTrip(original);

        clone.Render.LastBytes.ShouldBe(1024);
        clone.Render.AverageBytes.ShouldBe(900.5);
        clone.Render.MaxBytes.ShouldBe(2048);
        clone.CollectSwap.Samples.ShouldBe(60);
        clone.Update.LastKB.ShouldBe(2.0);
        clone.FixedUpdate.Capacity.ShouldBe(240);
    }

    // ── BvhMetricsPacket ───────────────────────────────────────────────

    [Test]
    public void BvhMetricsPacket_RoundTrip()
    {
        var original = new BvhMetricsPacket
        {
            BuildCount = 100,
            BuildMilliseconds = 1.234,
            RefitCount = 200,
            RefitMilliseconds = 0.567,
            CullCount = 50,
            CullMilliseconds = 0.123,
            RaycastCount = 10,
            RaycastMilliseconds = 0.045,
        };

        var clone = RoundTrip(original);

        clone.BuildCount.ShouldBe(100u);
        clone.BuildMilliseconds.ShouldBe(1.234);
        clone.RefitCount.ShouldBe(200u);
        clone.CullMilliseconds.ShouldBe(0.123);
        clone.RaycastCount.ShouldBe(10u);
    }

    // ── JobSystemStatsPacket ───────────────────────────────────────────

    [Test]
    public void JobSystemStatsPacket_RoundTrip()
    {
        var original = new JobSystemStatsPacket
        {
            WorkerCount = 8,
            IsQueueBounded = true,
            QueueCapacity = 1024,
            QueueSlotsInUse = 37,
            QueueSlotsAvailable = 987,
            Priorities =
            [
                new JobPriorityStatsEntry { Priority = 0, PriorityName = "Lowest", QueuedAny = 5, QueuedMain = 1, QueuedCollect = 0, AvgWaitMs = 12.5 },
                new JobPriorityStatsEntry { Priority = 4, PriorityName = "Highest", QueuedAny = 0, QueuedMain = 0, QueuedCollect = 0, AvgWaitMs = 0.1 },
            ]
        };

        var clone = RoundTrip(original);

        clone.WorkerCount.ShouldBe(8);
        clone.IsQueueBounded.ShouldBeTrue();
        clone.QueueCapacity.ShouldBe(1024);
        clone.Priorities.Length.ShouldBe(2);
        clone.Priorities[0].PriorityName.ShouldBe("Lowest");
        clone.Priorities[0].AvgWaitMs.ShouldBe(12.5);
        clone.Priorities[1].Priority.ShouldBe(4);
    }

    // ── MainThreadInvokesPacket ────────────────────────────────────────

    [Test]
    public void MainThreadInvokesPacket_RoundTrip()
    {
        var original = new MainThreadInvokesPacket
        {
            Entries =
            [
                new MainThreadInvokeEntryData
                {
                    Sequence = 42,
                    TimestampTicks = DateTimeOffset.UtcNow.Ticks,
                    Reason = "SceneLoad",
                    Mode = "Queued",
                    CallerThreadId = 3,
                },
            ]
        };

        var clone = RoundTrip(original);

        clone.Entries.Length.ShouldBe(1);
        clone.Entries[0].Sequence.ShouldBe(42);
        clone.Entries[0].Reason.ShouldBe("SceneLoad");
        clone.Entries[0].Mode.ShouldBe("Queued");
        clone.Entries[0].CallerThreadId.ShouldBe(3);
    }

    [Test]
    public void MainThreadInvokesPacket_Empty_RoundTrip()
    {
        var original = new MainThreadInvokesPacket { Entries = [] };
        var clone = RoundTrip(original);
        clone.Entries.Length.ShouldBe(0);
    }

    // ── HeartbeatPacket ────────────────────────────────────────────────

    [Test]
    public void HeartbeatPacket_RoundTrip()
    {
        var original = new HeartbeatPacket
        {
            ProcessName = "XREngine.Editor",
            ProcessId = 12345,
            UptimeMs = 60_000,
        };

        var clone = RoundTrip(original);

        clone.ProcessName.ShouldBe("XREngine.Editor");
        clone.ProcessId.ShouldBe(12345);
        clone.UptimeMs.ShouldBe(60_000);
    }

    // ── Wire framing ───────────────────────────────────────────────────

    [Test]
    public void WriteFrame_ThenReadFrame_RoundTrips()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02];
        byte[] buffer = new byte[ProfilerProtocol.MaxDatagramSize];

        int written = ProfilerProtocol.WriteFrame(buffer, ProfilerProtocol.MessageType.RenderStats, payload);
        written.ShouldBe(ProfilerProtocol.HeaderSize + payload.Length);

        bool ok = ProfilerProtocol.TryReadFrame(buffer.AsSpan(0, written), out var type, out var readPayload);
        ok.ShouldBeTrue();
        type.ShouldBe(ProfilerProtocol.MessageType.RenderStats);
        readPayload.Length.ShouldBe(payload.Length);
        readPayload.ToArray().ShouldBe(payload);
    }

    [Test]
    public void TryReadFrame_TooShort_ReturnsFalse()
    {
        byte[] tooShort = [0x01, 0x00];
        ProfilerProtocol.TryReadFrame(tooShort, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void WriteFrame_PayloadTooLarge_ReturnsNegative()
    {
        byte[] hugePayload = new byte[ProfilerProtocol.MaxPayloadSize + 1];
        byte[] buffer = new byte[ProfilerProtocol.MaxDatagramSize];
        int result = ProfilerProtocol.WriteFrame(buffer, ProfilerProtocol.MessageType.ProfilerFrame, hugePayload);
        result.ShouldBe(-1);
    }

    // ── Full end-to-end: serialize packet → frame → unframe → deserialize ──

    [Test]
    public void FullPipeline_ProfilerFrame_SerializeFrameDeserialize()
    {
        var original = new ProfilerFramePacket
        {
            FrameTime = 99.9f,
            Threads =
            [
                new ProfilerThreadData
                {
                    ThreadId = 5,
                    TotalTimeMs = 8.0f,
                    RootNodes = [new ProfilerNodeData { Name = "Tick", ElapsedMs = 8.0f, Children = [] }]
                }
            ],
            ThreadHistory = new Dictionary<int, float[]> { [5] = [8.0f] }
        };

        // Serialize
        byte[] payload = MemoryPackSerializer.Serialize(original);

        // Frame
        byte[] buffer = new byte[ProfilerProtocol.MaxDatagramSize];
        int written = ProfilerProtocol.WriteFrame(buffer, ProfilerProtocol.MessageType.ProfilerFrame, payload);
        written.ShouldBeGreaterThan(0);

        // Simulate receive: read frame
        bool ok = ProfilerProtocol.TryReadFrame(buffer.AsSpan(0, written), out var type, out var readPayload);
        ok.ShouldBeTrue();
        type.ShouldBe(ProfilerProtocol.MessageType.ProfilerFrame);

        // Deserialize
        var clone = MemoryPackSerializer.Deserialize<ProfilerFramePacket>(readPayload);
        clone.ShouldNotBeNull();
        clone!.FrameTime.ShouldBe(99.9f);
        clone.Threads[0].RootNodes[0].Name.ShouldBe("Tick");
    }

    // ── helper ─────────────────────────────────────────────────────────

    private static T RoundTrip<T>(T value) where T : class
    {
        byte[] bytes = MemoryPackSerializer.Serialize(value);
        bytes.Length.ShouldBeGreaterThan(0);
        var result = MemoryPackSerializer.Deserialize<T>(bytes);
        result.ShouldNotBeNull();
        return result!;
    }
}
