using NUnit.Framework;
using Shouldly;
using System.Diagnostics;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Scene;
using XREngine.Rendering.Residency;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>
/// Seek accuracy of the FFmpeg media session on generated clips in each supported container, the live stream rule, and the
/// cutscene video track: keyframes during a drag, the exact frame at a stop, cache hits, and seeks off the update thread.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class VideoSeekTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private string _directory = string.Empty;

    private static IEnumerable<TestCaseData> ContainerCases()
    {
        foreach ((string extension, string muxer) in VideoTestClips.Containers)
        {
            yield return new TestCaseData(extension, muxer, false).SetArgDisplayNames(muxer, "raw");

            // QuickTime keeps the bits per sample of qtrle. The other muxers of this FFmpeg build lose the qtrle sample
            // description (Matroska, AVI) or reject the codec (NUT).
            if (muxer == "mov")
                yield return new TestCaseData(extension, muxer, true).SetArgDisplayNames(muxer, "qtrle");
        }
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        if (!VideoTestClips.EnsureFfmpeg())
            Assert.Ignore("The FFmpeg libraries of the project (Build/Dependencies/FFmpeg/HlsReference/win-x64) are not on this machine.");
        _directory = Path.Combine(Path.GetTempPath(), $"xre-video-seek-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (_directory.Length > 0 && Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [TestCaseSource(nameof(ContainerCases))]
    public async Task Exact_ReturnsTheFrameWithTheRequestedTime(string extension, string muxer, bool interCoded)
    {
        using IMediaStreamSession session = await OpenClip(extension, muxer, interCoded);
        session.CanSeek.ShouldBeTrue();

        // Forward, backward, mid-frame, first, and last targets.
        (int Frame, long Offset)[] targets = [(0, 0), (7, 0), (23, 170_000), (31, 0), (49, 0), (10, 399_999), (3, 0), (19, 0), (2, 100_000)];
        foreach ((int expected, long offset) in targets)
        {
            long target = VideoTestClips.TicksOf(expected) + offset;
            await session.SeekAsync(target, EVideoSeekMode.Exact, CancellationToken.None).WaitAsync(Timeout);
            DecodedVideoFrame frame = Dequeue(session, target);
            VideoTestClips.FrameOf(frame).ShouldBe(expected, $"{muxer}: exact seek to {target} ticks");
            frame.PresentationTimestampTicks.ShouldBe(VideoTestClips.TicksOf(expected), $"{muxer}: timestamp of the exact frame");
            frame.DurationTicks.ShouldBe(VideoTestClips.FrameTicks, $"{muxer}: duration of the exact frame");
        }
        TestContext.Out.WriteLine($"{muxer} {(interCoded ? "qtrle" : "raw")}: {targets.Length} exact seeks returned the frame of the target time.");
    }

    [TestCaseSource(nameof(ContainerCases))]
    public async Task NearestKeyframe_NeverLandsAfterTheTarget(string extension, string muxer, bool interCoded)
    {
        using IMediaStreamSession session = await OpenClip(extension, muxer, interCoded);

        int[] targets = [0, 9, 15, 27, 44, 12, 30, 5, 49];
        foreach (int frameIndex in targets)
        {
            long target = VideoTestClips.TicksOf(frameIndex) + 20_000;
            await session.SeekAsync(target, EVideoSeekMode.NearestKeyframe, CancellationToken.None).WaitAsync(Timeout);
            DecodedVideoFrame frame = Dequeue(session, target);
            frame.PresentationTimestampTicks.ShouldBeLessThanOrEqualTo(target, $"{muxer}: keyframe seek to frame {frameIndex}");
            int landed = VideoTestClips.FrameOf(frame);
            frame.PresentationTimestampTicks.ShouldBe(VideoTestClips.TicksOf(landed), $"{muxer}: timestamp of the keyframe");
            if (interCoded)
            {
                // The container index has a keyframe every KeyframeInterval frames.
                int keyframe = frameIndex / VideoTestClips.KeyframeInterval * VideoTestClips.KeyframeInterval;
                landed.ShouldBe(keyframe, $"{muxer}: the keyframe at or before frame {frameIndex}");
            }
            else
            {
                // Every raw frame is a keyframe. A demuxer can still use an earlier sync point.
                landed.ShouldBeLessThanOrEqualTo(frameIndex, $"{muxer}: the keyframe at or before frame {frameIndex}");
            }
        }
        TestContext.Out.WriteLine($"{muxer} {(interCoded ? "qtrle" : "raw")}: {targets.Length} keyframe seeks landed at or before the target.");
    }

    [TestCaseSource(nameof(ContainerCases))]
    public async Task BackwardSeek_AfterPlayback_DecodesForwardFromTheNewFrame(string extension, string muxer, bool interCoded)
    {
        using IMediaStreamSession session = await OpenClip(extension, muxer, interCoded);

        await session.SeekAsync(0, EVideoSeekMode.Exact, CancellationToken.None).WaitAsync(Timeout);
        for (int i = 0; i <= 30; i++)
            VideoTestClips.FrameOf(Dequeue(session, VideoTestClips.TicksOf(i))).ShouldBe(i, $"{muxer}: playback frame {i}");

        await session.SeekAsync(VideoTestClips.TicksOf(5), EVideoSeekMode.Exact, CancellationToken.None).WaitAsync(Timeout);
        for (int i = 5; i <= 12; i++)
            VideoTestClips.FrameOf(Dequeue(session, VideoTestClips.TicksOf(i))).ShouldBe(i, $"{muxer}: playback frame {i} after the backward seek");
    }

    [Test]
    public async Task StaleSeek_SupersededByANewerSeek_IsDropped()
    {
        using IMediaStreamSession session = await OpenClip(".mkv", "matroska");
        await session.SeekAsync(VideoTestClips.TicksOf(45), EVideoSeekMode.Exact, CancellationToken.None).WaitAsync(Timeout);

        for (int round = 0; round < 20; round++)
        {
            Task stale = session.SeekAsync(VideoTestClips.TicksOf(40), EVideoSeekMode.Exact, CancellationToken.None);
            Task newer = session.SeekAsync(VideoTestClips.TicksOf(8), EVideoSeekMode.Exact, CancellationToken.None);
            await newer.WaitAsync(Timeout);
            try
            {
                await stale.WaitAsync(Timeout);
            }
            catch (OperationCanceledException)
            {
            }
            (stale.IsCanceled || stale.IsCompletedSuccessfully).ShouldBeTrue();

            // No frame of the stale position reaches the caller: the queue starts at the newer target and goes forward.
            for (int i = 8; i <= 11; i++)
                VideoTestClips.FrameOf(Dequeue(session, VideoTestClips.TicksOf(i))).ShouldBe(i, $"round {round}");
        }
    }

    [Test]
    public async Task CancelledSeek_EndsAsCancelled_AndDeliversNothing()
    {
        using IMediaStreamSession session = await OpenClip(".mkv", "matroska");
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Task seek = session.SeekAsync(VideoTestClips.TicksOf(20), EVideoSeekMode.Exact, cancel.Token);
        await Should.ThrowAsync<OperationCanceledException>(() => seek.WaitAsync(Timeout));
    }

    [Test]
    public async Task LiveHlsSession_CannotSeek()
    {
        string folder = Path.Combine(_directory, "hls");
        Directory.CreateDirectory(folder);
        string playlist = VideoTestClips.WriteLivePlaylist(folder);
        IMediaStreamSession session = HlsReferenceRuntime.CreateSession();
        try
        {
            await session.OpenAsync(playlist, ClockDrivenOptions(), CancellationToken.None).WaitAsync(Timeout);

            // The stream opens and decodes, but a live playlist has no seek.
            Dequeue(session, long.MaxValue / 2);
            session.CanSeek.ShouldBeFalse();
            await Should.ThrowAsync<NotSupportedException>(() => session.SeekAsync(0, EVideoSeekMode.Exact, CancellationToken.None));
        }
        finally
        {
            session.Close();
            session.Dispose();
        }
    }

    [Test]
    public async Task Seek_BeforeOpen_IsNotSupported()
    {
        using IMediaStreamSession session = HlsReferenceRuntime.CreateSession();
        session.CanSeek.ShouldBeFalse();
        await Should.ThrowAsync<NotSupportedException>(() => session.SeekAsync(0, EVideoSeekMode.Exact, CancellationToken.None));
    }

    [Test]
    public async Task NormalSession_OnALocalFile_CannotSeek()
    {
        string path = WriteClip(".mkv", "matroska");
        using IMediaStreamSession session = HlsReferenceRuntime.CreateSession();
        await session.OpenAsync(path, new StreamOpenOptions { VideoQueueCapacity = 4, EnableReconnect = false, DecodeAudio = false }, CancellationToken.None).WaitAsync(Timeout);

        // The normal streaming loop runs no seeks, so the session must not claim one.
        session.CanSeek.ShouldBeFalse();
        await Should.ThrowAsync<NotSupportedException>(() => session.SeekAsync(0, EVideoSeekMode.Exact, CancellationToken.None));
        session.RequestSeek(0, EVideoSeekMode.Exact).ShouldBe(-1);
        session.Close();
    }

    [Test]
    public async Task PolledSeek_CompletesTheNewest_AndCancelsTheSuperseded()
    {
        using IMediaStreamSession session = await OpenClip(".mkv", "matroska");
        int first = session.RequestSeek(VideoTestClips.TicksOf(40), EVideoSeekMode.Exact);
        int second = session.RequestSeek(VideoTestClips.TicksOf(6), EVideoSeekMode.NearestKeyframe);
        session.GetSeekState(first).ShouldBe(EVideoSeekState.Cancelled);

        Stopwatch watch = Stopwatch.StartNew();
        while (session.GetSeekState(second) == EVideoSeekState.Pending && watch.Elapsed < Timeout)
            Thread.Sleep(1);
        session.GetSeekState(second).ShouldBe(EVideoSeekState.Completed);
        VideoTestClips.FrameOf(Dequeue(session, VideoTestClips.TicksOf(6))).ShouldBe(6);
    }

    [Test]
    public async Task NonZeroStartTime_IsTheZeroOfTheFrameTimes()
    {
        string folder = Path.Combine(_directory, $"start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string path = VideoTestClips.Write(folder, ".mkv", "matroska", startFrame: 10);
        using IMediaStreamSession session = HlsReferenceRuntime.CreateSession();
        await session.OpenAsync(path, ClockDrivenOptions(), CancellationToken.None).WaitAsync(Timeout);

        await session.SeekAsync(0, EVideoSeekMode.Exact, CancellationToken.None).WaitAsync(Timeout);
        DecodedVideoFrame first = Dequeue(session, 0);
        VideoTestClips.FrameOf(first).ShouldBe(0);
        first.PresentationTimestampTicks.ShouldBe(0);

        await session.SeekAsync(VideoTestClips.TicksOf(7), EVideoSeekMode.Exact, CancellationToken.None).WaitAsync(Timeout);
        DecodedVideoFrame seventh = Dequeue(session, VideoTestClips.TicksOf(7));
        VideoTestClips.FrameOf(seventh).ShouldBe(7);
        seventh.PresentationTimestampTicks.ShouldBe(VideoTestClips.TicksOf(7));
    }

    [Test]
    public async Task CloseAfterDispose_DoesNothing()
    {
        IMediaStreamSession session = await OpenClip(".mkv", "matroska");
        session.Dispose();
        Should.NotThrow(session.Close);
        Should.NotThrow(session.Dispose);
    }

    // ═══════════════════════════════════════════════════════════════
    // Video track
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Track_HeldScrub_ShowsTheKeyframe_ThenTheExactFrameAfterTheDwell()
    {
        FakeTime time = new();
        using Rig rig = MakeRig(WriteClip(".mov", "mov", interCoded: true), time: time);
        long target = VideoTestClips.TicksOf(27) + 200_000;
        CutsceneTime position = FromTicks(target);

        // The editor sends a scrub seek in each frame while the ruler is held, also when the mouse does not move.
        rig.Player.BeginScrub();
        ScrubUntil(rig, position, () => rig.Target.SubmitCount > 0);
        rig.Target.LastFrame.ShouldBe(20, "a drag shows the keyframe at or before the time");
        rig.Target.MaxPts.ShouldBeLessThanOrEqualTo(target, "a drag never shows a frame after the time");

        // Shorter than the dwell: still the keyframe.
        time.Advance(TimeSpan.FromSeconds(CutsceneVideoChannel.DragDwellSeconds / 2));
        for (int i = 0; i < 5; i++)
            ScrubOnce(rig, position);
        rig.Target.LastFrame.ShouldBe(20);

        // The same time for the dwell: the playhead stopped, so the track decodes the exact frame.
        time.Advance(TimeSpan.FromSeconds(CutsceneVideoChannel.DragDwellSeconds));
        ScrubUntil(rig, position, () => rig.Target.LastFrame == 27);
        rig.Target.LastPts.ShouldBe(VideoTestClips.TicksOf(27));
        rig.Player.EndScrub();
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Track_PausedPlayheadWithNoScrub_DecodesTheExactFrameAfterTheDwell()
    {
        FakeTime time = new();
        using Rig rig = MakeRig(WriteClip(".mov", "mov", interCoded: true), time: time);
        rig.Player.BeginScrub();
        ScrubUntil(rig, FromTicks(VideoTestClips.TicksOf(15)), () => rig.Target.SubmitCount > 0);
        rig.Target.LastFrame.ShouldBe(10);

        time.Advance(TimeSpan.FromSeconds(CutsceneVideoChannel.DragDwellSeconds * 2));
        RunUntil(() => rig.Target.LastFrame == 15, rig.Player.UpdateVideo);
    }

    [Test]
    public void Track_MovingDrag_AllocatesNothingPerTick()
    {
        FakeTime time = new();
        using Rig rig = MakeRig(WriteClip(".mkv", "matroska"), time: time, cacheFrames: 4);
        CutsceneVideoChannel channel = rig.Player.Video.GetChannel(0)!;
        rig.Player.BeginScrub();
        ScrubUntil(rig, FromTicks(VideoTestClips.TicksOf(1)), () => channel.IsSessionOpen);

        // Warm the drag path: every moving tick asks for a keyframe seek, and its result shows.
        for (int frame = 2; frame < 14; frame++)
            DragStep(rig, channel, frame);

        int seeks = rig.Player.Video.SeekRequestCount;
        int shown = rig.Target.SubmitCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 14; frame < 40; frame++)
            DragStep(rig, channel, frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0, "a moving drag must not allocate on the update thread");
        rig.Player.Video.SeekRequestCount.ShouldBe(seeks + 26, "each moving tick asks for one keyframe seek");
        rig.Target.SubmitCount.ShouldBeGreaterThan(shown);
        rig.Target.MaxPts.ShouldBeLessThanOrEqualTo(VideoTestClips.TicksOf(39));
    }

    // One drag tick to a new frame, then the updates until its seek ends and the result shows. The waits allocate nothing.
    private static void DragStep(Rig rig, CutsceneVideoChannel channel, int frame)
    {
        rig.Player.UpdateScrub(new CutsceneTime(VideoTestClips.TicksOf(frame) * CutsceneTime.FlicksPerSecond / TimeSpan.TicksPerSecond));
        rig.Player.Tick(0.0);
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (channel.IsSeekInFlight && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(1);
            rig.Player.UpdateVideo();
        }
        rig.Player.UpdateVideo();
    }

    [Test]
    public void Track_CacheHit_ShowsTheFrameWithNoDecode()
    {
        using Rig rig = MakeRig(WriteClip(".mkv", "matroska"));
        JumpAndWait(rig, 10);
        JumpAndWait(rig, 30);
        int seeks = rig.Player.Video.SeekRequestCount;

        // Frame 10 is in the cache, so the jump back shows it in the same tick, with no seek.
        rig.Player.Seek(FromTicks(VideoTestClips.TicksOf(10)), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Target.LastFrame.ShouldBe(10);
        rig.Player.Video.SeekRequestCount.ShouldBe(seeks);
        rig.Player.Video.GetChannel(0)!.Cache.HitCount.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Track_Playback_FollowsTheCutsceneTime_AndAllocatesNothingPerFrame()
    {
        using Rig rig = MakeRig(WriteClip(".mkv", "matroska"), cacheFrames: 8);
        rig.Player.Play();

        // Each tick waits until the decoder has queued the next frame, so the result does not depend on the speed of the
        // machine. The waits allocate nothing.
        const double step = 1.0 / 60.0;
        rig.Player.Tick(0.0);
        RunUntil(() => rig.Target.SubmitCount >= 1, () => rig.Player.Tick(0.0));
        for (int i = 0; i < 20; i++)
            TickWhenDecoded(rig, step);

        int submitted = rig.Target.SubmitCount;
        int seeks = rig.Player.Video.SeekRequestCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++)
            TickWhenDecoded(rig, step);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0, "steady video playback must not allocate on the update thread");
        rig.Target.SubmitCount.ShouldBeGreaterThan(submitted);
        rig.Player.Video.SeekRequestCount.ShouldBe(seeks, "forward playback decodes forward with no seek");

        long position = rig.Player.Position.ToSamples((int)TimeSpan.TicksPerSecond);
        rig.Target.LastFrame.ShouldBe((int)(position / VideoTestClips.FrameTicks));
        rig.Target.MaxPts.ShouldBeLessThanOrEqualTo(position);
    }

    // Waits until the session queue holds the next frame, then ticks.
    private static void TickWhenDecoded(Rig rig, double seconds)
    {
        CutsceneVideoChannel channel = rig.Player.Video.GetChannel(0)!;
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (channel.QueuedFrameCount == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(1);
        rig.Player.Tick(seconds);
    }

    [Test]
    public void Track_KeepsOneSeekInFlight_AndAsksForTheNewestTimeWhenItEnds()
    {
        FakeSeekSession session = new();
        using Rig rig = MakeRig("unused-file.mp4", () => session);
        CutsceneTime first = FromTicks(VideoTestClips.TicksOf(12));
        CutsceneTime second = FromTicks(VideoTestClips.TicksOf(33));

        rig.Player.BeginScrub();
        ScrubUntil(rig, first, () => session.Seeks.Count > 0);

        // The tick returns while the seek runs. A newer time waits: one seek at a time.
        for (int i = 0; i < 3; i++)
            ScrubOnce(rig, second);
        session.Seeks.Count.ShouldBe(1);
        session.Seeks[0].State.ShouldBe(EVideoSeekState.Pending);
        rig.Target.SubmitCount.ShouldBe(0);

        // The seek ends. Its keyframe (10) is before the new time, so it shows, and the track asks for the newest time.
        session.Complete(0, VideoTestClips.TicksOf(10));
        ScrubUntil(rig, second, () => session.Seeks.Count > 1);
        rig.Target.LastPts.ShouldBe(VideoTestClips.TicksOf(10));
        session.Seeks[1].TargetTicks.ShouldBe(VideoTestClips.TicksOf(33));
        session.Seeks[1].Mode.ShouldBe(EVideoSeekMode.NearestKeyframe);

        session.Complete(1, VideoTestClips.TicksOf(30));
        ScrubUntil(rig, second, () => rig.Target.SubmitCount > 1);
        rig.Target.LastPts.ShouldBe(VideoTestClips.TicksOf(30));
        rig.Target.SubmitCount.ShouldBe(2);
    }

    [Test]
    public void Track_ForwardDrag_ShowsALateResultThatIsBeforeTheWantedTime()
    {
        FakeSeekSession session = new();
        using Rig rig = MakeRig("unused-file.mp4", () => session);
        rig.Player.BeginScrub();
        ScrubUntil(rig, FromTicks(VideoTestClips.TicksOf(12)), () => session.Seeks.Count > 0);

        // The keyframe decode takes longer than a tick: the drag moves on before it ends.
        ScrubOnce(rig, FromTicks(VideoTestClips.TicksOf(20)));
        session.Complete(0, VideoTestClips.TicksOf(10));
        ScrubOnce(rig, FromTicks(VideoTestClips.TicksOf(28)));

        // The late result (frame 10) is at or before the wanted time, so it shows, and the newest time is asked for.
        rig.Target.SubmitCount.ShouldBe(1);
        rig.Target.LastPts.ShouldBe(VideoTestClips.TicksOf(10));
        session.Seeks.Count.ShouldBe(2);
        session.Seeks[1].TargetTicks.ShouldBe(VideoTestClips.TicksOf(28));

        // A result after the wanted time (a backward drag) does not show.
        ScrubOnce(rig, FromTicks(VideoTestClips.TicksOf(26)));
        session.Complete(1, VideoTestClips.TicksOf(27));
        ScrubOnce(rig, FromTicks(VideoTestClips.TicksOf(24)));
        rig.Target.LastPts.ShouldBe(VideoTestClips.TicksOf(10));
        rig.Target.MaxPts.ShouldBeLessThanOrEqualTo(VideoTestClips.TicksOf(24));
    }

    [Test]
    public void Residency_At1080p_KeepsThePinnedDeadlineFrames_AndGetLevelMatchesWhatIsHeld()
    {
        string folder = Path.Combine(_directory, $"hd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string path = Path.GetFullPath(VideoTestClips.Write(folder, ".mov", "mov", interCoded: true, width: 1920, height: 1080));
        using VideoResidencyBackend backend = new(CompileVideoAsset(path));
        Guid asset = backend.BuildManifest().Assets[0];
        VideoFrameCache cache = backend.GetCache(path)!;

        ResidencyTicket proxy = backend.Request(new ResidencyRequest(asset, (int)EResidencyLevel.Proxy, FromTicks(VideoTestClips.TicksOf(27)), 1.0f, 0));
        RunUntil(() => backend.GetLevel(asset) == EResidencyLevel.Proxy, () => { });
        ResidencyTicket target = backend.Request(new ResidencyRequest(asset, (int)EResidencyLevel.Target, FromTicks(VideoTestClips.TicksOf(33)), 1.0f, 0));
        RunUntil(() => backend.GetLevel(asset) == EResidencyLevel.Target, () => { });
        ResidencyTicket coarse = backend.Request(new ResidencyRequest(asset, (int)EResidencyLevel.Coarse, FromTicks(VideoTestClips.TicksOf(44)), 1.0f, 0));
        RunUntil(() => cache.IsPinned(VideoTestClips.TicksOf(40)), () => { });

        // The warm frames after frame 33 do not evict a deadline frame: each level keeps its frame.
        cache.IsPinned(VideoTestClips.TicksOf(20)).ShouldBeTrue("T0 keyframe");
        cache.IsPinned(VideoTestClips.TicksOf(33)).ShouldBeTrue("T2 exact frame");
        cache.IsPinned(VideoTestClips.TicksOf(40)).ShouldBeTrue("T1 keyframe");
        cache.TryGetFrameAt(VideoTestClips.TicksOf(33), out DecodedVideoFrame exact).ShouldBeTrue();
        exact.Width.ShouldBe(1920);
        VideoTestClips.FrameOf(exact).ShouldBe(33);
        long warmBytes = cache.AllocatedBytes - 3L * 1920 * 1080 * 3;
        warmBytes.ShouldBeLessThanOrEqualTo(VideoResidencyBackend.SourceCacheBudgetBytes);

        // A cancel releases the frame of that level only.
        backend.Cancel(target);
        cache.Contains(VideoTestClips.TicksOf(33)).ShouldBeFalse();
        cache.IsPinned(VideoTestClips.TicksOf(20)).ShouldBeTrue();
        backend.GetLevel(asset).ShouldBe(EResidencyLevel.Coarse);
        backend.Cancel(coarse);
        backend.Cancel(proxy);
        backend.GetLevel(asset).ShouldBe(EResidencyLevel.None);
        backend.FailedLoadCount.ShouldBe(0);
    }

    [Test]
    public void Track_ForwardDrag_NeverStepsBack_AfterAnExactFrame()
    {
        FakeTime time = new();
        FakeSeekSession session = new();
        using Rig rig = MakeRig("unused-file.mp4", () => session, time: time);
        CutsceneTime held = FromTicks(VideoTestClips.TicksOf(27) + 80_000);

        // A held playhead: first the keyframe, then the exact frame 27 after the dwell. The forward decode queues frame 28.
        rig.Player.BeginScrub();
        ScrubUntil(rig, held, () => session.Seeks.Count > 0);
        session.Complete(0, VideoTestClips.TicksOf(20));
        ScrubUntil(rig, held, () => rig.Target.SubmitCount > 0);
        time.Advance(TimeSpan.FromSeconds(CutsceneVideoChannel.DragDwellSeconds * 2));
        ScrubUntil(rig, held, () => session.Seeks.Count > 1);
        session.Seeks[1].Mode.ShouldBe(EVideoSeekMode.Exact);
        session.Complete(1, VideoTestClips.TicksOf(27));
        session.EnqueueFrame(VideoTestClips.TicksOf(28));
        ScrubUntil(rig, held, () => rig.Target.LastFrame == 27);

        // The drag moves on: a cache hit shows frame 28.
        ScrubOnce(rig, FromTicks(VideoTestClips.TicksOf(28) + 10_000));
        rig.Target.LastFrame.ShouldBe(28);

        // Past the cache: neither the late exact frame 27 nor the keyframe 20 of the new seek shows; 28 stays.
        CutsceneTime past = FromTicks(VideoTestClips.TicksOf(29) + 200_000);
        ScrubUntil(rig, past, () => session.Seeks.Count > 2);
        rig.Target.LastFrame.ShouldBe(28);
        session.Complete(2, VideoTestClips.TicksOf(20));
        for (int i = 0; i < 3; i++)
            ScrubOnce(rig, past);
        rig.Target.LastFrame.ShouldBe(28);

        // A backward drag still shows a keyframe at or before its time.
        CutsceneTime back = FromTicks(VideoTestClips.TicksOf(15));
        ScrubUntil(rig, back, () => session.Seeks.Count > 3);
        session.Complete(3, VideoTestClips.TicksOf(10));
        ScrubUntil(rig, back, () => rig.Target.LastFrame == 10);
        rig.Target.LastPts.ShouldBe(VideoTestClips.TicksOf(10));
    }

    private static void ScrubOnce(Rig rig, CutsceneTime position)
    {
        rig.Player.UpdateScrub(position);
        rig.Player.Tick(0.0);
    }

    private static void ScrubUntil(Rig rig, CutsceneTime position, Func<bool> done)
        => RunUntil(done, () => ScrubOnce(rig, position));

    [Test]
    public void Track_LiveSource_ReportsOneDiagnostic_AndDoesNotFakeASeek()
    {
        string folder = Path.Combine(_directory, $"track-hls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        using Rig rig = MakeRig(VideoTestClips.WriteLivePlaylist(folder));
        rig.Player.Play();
        RunUntil(() => CountDiagnostics(rig, "cannot seek") > 0, () => rig.Player.Tick(1.0 / 60.0));
        for (int i = 0; i < 20; i++)
            rig.Player.Tick(1.0 / 60.0);

        CountDiagnostics(rig, "cannot seek").ShouldBe(1);
        rig.Target.SubmitCount.ShouldBe(0);
        rig.Player.Video.SeekRequestCount.ShouldBe(0);
    }

    [Test]
    public void Track_LiveFakeSession_ReportsOneDiagnostic()
    {
        FakeSeekSession session = new() { Seekable = false };
        using Rig rig = MakeRig("live.m3u8", () => session);
        rig.Player.Play();
        RunUntil(() => CountDiagnostics(rig, "cannot seek") > 0, () => rig.Player.Tick(1.0 / 60.0));
        for (int i = 0; i < 20; i++)
            rig.Player.Tick(1.0 / 60.0);

        CountDiagnostics(rig, "cannot seek").ShouldBe(1);
        session.Seeks.ShouldBeEmpty();
    }

    [Test]
    public void Track_NodeWithNoTarget_ReportsOneDiagnostic()
    {
        FakeSeekSession session = new();
        using Rig rig = MakeRig("unused-file.mp4", () => session, addTarget: false);
        rig.Player.Play();
        for (int i = 0; i < 5; i++)
            rig.Player.Tick(1.0 / 60.0);

        CountDiagnostics(rig, nameof(IVideoFrameTarget)).ShouldBe(1);
        session.OpenCount.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    // Video residency
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Residency_T0DecodesTheKeyframe_T2WarmsTheFramesAroundTheDeadline_AndCancelReleases()
    {
        string path = WriteClip(".mov", "mov", interCoded: true);
        CompiledCutscene compiled = CompileVideoAsset(path);
        using VideoResidencyBackend backend = new(compiled);
        backend.SourceCount.ShouldBe(1);
        CutsceneResidencyManifest manifest = backend.BuildManifest();
        manifest.Assets.Length.ShouldBe(1);
        manifest.AssetKinds[0].ShouldBe(EResidencyAssetKind.Video);
        Guid asset = manifest.Assets[0];
        asset.ShouldBe(CutsceneResidencyAssetKeys.ForVideoSource(Path.GetFullPath(path)));
        VideoFrameCache cache = backend.GetCache(Path.GetFullPath(path))!;

        // T0 at the deadline of frame 27 decodes the keyframe at or before it.
        ResidencyTicket proxy = backend.Request(new ResidencyRequest(asset, (int)EResidencyLevel.Proxy, FromTicks(VideoTestClips.TicksOf(27) + 100_000), 1.0f, 0));
        RunUntil(() => backend.GetLevel(asset) == EResidencyLevel.Proxy, () => { });
        cache.Contains(VideoTestClips.TicksOf(20)).ShouldBeTrue();
        backend.OpenSessionCount.ShouldBe(1);

        // T2 at frame 33 decodes the exact frame and the frames after it.
        ResidencyTicket target = backend.Request(new ResidencyRequest(asset, (int)EResidencyLevel.Target, FromTicks(VideoTestClips.TicksOf(33)), 1.0f, 0));
        RunUntil(() => backend.GetLevel(asset) == EResidencyLevel.Target, () => { });
        for (int frame = 33; frame <= 33 + VideoResidencyBackend.WarmFrameCount; frame++)
            cache.Contains(VideoTestClips.TicksOf(frame)).ShouldBeTrue($"warm frame {frame}");

        // A track copies a warm frame with no decode.
        VideoFrameCache trackCache = new(4);
        byte[]? scratch = null;
        backend.TryCopyFrame(Path.GetFullPath(path), VideoTestClips.TicksOf(35) + 10, trackCache, ref scratch).ShouldBeTrue();
        trackCache.TryGetFrame(VideoTestClips.TicksOf(35), out DecodedVideoFrame copy).ShouldBeTrue();
        VideoTestClips.FrameOf(copy).ShouldBe(35);

        // A cancel of the complete T2 ticket releases its frames; T0 keeps its keyframe and the session.
        backend.Cancel(target);
        backend.GetLevel(asset).ShouldBe(EResidencyLevel.Proxy);
        cache.Contains(VideoTestClips.TicksOf(33)).ShouldBeFalse();
        cache.Contains(VideoTestClips.TicksOf(20)).ShouldBeTrue();
        backend.OpenSessionCount.ShouldBe(1);

        backend.Cancel(proxy);
        backend.GetLevel(asset).ShouldBe(EResidencyLevel.None);
        cache.Count.ShouldBe(0);
        backend.OpenSessionCount.ShouldBe(0);
        backend.FailedLoadCount.ShouldBe(0);
    }

    [Test]
    public void ResidencySession_RoutesVideoAssets_ToTheVideoBackend_AndGivesItToThePlayer()
    {
        FakeSeekSession media = new();
        using Rig rig = MakeRig("unused-file.mp4", () => media);
        CompiledCutscene compiled = CompileVideoAsset("unused-file.mp4");
        VideoResidencyBackend? created = null;
        List<CutsceneCompileDiagnostic> diagnostics = [];
        CutsceneResidencySession session = CutsceneResidencySession.Create(
            compiled,
            rig.Table,
            new FakeResidencyBackend(),
            new NoAssets(),
            diagnostics,
            createVideoBackend: c => created = new VideoResidencyBackend(c, () => new FakeSeekSession()));
        try
        {
            session.VideoPlanner.ShouldNotBeNull();
            session.VideoBackend.ShouldBeSameAs(created);
            session.VideoPlanner!.Manifest.AssetKinds.ShouldAllBe(kind => kind == EResidencyAssetKind.Video);
            session.Planner.Manifest.CountKind(EResidencyAssetKind.Video).ShouldBe(0);

            session.Open(rig.Player);
            session.VideoPlanner.IsOpen.ShouldBeTrue();
            rig.Player.Video.Residency.ShouldBeSameAs(created);
        }
        finally
        {
            session.Close();
        }
        rig.Player.Video.Residency.ShouldBeNull();
        session.VideoPlanner.ShouldBeNull();
    }

    private static CompiledCutscene CompileVideoAsset(string sourcePath)
    {
        CutsceneAsset asset = new() { Duration = CutsceneTime.FromSeconds(2.0) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = Guid.NewGuid() });
        VideoTrack track = new() { Name = "Screen", BindingSlot = 0 };
        track.Clips.Add(new VideoTrackClip { Start = CutsceneTime.Zero, Duration = CutsceneTime.FromSeconds(2.0), SourcePath = sourcePath });
        asset.Tracks.Add(track);
        return CutsceneCompiler.Compile(asset, []);
    }

    private sealed class NoAssets : ICutsceneResidencyAssetCollector
    {
        public void Collect(SceneNode node, List<CutsceneResidencyAsset> destination)
        {
        }
    }

    private string WriteClip(string extension, string muxer, bool interCoded = false)
    {
        string folder = Path.Combine(_directory, $"track-{muxer}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return VideoTestClips.Write(folder, extension, muxer, interCoded);
    }

    private static void JumpAndWait(Rig rig, int frame)
    {
        rig.Player.Seek(FromTicks(VideoTestClips.TicksOf(frame)), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        RunUntil(() => rig.Target.LastFrame == frame, rig.Player.UpdateVideo);
    }

    private static int CountDiagnostics(Rig rig, string text)
    {
        int count = 0;
        foreach (CutsceneCompileDiagnostic diagnostic in rig.Player.Diagnostics)
            if (diagnostic.Message.Contains(text, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static CutsceneTime FromTicks(long ticks)
        => CutsceneTime.FromSamples(ticks, (int)TimeSpan.TicksPerSecond);

    private static void RunUntil(Func<bool> done, Action step)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > Timeout)
                throw new TimeoutException("The condition did not become true in time.");
            step();
            Thread.Sleep(1);
        }
    }

    private static Rig MakeRig(string sourcePath, Func<IMediaStreamSession>? createSession = null, bool addTarget = true, TimeProvider? time = null, int cacheFrames = VideoFrameCache.DefaultCapacity)
    {
        SceneNode node = new("Screen");
        FakeVideoTarget? target = addTarget ? node.AddComponent<FakeVideoTarget>() : null;
        CutsceneAsset asset = new() { Duration = CutsceneTime.FromSeconds(2.0) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        VideoTrack track = new() { Name = "Screen", BindingSlot = 0, FrameCacheCapacity = cacheFrames };
        track.Clips.Add(new VideoTrackClip { Start = CutsceneTime.Zero, Duration = CutsceneTime.FromSeconds(2.0), SourcePath = sourcePath });
        asset.Tracks.Add(track);

        List<CutsceneCompileDiagnostic> diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        diagnostics.ShouldBeEmpty();
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        table.Diagnostics.ShouldBeEmpty();
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table), null, time ?? TimeProvider.System, null, createSession);
        return new Rig(player, table, target ?? new FakeVideoTarget());
    }

    private sealed class Rig(CutscenePlayer player, CutsceneBindingTable table, FakeVideoTarget target) : IDisposable
    {
        public CutscenePlayer Player { get; } = player;
        public CutsceneBindingTable Table { get; } = table;
        public FakeVideoTarget Target { get; } = target;

        public void Dispose()
        {
            Player.Stop();
            Table.Dispose();
        }
    }

    private sealed class FakeResolver(SceneNode node) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id) => node.ID == id ? node : null;
        public SceneNode? FindNodeByPath(string path) => null;
        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;
        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>A frame surface that records what it shows. It allocates nothing in a submit.</summary>
    private sealed class FakeVideoTarget : XRComponent, IVideoFrameTarget
    {
        public int SubmitCount { get; private set; }
        public int ClearCount { get; private set; }
        public long? LastPts { get; private set; }
        public int LastFrame { get; private set; } = -1;
        public long MaxPts { get; private set; } = long.MinValue;

        public void SubmitFrame(in DecodedVideoFrame frame)
        {
            SubmitCount++;
            LastPts = frame.PresentationTimestampTicks;
            LastFrame = VideoTestClips.FrameOf(frame);
            MaxPts = Math.Max(MaxPts, frame.PresentationTimestampTicks);
        }

        public void ClearFrame()
            => ClearCount++;
    }

    /// <summary>A wall clock that moves only when the test advances it.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private long _timestamp = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
            => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan time)
            => Interlocked.Add(ref _timestamp, time.Ticks);
    }

    /// <summary>
    /// A media session whose seeks the test completes by hand. A newer seek cancels an older one that did not complete,
    /// and a late completion of a cancelled seek delivers nothing, as in the real session.
    /// </summary>
    private sealed class FakeSeekSession : IMediaStreamSession
    {
        private readonly Lock _sync = new();
        private readonly Queue<DecodedVideoFrame> _frames = new();

        public bool Seekable { get; init; } = true;
        public List<FakeSeek> Seeks { get; } = [];
        public int OpenCount { get; private set; }

        public event Action<int, int>? VideoSizeChanged
        {
            add { }
            remove { }
        }

        public bool IsOpen { get; private set; }
        public int QueuedVideoFrameCount => _frames.Count;
        public int QueuedAudioFrameCount => 0;
        public long EstimatedQueuedVideoDurationTicks => 0;
        public bool CanSeek => IsOpen && Seekable;
        public string? LastSeekError => null;

        public Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
        {
            OpenCount++;
            IsOpen = true;
            return Task.CompletedTask;
        }

        public int RequestSeek(long targetTicks, EVideoSeekMode mode)
        {
            lock (_sync)
            {
                if (Seeks.Count > 0 && Seeks[^1].State == EVideoSeekState.Pending)
                    Seeks[^1].State = EVideoSeekState.Cancelled;
                _frames.Clear();
                Seeks.Add(new FakeSeek(targetTicks, mode));
                return Seeks.Count - 1;
            }
        }

        public EVideoSeekState GetSeekState(int requestId)
        {
            lock (_sync)
                return (uint)requestId < (uint)Seeks.Count ? Seeks[requestId].State : EVideoSeekState.Cancelled;
        }

        public Task SeekAsync(long targetTicks, EVideoSeekMode mode, CancellationToken cancellationToken)
            => throw new NotSupportedException("The track uses the polled seek.");

        /// <summary>Queues one more frame after a completed seek, as the forward decode of an exact seek does.</summary>
        public void EnqueueFrame(long framePts)
        {
            lock (_sync)
            {
                byte[] data = new byte[VideoTestClips.Width * VideoTestClips.Height * 3];
                Array.Fill(data, VideoTestClips.GrayOf((int)(framePts / VideoTestClips.FrameTicks)));
                _frames.Enqueue(new DecodedVideoFrame(VideoTestClips.Width, VideoTestClips.Height, framePts, VideoTestClips.FrameTicks, VideoPixelFormat.Rgb24, data));
            }
        }

        /// <summary>Completes a seek with one frame. A cancelled seek delivers nothing.</summary>
        public void Complete(int index, long framePts)
        {
            lock (_sync)
            {
                FakeSeek seek = Seeks[index];
                if (seek.State != EVideoSeekState.Pending)
                    return;
                byte[] data = new byte[VideoTestClips.Width * VideoTestClips.Height * 3];
                Array.Fill(data, VideoTestClips.GrayOf((int)(framePts / VideoTestClips.FrameTicks)));
                _frames.Enqueue(new DecodedVideoFrame(VideoTestClips.Width, VideoTestClips.Height, framePts, VideoTestClips.FrameTicks, VideoPixelFormat.Rgb24, data));
                seek.State = EVideoSeekState.Completed;
            }
        }

        public bool TryDequeueVideoFrame(long audioClockTicks, out DecodedVideoFrame frame)
        {
            lock (_sync)
            {
                if (_frames.Count > 0 && _frames.Peek().PresentationTimestampTicks <= audioClockTicks + 400_000)
                {
                    frame = _frames.Dequeue();
                    return true;
                }
                frame = default;
                return false;
            }
        }

        public bool TryDequeueAudioFrame(out DecodedAudioFrame frame)
        {
            frame = default;
            return false;
        }

        public void SetTargetFramebuffer(uint framebufferId)
        {
        }

        public void Present()
        {
        }

        public void Close()
            => IsOpen = false;

        public void Dispose()
            => IsOpen = false;
    }

    private sealed class FakeSeek(long targetTicks, EVideoSeekMode mode)
    {
        public long TargetTicks { get; } = targetTicks;
        public EVideoSeekMode Mode { get; } = mode;
        public EVideoSeekState State { get; set; } = EVideoSeekState.Pending;
    }

    internal static StreamOpenOptions ClockDrivenOptions()
        => new()
        {
            ClockDrivenPresentation = true,
            DecodeAudio = false,
            VideoQueueCapacity = 4,
            EnableReconnect = false,
        };

    private async Task<IMediaStreamSession> OpenClip(string extension, string muxer, bool interCoded = false)
    {
        string folder = Path.Combine(_directory, $"{muxer}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string path = VideoTestClips.Write(folder, extension, muxer, interCoded);
        IMediaStreamSession session = HlsReferenceRuntime.CreateSession();
        await session.OpenAsync(path, ClockDrivenOptions(), CancellationToken.None).WaitAsync(Timeout);
        return session;
    }

    /// <summary>Waits until the session releases a frame for the clock.</summary>
    internal static DecodedVideoFrame Dequeue(IMediaStreamSession session, long clockTicks)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout)
        {
            if (session.TryDequeueVideoFrame(clockTicks, out DecodedVideoFrame frame))
                return frame;
            Thread.Sleep(1);
        }
        throw new TimeoutException($"No frame for the clock {clockTicks} within {Timeout.TotalSeconds} s.");
    }
}
