using NUnit.Framework;
using Shouldly;
using XREngine.Data;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class VorbisSeekIndexTests
{
    private const int ReadFrames = 512;

    private static byte[] Fixture()
        => Convert.FromBase64String(StreamingAudioTestData.VorbisStereoBase64);

    /// <summary>A scheduler that queues each task until the test runs it, so the index build runs at a known time.</summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly List<Task> _tasks = [];

        public int Pending => _tasks.Count;

        public void RunAll()
        {
            while (_tasks.Count > 0)
            {
                Task task = _tasks[0];
                _tasks.RemoveAt(0);
                TryExecuteTask(task);
            }
        }

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks;

        protected override void QueueTask(Task task) => _tasks.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    private static byte[] TwoBlockFixture()
        => Convert.FromBase64String(VorbisTwoBlockTestData.Base64);

    /// <summary>Opens a decoder. With the index, it waits for the build, so the test uses a known seek path.</summary>
    private static VorbisStreamingDecoder Open(byte[] file, bool useIndex)
    {
        VorbisStreamingDecoder decoder = new(
            new MemoryStream(file, writable: false),
            leaveOpen: false,
            useIndex ? () => new MemoryStream(file, writable: false) : null,
            null);
        if (useIndex)
            decoder.WaitForSeekIndex(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        return decoder;
    }

    /// <summary>The decode of the whole file from frame zero, with no seek.</summary>
    private static float[] Linear(byte[] file)
    {
        using VorbisStreamingDecoder decoder = Open(file, useIndex: false);
        float[] all = new float[decoder.TotalFrames * decoder.Channels];
        int frame = 0;
        float[] part = new float[1000 * decoder.Channels];
        while (true)
        {
            int read = decoder.Read(part);
            if (read == 0)
                break;
            Array.Copy(part, 0, all, (long)frame * decoder.Channels, (long)read * decoder.Channels);
            frame += read;
        }
        frame.ShouldBe((int)decoder.TotalFrames);
        return all;
    }

    /// <summary>Runs a decode on another thread and fails if it does not end in time, so a decoder loop cannot block the test host.</summary>
    private static T RunGuarded<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work);
        task.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue("The Vorbis decode did not end in 20 seconds.");
        return task.Result;
    }

    /// <summary>Seeks, reads, and returns the number of samples that are not equal to the linear decode.</summary>
    private static int SeekMismatches(VorbisStreamingDecoder decoder, float[] whole, long frame, float[] buffer)
    {
        decoder.SeekToFrame(frame);
        decoder.FramePosition.ShouldBe(frame);
        int expected = (int)Math.Min(ReadFrames, decoder.TotalFrames - frame);
        int read = decoder.Read(buffer);
        read.ShouldBe(expected, $"read count after a seek to frame {frame}");
        int channels = decoder.Channels;
        int mismatches = 0;
        for (int i = 0; i < read * channels; i++)
        {
            if (buffer[i] != whole[frame * channels + i])
                mismatches++;
        }
        return mismatches;
    }

    // Checks that the index is ready and moves the decoder to the end, so the next seek is a backward seek.
    private static void BuildIndex(VorbisStreamingDecoder decoder)
    {
        decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready, decoder.SeekIndexFailure);
        decoder.SeekToFrame(decoder.TotalFrames);
    }

    /// <summary>
    /// Seeks backward to every target from the last to the first and counts the samples that are not equal to the linear
    /// decode. Each target is before the end of the previous read, so every seek is a backward seek.
    /// </summary>
    private static int BackwardSweep(VorbisStreamingDecoder decoder, float[] whole, SortedSet<long> targets, out int count)
    {
        float[] buffer = new float[ReadFrames * decoder.Channels];
        int bad = 0;
        count = 0;
        foreach (long frame in targets.Reverse())
        {
            if (frame < 0 || frame >= decoder.TotalFrames)
                continue;
            bad += SeekMismatches(decoder, whole, frame, buffer);
            count++;
        }
        return bad;
    }

    /// <summary>The targets around each index entry, each page granule, and each change between short and long blocks.</summary>
    private static SortedSet<long> EdgeTargets(VorbisStreamingDecoder decoder, List<VorbisTestFileBuilder.Page> pages, VorbisTestFileBuilder.Packets packets)
    {
        long total = decoder.TotalFrames;
        SortedSet<long> set = [0, 1, 100, total - 1, total - 300, total - ReadFrames];
        foreach (VorbisPageIndexEntry entry in decoder.PageIndex!.Entries)
        {
            set.Add(entry.RestartFrame);
            set.Add(entry.RestartFrame - 1);
            set.Add(entry.RestartFrame + 1);
        }
        long previousGranule = 0;
        foreach (VorbisTestFileBuilder.Page page in pages)
        {
            if (!page.IsAudio || page.Granule < 0)
                continue;
            set.Add(page.Granule - 1);
            set.Add(page.Granule);
            set.Add(page.Granule + 1);
            set.Add((previousGranule + page.Granule) / 2);
            previousGranule = page.Granule;
        }
        for (int i = 1; i < packets.IsLong.Length; i++)
        {
            if (packets.IsLong[i] == packets.IsLong[i - 1])
                continue;
            set.Add(packets.Ends[i - 1] - 1);
            set.Add(packets.Ends[i - 1]);
            set.Add(packets.Ends[i]);
        }
        return set;
    }

    [Test]
    [CancelAfter(60000)]
    public void BackwardSeek_WithTheIndex_MatchesTheLinearDecodeAtPageEdgesAndInsidePages()
    {
        // Small pages, so packets cross page edges and the file has many restart pages.
        byte[] file = VorbisTestFileBuilder.Build(Fixture(), repeat: 2, maxBodyBytes: 160, out List<VorbisTestFileBuilder.Page> pages);
        (int mismatches, int targets, int indexed, int entries) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            BuildIndex(decoder);
            long total = decoder.TotalFrames;

            SortedSet<long> set = [0, 1, 100, total - 1, total - 300, total - ReadFrames];
            foreach (VorbisPageIndexEntry entry in decoder.PageIndex!.Entries)
            {
                set.Add(entry.RestartFrame);
                set.Add(entry.RestartFrame - 1);
                set.Add(entry.RestartFrame + 1);
            }
            long previousGranule = 0;
            foreach (VorbisTestFileBuilder.Page page in pages)
            {
                if (!page.IsAudio || page.Granule < 0)
                    continue;
                set.Add(page.Granule - 1);
                set.Add(page.Granule);
                set.Add(page.Granule + 1);
                set.Add((previousGranule + page.Granule) / 2);
                previousGranule = page.Granule;
            }

            // Each target is before the end of the previous read, so every seek is a backward seek.
            float[] buffer = new float[ReadFrames * decoder.Channels];
            int count = 0;
            int bad = 0;
            foreach (long frame in set.Reverse())
            {
                if (frame < 0 || frame >= total)
                    continue;
                bad += SeekMismatches(decoder, whole, frame, buffer);
                count++;
            }

            decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready, decoder.SeekIndexFailure);
            return (bad, count, decoder.IndexedSeekCount, decoder.PageIndex!.Count);
        });

        TestContext.Out.WriteLine($"Indexed backward seeks: {targets} targets, {indexed} from an indexed page, {entries} index entries.");
        entries.ShouldBeGreaterThan(20);
        indexed.ShouldBeGreaterThan(targets / 2);
        mismatches.ShouldBe(0);
    }

    /// <summary>
    /// Makes a file whose first two audio packets are long and then short: the two-block fixture from its first such pair
    /// on, with new granule positions. No encoder is needed.
    /// </summary>
    private static byte[] LongShortStartFile(int maxBodyBytes, out List<VorbisTestFileBuilder.Page> pages)
    {
        VorbisTestFileBuilder.Packets source = VorbisTestFileBuilder.Analyze(TwoBlockFixture());
        int first = 1;
        while (!(source.IsLong[first] && !source.IsLong[first + 1]))
            first++;
        return VorbisTestFileBuilder.Build(TwoBlockFixture(), repeat: 0, maxBodyBytes, out pages, firstPacket: first);
    }

    [Test]
    [CancelAfter(60000)]
    public void LongShortStart_LinearDecodeAndIndexedSeeksShareOneTimeline([Values(160, 4096)] int maxBodyBytes)
    {
        // NVorbis drops the flat part of the first long window (2048/4 - 256/4 = 448 frames) at the start of the frame-zero
        // decode too. The decoder timeline is the frame-zero decode, so the frame count and every restart frame move by 448.
        byte[] file = LongShortStartFile(maxBodyBytes, out List<VorbisTestFileBuilder.Page> pages);
        VorbisTestFileBuilder.Packets packets = VorbisTestFileBuilder.Analyze(file);
        packets.IsLong[0].ShouldBeTrue();
        packets.IsLong[1].ShouldBeFalse();
        long finalGranule = VorbisTestFileBuilder.ReadPackets(file, out long granule, out _).Count > 0 ? granule : -1;

        (int mismatches, int targets, int indexed, int startDrop, long total) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            BuildIndex(decoder);
            decoder.PageIndex!.StartDrop.ShouldBe(448);

            // The page granules are in granule frames. Move them to decoder frames for the edge targets.
            List<VorbisTestFileBuilder.Page> shifted = [];
            foreach (VorbisTestFileBuilder.Page page in pages)
                shifted.Add(page.Granule < 0 ? page : page with { Granule = page.Granule - 448 });
            long[] ends = new long[packets.Ends.Length];
            for (int i = 0; i < ends.Length; i++)
                ends[i] = packets.Ends[i] - 448;
            VorbisTestFileBuilder.Packets moved = packets with { Ends = ends };

            int bad = BackwardSweep(decoder, whole, EdgeTargets(decoder, shifted, moved), out int count);
            decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready, decoder.SeekIndexFailure);
            return (bad, count, decoder.IndexedSeekCount, decoder.StartDrop, decoder.TotalFrames);
        });

        TestContext.Out.WriteLine($"Long-short start, pages up to {maxBodyBytes} bytes: start drop {startDrop}, {total} frames (last granule {finalGranule}), {targets} targets, {indexed} from an indexed page.");
        startDrop.ShouldBe(448);
        total.ShouldBe(finalGranule - 448);
        indexed.ShouldBeGreaterThan(0);
        mismatches.ShouldBe(0);
    }

    [Test]
    public void ShortStart_HasNoStartDrop()
    {
        using VorbisStreamingDecoder decoder = Open(TwoBlockFixture(), useIndex: false);
        decoder.StartDrop.ShouldBe(0);
        decoder.TotalFrames.ShouldBe(132300);
    }

    [Test]
    public void DisposeBeforeTheBuildStarts_CancelsTheQueuedBuild()
    {
        byte[] file = TwoBlockFixture();
        ManualScheduler scheduler = new();
        VorbisStreamingDecoder decoder = new(new MemoryStream(file, writable: false), leaveOpen: false, () => new MemoryStream(file, writable: false), scheduler);
        scheduler.Pending.ShouldBe(1);
        decoder.Dispose();
        scheduler.RunAll();
        decoder.IndexBuildStarted.ShouldBeFalse();
    }

    [Test]
    public void LinearDecoder_StartsNoIndexBuild_AndReportsNothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xre-vorbis-linear-{Guid.NewGuid():N}.ogg");
        File.WriteAllBytes(path, TwoBlockFixture());
        try
        {
            using IStreamingAudioDecoder linear = XREngine.Cutscenes.AudioTrackEvaluator.OpenLinearDecoder(path);
            VorbisStreamingDecoder vorbis = linear.ShouldBeOfType<VorbisStreamingDecoder>();
            vorbis.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Disabled);
            vorbis.SeekIndexFailure.ShouldBeNull();
            vorbis.WaitForSeekIndex(TimeSpan.FromSeconds(1)).ShouldBeTrue();
            vorbis.IndexBuildStarted.ShouldBeFalse();

            using IStreamingAudioDecoder seeking = XREngine.Cutscenes.AudioTrackEvaluator.OpenDefaultDecoder(path);
            VorbisStreamingDecoder indexed = seeking.ShouldBeOfType<VorbisStreamingDecoder>();
            indexed.WaitForSeekIndex(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            indexed.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void StreamThatCannotBeOpenedTwice_ReportsTheMissingIndex()
    {
        byte[] file = TwoBlockFixture();
        using VorbisStreamingDecoder decoder = new(new BufferedStream(new MemoryStream(file, writable: false)), leaveOpen: false);
        decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Failed);
        decoder.SeekIndexFailure.ShouldNotBeNull();
        decoder.SeekIndexFailure!.ShouldContain("cannot be opened a second time");
    }

    [Test]
    public void TwoBlockFixture_HasTwoBlockSizesTwoModesAndBlockChanges()
    {
        VorbisTestFileBuilder.Packets packets = VorbisTestFileBuilder.Analyze(TwoBlockFixture());
        TestContext.Out.WriteLine($"Block sizes {packets.ShortBlock}/{packets.LongBlock}, {packets.ModeCount} modes, {packets.IsLong.Length} audio packets, {packets.Changes} block changes.");
        packets.ShortBlock.ShouldBe(256);
        packets.LongBlock.ShouldBe(2048);
        packets.ModeCount.ShouldBeGreaterThanOrEqualTo(2);
        packets.Changes.ShouldBeGreaterThan(10);
    }

    [Test]
    [CancelAfter(60000)]
    public void BackwardSeek_OnATwoBlockFile_MatchesTheLinearDecodeAtEdgesAndBlockChanges()
    {
        // The libvorbis file re-paged with 160-byte pages and its packet run repeated once, so restart pages follow both
        // short and long packets, and long packets cross pages.
        byte[] file = VorbisTestFileBuilder.Build(TwoBlockFixture(), repeat: 1, maxBodyBytes: 160, out List<VorbisTestFileBuilder.Page> pages);
        VorbisTestFileBuilder.Packets packets = VorbisTestFileBuilder.Analyze(file);
        (int mismatches, int targets, int indexed, int afterShort, int afterLong) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            BuildIndex(decoder);

            int shortCount = 0;
            int longCount = 0;
            int longToShort = 0;
            int shortToLong = 0;
            foreach (VorbisPageIndexEntry entry in decoder.PageIndex!.Entries)
            {
                // The first packet of the restart is the last packet that ends at or before the restart frame (the frame is
                // later than its end after a long-to-short change). The packet before it ends on the earlier page.
                int first = Array.BinarySearch(packets.Ends, entry.RestartFrame);
                if (first < 0)
                {
                    first = ~first - 1;
                    longToShort++;
                }
                first.ShouldBeGreaterThan(0, $"no packet ends before restart frame {entry.RestartFrame}");
                if (first + 1 < packets.IsLong.Length && !packets.IsLong[first] && packets.IsLong[first + 1])
                    shortToLong++;
                if (packets.IsLong[first - 1])
                    longCount++;
                else
                    shortCount++;
            }

            int bad = BackwardSweep(decoder, whole, EdgeTargets(decoder, pages, packets), out int count);
            decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready, decoder.SeekIndexFailure);
            longToShort.ShouldBeGreaterThan(0, "no restart page starts with a long packet before a short packet");
            shortToLong.ShouldBeGreaterThan(0, "no restart page starts with a short packet before a long packet");
            return (bad, count, decoder.IndexedSeekCount, shortCount, longCount);
        });

        TestContext.Out.WriteLine($"Two-block backward seeks: {targets} targets, {indexed} from an indexed page; restart pages after a short packet {afterShort}, after a long packet {afterLong}.");
        afterShort.ShouldBeGreaterThan(0);
        afterLong.ShouldBeGreaterThan(0);
        indexed.ShouldBeGreaterThan(targets / 2);
        mismatches.ShouldBe(0);
    }

    [Test]
    [CancelAfter(60000)]
    public void BackwardSeek_OnTheEncoderPagesOfATwoBlockFile_MatchesTheLinearDecode()
    {
        // The file as libvorbis wrote it, with its own pages of about 4 KB.
        byte[] file = TwoBlockFixture();
        VorbisTestFileBuilder.Packets packets = VorbisTestFileBuilder.Analyze(file);
        (int mismatches, int indexed) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            BuildIndex(decoder);
            SortedSet<long> targets = [];
            foreach (VorbisPageIndexEntry entry in decoder.PageIndex!.Entries)
            {
                targets.Add(entry.RestartFrame);
                targets.Add(entry.RestartFrame + 1);
                targets.Add(entry.RestartFrame + 5000);
            }
            for (int i = 1; i < packets.IsLong.Length; i += 7)
                targets.Add(packets.Ends[i]);
            targets.Add(decoder.TotalFrames - 1);
            int bad = BackwardSweep(decoder, whole, targets, out _);
            return (bad, decoder.IndexedSeekCount);
        });

        indexed.ShouldBeGreaterThan(0);
        mismatches.ShouldBe(0);
    }

    [Test]
    [CancelAfter(60000)]
    public void RewindBeforeTheIndexIsReady_DecodesFromTheStart_WithNoBuildOnTheCallingThread()
    {
        byte[] file = TwoBlockFixture();
        float[] whole = Linear(file);
        ManualScheduler scheduler = new();
        using VorbisStreamingDecoder decoder = new(new MemoryStream(file, writable: false), leaveOpen: false, () => new MemoryStream(file, writable: false), scheduler);
        float[] buffer = new float[ReadFrames * decoder.Channels];

        decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Building);
        scheduler.Pending.ShouldBe(1);
        decoder.SeekToFrame(60000);
        int bad = SeekMismatches(decoder, whole, 0, buffer);
        bad += SeekMismatches(decoder, whole, 30000, buffer);
        bad += SeekMismatches(decoder, whole, 1000, buffer);

        // No build ran: the build task still waits in the scheduler, and no seek used an index.
        decoder.IndexBuildThreadId.ShouldBe(0);
        scheduler.Pending.ShouldBe(1);
        decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Building);
        decoder.IndexedSeekCount.ShouldBe(0);
        bad.ShouldBe(0);

        // The build runs on another thread. The decoder takes the result at its next seek and does not wait for it.
        int callingThread = Environment.CurrentManagedThreadId;
        Task.Run(scheduler.RunAll).Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        decoder.IndexBuildThreadId.ShouldNotBe(0);
        decoder.IndexBuildThreadId.ShouldNotBe(callingThread);
        decoder.SeekToFrame(decoder.TotalFrames);
        bad += SeekMismatches(decoder, whole, decoder.TotalFrames - 2000, buffer);
        decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready);
        decoder.IndexedSeekCount.ShouldBe(1);

        // A target at the first indexed page or earlier decodes from the start.
        bad += SeekMismatches(decoder, whole, decoder.PageIndex!.Entries[0].RestartFrame + 10, buffer);
        decoder.IndexedSeekCount.ShouldBe(1);
        bad.ShouldBe(0);
    }

    [Test]
    [CancelAfter(60000)]
    public void BackwardSeek_OnTheFixtureFile_DecodesFromTheStartBeforeTheSecondEntry()
    {
        byte[] file = Fixture();
        (int mismatches, int indexed) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            BuildIndex(decoder);
            long total = decoder.TotalFrames;
            float[] buffer = new float[ReadFrames * decoder.Channels];
            int bad = 0;
            foreach (long frame in new long[] { total - 1, total - 300, total - 4000, 22528, 22527, 13230, 512, 1, 0 })
                bad += SeekMismatches(decoder, whole, frame, buffer);
            return (bad, decoder.IndexedSeekCount);
        });

        // The file has one restart page, which is the first entry, so every seek decodes from the start.
        indexed.ShouldBe(0);
        mismatches.ShouldBe(0);
    }

    [Test]
    [CancelAfter(60000)]
    public void DeepBackwardSeek_WithTheIndex_DecodesFarFewerFramesThanAFullDecode()
    {
        // About 37 seconds of audio on pages of up to 4 KB.
        byte[] file = VorbisTestFileBuilder.Build(Fixture(), repeat: 30, maxBodyBytes: 4096, out _);
        (long indexedCost, long fullCost, long target, int mismatches) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            float[] buffer = new float[ReadFrames * 2];

            using VorbisStreamingDecoder full = Open(file, useIndex: false);
            long frame = full.TotalFrames * 9 / 10;
            full.SeekToFrame(full.TotalFrames);
            long before = full.DecodedFrames;
            full.SeekToFrame(frame);
            long fullFrames = full.DecodedFrames - before;

            using VorbisStreamingDecoder indexed = Open(file, useIndex: true);
            BuildIndex(indexed);
            before = indexed.DecodedFrames;
            int bad = SeekMismatches(indexed, whole, frame, buffer);
            long indexedFrames = indexed.DecodedFrames - before - ReadFrames;
            indexed.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready, indexed.SeekIndexFailure);
            return (indexedFrames, fullFrames, frame, bad);
        });

        TestContext.Out.WriteLine($"Backward seek to frame {target}: {fullCost} decoded frames from the start, {indexedCost} decoded frames with the index.");
        fullCost.ShouldBe(target);
        indexedCost.ShouldBeLessThan(fullCost / 20);
        mismatches.ShouldBe(0);
    }

    [Test]
    [CancelAfter(60000)]
    public void DamagedIndexEntry_FallsBackToTheFullDecode_WithOneFailure()
    {
        byte[] file = VorbisTestFileBuilder.Build(Fixture(), repeat: 2, maxBodyBytes: 160, out _);
        (int mismatches, string? firstFailure, string? lastFailure, int indexedBefore, int indexedAfter, EVorbisSeekIndexState state) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            BuildIndex(decoder);
            float[] buffer = new float[ReadFrames * decoder.Channels];

            // Point an entry a few bytes past its page, as if the file changed after the build.
            VorbisPageIndexEntry[] entries = decoder.PageIndex!.Entries;
            int k = entries.Length / 2;
            long frame = entries[k].RestartFrame + 77;
            entries[k] = entries[k] with { ByteOffset = entries[k].ByteOffset + 3 };
            int before = decoder.IndexedSeekCount;

            int bad = SeekMismatches(decoder, whole, frame, buffer);
            string? first = decoder.SeekIndexFailure;
            bad += SeekMismatches(decoder, whole, frame - 1000, buffer);
            bad += SeekMismatches(decoder, whole, 10, buffer);
            return (bad, first, decoder.SeekIndexFailure, before, decoder.IndexedSeekCount, decoder.SeekIndexState);
        });

        TestContext.Out.WriteLine($"Index failure: {firstFailure}");
        mismatches.ShouldBe(0);
        state.ShouldBe(EVorbisSeekIndexState.Failed);
        firstFailure.ShouldNotBeNull();
        lastFailure.ShouldBeSameAs(firstFailure);
        indexedAfter.ShouldBe(indexedBefore);
    }

    [Test]
    [CancelAfter(60000)]
    public void FileThatCannotBeIndexed_FallsBackToTheFullDecode_AndTheSeekStaysExact()
    {
        // Bytes after the end-of-stream page are not an Ogg page, so the build gives no index. NVorbis stops at the end page.
        byte[] fixture = Fixture();
        byte[] file = new byte[fixture.Length + 40];
        fixture.CopyTo(file, 0);
        file.AsSpan(fixture.Length).Fill((byte)'x');
        (int mismatches, EVorbisSeekIndexState state, string? failure) = RunGuarded(() =>
        {
            float[] whole = Linear(file);
            using VorbisStreamingDecoder decoder = Open(file, useIndex: true);
            float[] buffer = new float[ReadFrames * decoder.Channels];
            decoder.SeekToFrame(decoder.TotalFrames);
            int bad = SeekMismatches(decoder, whole, decoder.TotalFrames - 300, buffer);
            bad += SeekMismatches(decoder, whole, 1000, buffer);
            return (bad, decoder.SeekIndexState, decoder.SeekIndexFailure);
        });

        TestContext.Out.WriteLine($"Index failure: {failure}");
        mismatches.ShouldBe(0);
        state.ShouldBe(EVorbisSeekIndexState.Failed);
        failure.ShouldNotBeNull();
    }

    [Test]
    public void IndexMemory_IsBoundedByTheEntryLimit()
    {
        // One packet on each page gives more pages than the entry limit, so the build doubles the stride.
        byte[] file = VorbisTestFileBuilder.Build(Fixture(), repeat: 60, maxBodyBytes: 60, out List<VorbisTestFileBuilder.Page> pages);
        VorbisPageIndex? index = VorbisPageIndex.TryBuild(new MemoryStream(file), CancellationToken.None, out string? failure);

        index.ShouldNotBeNull(failure);
        pages.Count.ShouldBeGreaterThan(VorbisPageIndex.MaxEntries * 2);
        index.Count.ShouldBeLessThanOrEqualTo(VorbisPageIndex.MaxEntries);
        index.Count.ShouldBeGreaterThan(VorbisPageIndex.MaxEntries / 4);
        index.PageStride.ShouldBeGreaterThan(1);
        for (int i = 1; i < index.Count; i++)
            index.Entries[i].RestartFrame.ShouldBeGreaterThan(index.Entries[i - 1].RestartFrame);
    }

    [Test]
    public void SetupHeaderModes_AreReadFromTheEndOfThePacket()
    {
        // Two modes: mode 0 short, mode 1 long. Bits are written from the low bit of each byte up.
        List<bool> bits = [];
        void Write(int value, int count)
        {
            for (int i = 0; i < count; i++)
                bits.Add(((value >> i) & 1) != 0);
        }

        foreach (char c in "\u0005vorbis")
            Write(c, 8);
        Write(0x2A5, 11); // Unrelated fields before the modes.
        Write(1, 6); // Mode count minus one.
        Write(0, 1); Write(0, 16); Write(0, 16); Write(0, 8);
        Write(1, 1); Write(0, 16); Write(0, 16); Write(1, 8);
        Write(1, 1); // Framing bit.

        byte[] packet = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                packet[i >> 3] |= (byte)(1 << (i & 7));
        }

        bool[] flags = new bool[VorbisSetupHeader.MaxModes];
        VorbisSetupHeader.TryReadModeBlockFlags(packet, flags, out int modeCount).ShouldBeTrue();
        modeCount.ShouldBe(2);
        flags[0].ShouldBeFalse();
        flags[1].ShouldBeTrue();
    }
}
