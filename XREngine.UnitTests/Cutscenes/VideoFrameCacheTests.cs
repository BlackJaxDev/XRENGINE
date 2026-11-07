using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;
using XREngine.Rendering.VideoStreaming;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>The bounded frame cache of the cutscene video track.</summary>
[TestFixture]
public sealed class VideoFrameCacheTests
{
    private const int Width = 4;
    private const int Height = 2;
    private const int FrameBytes = Width * Height * 3;
    private const long FrameTicks = 400_000;

    private static DecodedVideoFrame MakeFrame(int index, byte[] buffer)
    {
        Array.Fill(buffer, (byte)(index & 0xFF));
        return new DecodedVideoFrame(Width, Height, index * FrameTicks, FrameTicks, VideoPixelFormat.Rgb24, buffer);
    }

    [Test]
    public void Lookup_FindsTheFrameThatShowsAtATime()
    {
        VideoFrameCache cache = new(8);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(3, buffer)).ShouldBeTrue();
        cache.Insert(MakeFrame(4, buffer)).ShouldBeTrue();

        cache.TryGetFrame(3 * FrameTicks, out DecodedVideoFrame start).ShouldBeTrue();
        start.PresentationTimestampTicks.ShouldBe(3 * FrameTicks);
        start.PackedData.Span[0].ShouldBe((byte)3);

        cache.TryGetFrame(4 * FrameTicks - 1, out DecodedVideoFrame end).ShouldBeTrue();
        end.PresentationTimestampTicks.ShouldBe(3 * FrameTicks);

        cache.TryGetFrame(4 * FrameTicks + 123, out DecodedVideoFrame next).ShouldBeTrue();
        next.PackedData.Span[0].ShouldBe((byte)4);

