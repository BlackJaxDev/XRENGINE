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

    private static VorbisStreamingDecoder Open(byte[] file, bool useIndex)
        => new(new MemoryStream(file, writable: false), leaveOpen: false, useSeekIndex: useIndex);

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

    // Moves the decoder to the end and back by one frame, so the index is built.
    private static void BuildIndex(VorbisStreamingDecoder decoder)
    {
        decoder.SeekToFrame(decoder.TotalFrames);
        decoder.SeekToFrame(decoder.TotalFrames - 1);
        decoder.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready, decoder.SeekIndexFailure);
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

    [Test]
    [CancelAfter(60000)]
    public void BackwardSeek_WithTheIndex_MatchesTheLinearDecodeOnTheFixtureFile()
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

        indexed.ShouldBeGreaterThan(0);
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
        VorbisPageIndex? index = VorbisPageIndex.TryBuild(new MemoryStream(file), out string? failure);

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