        cache.TryGetFrame(5 * FrameTicks, out _).ShouldBeFalse();
        cache.TryGetFrame(3 * FrameTicks - 1, out _).ShouldBeFalse();
        cache.HitCount.ShouldBe(3);
        cache.MissCount.ShouldBe(2);
    }

    [Test]
    public void Insert_CopiesTheData_SoTheCallerCanReuseItsBuffer()
    {
        VideoFrameCache cache = new(4);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(1, buffer));
        Array.Fill(buffer, (byte)99);

        cache.TryGetFrameAt(FrameTicks, out DecodedVideoFrame frame).ShouldBeTrue();
        frame.PackedData.Span[0].ShouldBe((byte)1);
    }

    [Test]
    public void Cache_StaysWithinItsFrameLimit_AndEvictsTheLeastRecentlyUsedFrame()
    {
        VideoFrameCache cache = new(4);
        byte[] buffer = new byte[FrameBytes];
        for (int i = 0; i < 4; i++)
            cache.Insert(MakeFrame(i, buffer));

        // Frame 0 is used again, so frame 1 is now the least recently used frame.
        cache.TryGetFrame(0, out _).ShouldBeTrue();
        cache.Insert(MakeFrame(4, buffer));
        cache.Count.ShouldBe(4);
        cache.Contains(0).ShouldBeTrue();
        cache.Contains(FrameTicks).ShouldBeFalse();

        for (int i = 5; i < 40; i++)
        {
            cache.Insert(MakeFrame(i, buffer));
            cache.Count.ShouldBeLessThanOrEqualTo(cache.Capacity);
        }
        cache.Count.ShouldBe(4);
        cache.EvictionCount.ShouldBe(36);
    }

    [Test]
    public void SameTime_ReplacesTheFrame_AndUsesNoNewSlot()
    {
        VideoFrameCache cache = new(4);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(2, buffer));
        Array.Fill(buffer, (byte)77);
        cache.Insert(new DecodedVideoFrame(Width, Height, 2 * FrameTicks, FrameTicks, VideoPixelFormat.Rgb24, buffer));

        cache.Count.ShouldBe(1);
        cache.TryGetFrameAt(2 * FrameTicks, out DecodedVideoFrame frame).ShouldBeTrue();
        frame.PackedData.Span[0].ShouldBe((byte)77);
    }

    [Test]
    public void SteadyUse_AllocatesNothing_AfterTheSlotBuffersExist()
    {
        VideoFrameCache cache = new(6);
        byte[] buffer = new byte[FrameBytes];
        for (int i = 0; i < 6; i++)
            cache.Insert(MakeFrame(i, buffer));
        cache.BufferAllocationCount.ShouldBe(6);

        // Warm the JIT for the measured calls.
        cache.Insert(MakeFrame(6, buffer));
        cache.TryGetFrame(6 * FrameTicks, out _);

        ReadOnlyMemory<byte> data = buffer;
        bool allFound = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 7; i < 1000; i++)
        {
            buffer[0] = (byte)i;
            cache.Insert(new DecodedVideoFrame(Width, Height, i * FrameTicks, FrameTicks, VideoPixelFormat.Rgb24, data));
            allFound &= cache.TryGetFrame(i * FrameTicks + 10, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        allFound.ShouldBeTrue();
        cache.BufferAllocationCount.ShouldBe(6);
    }

    [Test]
    public void ByteBudget_BoundsTheMemory_AndSlotBuffersAreMadeOnlyWhenFramesCome()
    {
        // Room for three frames by bytes, ten by count.
        VideoFrameCache cache = new(10, 3 * FrameBytes);
        cache.AllocatedBytes.ShouldBe(0);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(0, buffer));
        cache.AllocatedBytes.ShouldBe(FrameBytes);

        for (int i = 1; i < 20; i++)
        {
            cache.Insert(MakeFrame(i, buffer));
            cache.AllocatedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
        }
        cache.Count.ShouldBe(3);
        cache.BufferAllocationCount.ShouldBe(3);
        cache.Contains(19 * FrameTicks).ShouldBeTrue();
        cache.Contains(16 * FrameTicks).ShouldBeFalse();
    }

    [Test]
    public void ByteBudget_SmallerThanOneFrame_StillKeepsOneFrame()
    {
        VideoFrameCache cache = new(4, 1);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(0, buffer)).ShouldBeTrue();
        cache.Insert(MakeFrame(1, buffer)).ShouldBeTrue();
        cache.Count.ShouldBe(1);
        cache.BufferAllocationCount.ShouldBe(1);
        cache.TryGetFrameAt(FrameTicks, out _).ShouldBeTrue();
    }

    [Test]
    public void LargerFrame_GrowsOnlyTheSlotThatStoresIt()
    {
        VideoFrameCache cache = new(2);
        cache.Insert(MakeFrame(0, new byte[FrameBytes]));
        cache.Insert(new DecodedVideoFrame(8, 8, FrameTicks, FrameTicks, VideoPixelFormat.Rgb24, new byte[8 * 8 * 3]));

        cache.BufferAllocationCount.ShouldBe(2);
        cache.TryGetFrameAt(FrameTicks, out DecodedVideoFrame large).ShouldBeTrue();
        large.Width.ShouldBe(8);
        large.PackedData.Length.ShouldBe(8 * 8 * 3);
    }

    [Test]
    public void CopyToAnotherCache_GivesTheFrameWithNoDecode()
    {
        VideoFrameCache source = new(4);
        VideoFrameCache target = new(4);
        byte[] buffer = new byte[FrameBytes];
        source.Insert(MakeFrame(5, buffer));

        byte[]? scratch = null;
        source.TryCopyFrameTo(5 * FrameTicks + 7, target, ref scratch).ShouldBeTrue();
        source.TryCopyFrameTo(9 * FrameTicks, target, ref scratch).ShouldBeFalse();

        target.TryGetFrame(5 * FrameTicks, out DecodedVideoFrame copy).ShouldBeTrue();
        copy.PackedData.Span[0].ShouldBe((byte)5);
        copy.DurationTicks.ShouldBe(FrameTicks);
    }

    [Test]
    public void Remove_KeepsTheSlotBuffer_AndClearReleasesEveryBuffer()
    {
        VideoFrameCache cache = new(2);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(0, buffer));
        cache.Insert(MakeFrame(1, buffer));

        cache.Remove(0).ShouldBeTrue();
        cache.Remove(0).ShouldBeFalse();
        cache.Count.ShouldBe(1);
        cache.Insert(MakeFrame(2, buffer));
        cache.BufferAllocationCount.ShouldBe(2, "a removed frame leaves its buffer for the next frame");

        cache.Clear();
        cache.Count.ShouldBe(0);
        cache.AllocatedBytes.ShouldBe(0);
    }

    private const int Bytes1080p = 1920 * 1080 * 3;
    private const int Bytes4K = 3840 * 2160 * 3;

    private static DecodedVideoFrame MakeSized(int index, int width, int height, byte[] buffer)
    {
        buffer[0] = (byte)index;
        return new DecodedVideoFrame(width, height, index * FrameTicks, FrameTicks, VideoPixelFormat.Rgb24, buffer);
    }

    [Test]
    public void ResolutionChange_From1080pTo4KMidStream_StaysWithinTheBudget()
    {
        VideoFrameCache cache = new(24, VideoFrameCache.DefaultBudgetBytes);
        byte[] hd = new byte[Bytes1080p];
        byte[] uhd = new byte[Bytes4K];
        int index = 0;
        for (int i = 0; i < 12; i++)
        {
            cache.Insert(MakeSized(index++, 1920, 1080, hd));
            cache.AllocatedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
        }
        cache.Count.ShouldBe((int)(cache.BudgetBytes / Bytes1080p));

        for (int i = 0; i < 12; i++)
        {
            cache.Insert(MakeSized(index++, 3840, 2160, uhd));
            cache.AllocatedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes, $"4K frame {i}");
        }
        cache.Count.ShouldBe((int)(cache.BudgetBytes / Bytes4K));
        cache.AllocatedBytes.ShouldBe(cache.Count * (long)Bytes4K, "no small buffer of the 1080p frames stays");

        // Back to 1080p: the 4K buffers are two or more times the frame size, so they are made smaller.
        for (int i = 0; i < 12; i++)
        {
            cache.Insert(MakeSized(index++, 1920, 1080, hd));
            cache.AllocatedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
        }
        cache.Count.ShouldBe((int)(cache.BudgetBytes / Bytes1080p));
        cache.AllocatedBytes.ShouldBe(cache.Count * (long)Bytes1080p);
    }

    [Test]
    public void ClipChange_FreesTheUnusedBuffers()
    {
        VideoFrameCache cache = new(24, VideoFrameCache.DefaultBudgetBytes);
        byte[] hd = new byte[Bytes1080p];
        for (int i = 0; i < 10; i++)
            cache.Insert(MakeSized(i, 1920, 1080, hd));
        cache.AllocatedBytes.ShouldBeGreaterThan(50L * 1024 * 1024);

        // A clip change clears the cache. The next clip has frames of another size.
        cache.Clear();
        cache.AllocatedBytes.ShouldBe(0);
        cache.Insert(MakeSized(0, 3840, 2160, new byte[Bytes4K]));
        cache.AllocatedBytes.ShouldBe(Bytes4K);
    }

    [Test]
    public void PinnedFrame_IsNeverEvicted_AndDoesNotCountTowardTheBudget()
    {
        VideoFrameCache cache = new(4, 2 * FrameBytes);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(0, buffer), pin: true).ShouldBeTrue();
        for (int i = 1; i < 20; i++)
            cache.Insert(MakeFrame(i, buffer));

        cache.IsPinned(0).ShouldBeTrue();
        cache.Count.ShouldBe(3, "the pinned frame comes on top of two unpinned frames");
        cache.Remove(0).ShouldBeFalse("a pinned frame is not removed");

        cache.Unpin(0).ShouldBeTrue();
        cache.Insert(MakeFrame(20, buffer));
        cache.Insert(MakeFrame(21, buffer));
        cache.Contains(0).ShouldBeFalse("an unpinned frame can be evicted");
    }

    [Test]
    public void AllSlotsPinned_RejectsAnUnpinnedFrame()
    {
        VideoFrameCache cache = new(2);
        byte[] buffer = new byte[FrameBytes];
        cache.Insert(MakeFrame(0, buffer), pin: true).ShouldBeTrue();
        cache.Insert(MakeFrame(1, buffer), pin: true).ShouldBeTrue();
        cache.Insert(MakeFrame(2, buffer)).ShouldBeFalse();
        cache.Count.ShouldBe(2);
    }

    [Test]
    public void OtherPixelFormats_AreNotStored()
    {
        VideoFrameCache cache = new(2);
        cache.Insert(new DecodedVideoFrame(Width, Height, 0, FrameTicks, VideoPixelFormat.Nv12, new byte[FrameBytes])).ShouldBeFalse();
        cache.Insert(new DecodedVideoFrame(Width, Height, 0, FrameTicks, VideoPixelFormat.Rgb24, ReadOnlyMemory<byte>.Empty)).ShouldBeFalse();
        cache.Count.ShouldBe(0);
    }
}
