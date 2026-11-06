using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class StreamingAudioDecoderTests
{
    private const double SeekTolerance = 1e-3;

    /// <summary>Decodes a whole file from the start in parts of a fixed size.</summary>
    private static float[] DecodeAll(IStreamingAudioDecoder decoder, int partFrames)
    {
        float[] all = new float[decoder.TotalFrames * decoder.Channels];
        float[] part = new float[partFrames * decoder.Channels];
        int frame = 0;
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

    /// <summary>Seeks to each frame in order, reads, and returns the largest difference from the whole-file data.</summary>
    private static double MaxSeekError(IStreamingAudioDecoder decoder, float[] whole, long[] frames, int readFrames)
    {
        int channels = decoder.Channels;
        float[] buffer = new float[readFrames * channels];
        double maxError = 0.0;
        foreach (long frame in frames)
        {
            decoder.SeekToFrame(frame);
            int expectedFrames = (int)Math.Min(readFrames, decoder.TotalFrames - frame);
            int read = decoder.Read(buffer);
            read.ShouldBe(expectedFrames, $"read count after a seek to frame {frame}");
            for (int i = 0; i < read * channels; i++)
                maxError = Math.Max(maxError, Math.Abs(buffer[i] - whole[frame * channels + i]));
        }
        return maxError;
    }

    private static byte[] BuildWav(int channels, int sampleRate, int bits, bool isFloat, int frames, Func<int, int, double> sample)
    {
        int bytesPerSample = bits / 8;
        int dataLength = frames * channels * bytesPerSample;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 8 + dataLength + 12);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // An unknown chunk before the format chunk must be skipped.
        writer.Write(Encoding.ASCII.GetBytes("JUNK"));
        writer.Write(4);
        writer.Write(0);

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)(isFloat ? 3 : 1));
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((ushort)(channels * bytesPerSample));
        writer.Write((ushort)bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        Span<byte> bytes = stackalloc byte[4];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                double value = sample(f, c);
                if (isFloat)
                    writer.Write((float)value);
                else if (bits == 16)
                    writer.Write((short)Math.Round(value * 32768.0));
                else
                {
                    int integer = (int)Math.Round(value * 8388608.0);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, integer);
                    writer.Write(bytes[..3]);
                }
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static double Pattern(int frame, int channel)
        => ((frame * 7 + channel * 1000) % 30000 - 15000) / 32768.0;

    private static VorbisStreamingDecoder OpenVorbis()
        => new(new MemoryStream(Convert.FromBase64String(StreamingAudioTestData.VorbisStereoBase64)), leaveOpen: false);

    /// <summary>The linear decode of the Vorbis test file, from a new decoder with no seek.</summary>
    private static float[] LinearVorbis()
    {
        using VorbisStreamingDecoder decoder = OpenVorbis();
        return DecodeAll(decoder, 1000);
    }

    /// <summary>
    /// Runs a decode on another thread and fails if it does not end in time. A seek regression in the decoder library
    /// can loop forever, and this keeps such a loop from blocking the test host.
    /// </summary>
    private static T RunGuarded<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work);
        task.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("The Vorbis decode did not end in 10 seconds.");
        return task.Result;
    }

    /// <summary>Seeks a new decoder to the frame, reads, and returns the largest difference from the linear decode.</summary>
    private static double NewDecoderSeekError(long frame, int readFrames)
        => RunGuarded(() =>
        {
            float[] whole = LinearVorbis();
            using VorbisStreamingDecoder decoder = OpenVorbis();
            return MaxSeekError(decoder, whole, [frame], readFrames);
        });

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_SeekToFrame_MatchesTheWholeFileDecode()
    {
        (double maxError, long total) = RunGuarded(() =>
        {
            float[] whole = LinearVorbis();
            using VorbisStreamingDecoder decoder = OpenVorbis();
            decoder.Channels.ShouldBe(2);
            decoder.SampleRate.ShouldBe(22050);
            decoder.TotalFrames.ShouldBeGreaterThan(20000);

            // Frames at page and packet edges, in an order with backward and forward seeks.
            long count = decoder.TotalFrames;
            long[] frames = [13230, 0, 1, 777, count - 300, 4096, 8191, 20000, 2047, 2048, count / 2, 64];
            return (MaxSeekError(decoder, whole, frames, 512), count);
        });

        TestContext.Out.WriteLine($"Vorbis seek: max absolute error {maxError:E3}, total frames {total}.");
        maxError.ShouldBeLessThan(SeekTolerance);
    }

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_SeekToZero_OnANewDecoder_MatchesTheLinearDecode()
        => NewDecoderSeekError(0, 512).ShouldBeLessThan(SeekTolerance);

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_SeekToOne_MatchesTheLinearDecode()
        => NewDecoderSeekError(1, 512).ShouldBeLessThan(SeekTolerance);

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_SeekTo64_MatchesTheLinearDecode()
        => NewDecoderSeekError(64, 512).ShouldBeLessThan(SeekTolerance);

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_SeekIntoTheLastPage_MatchesTheLinearDecode()
    {
        long total;
        using (VorbisStreamingDecoder decoder = OpenVorbis())
            total = decoder.TotalFrames;
        NewDecoderSeekError(total - 300, 512).ShouldBeLessThan(SeekTolerance);
    }

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_BackwardSeekAfterAForwardRead_MatchesTheLinearDecode()
    {
        double maxError = RunGuarded(() =>
        {
            float[] whole = LinearVorbis();
            using VorbisStreamingDecoder decoder = OpenVorbis();
            float[] buffer = new float[4000 * decoder.Channels];
            decoder.SeekToFrame(15000);
            decoder.Read(buffer).ShouldBe(4000);
            return MaxSeekError(decoder, whole, [3000, 2999, 0], 512);
        });
        maxError.ShouldBeLessThan(SeekTolerance);
    }

    [Test]
    [CancelAfter(30000)]
    public void Vorbis_BackwardSeekCost_IsReported()
    {
        (double seekMs, double realTimeFactor) = RunGuarded(() =>
        {
            using VorbisStreamingDecoder decoder = OpenVorbis();
            decoder.SeekToFrame(decoder.TotalFrames);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            decoder.SeekToFrame(decoder.TotalFrames - 1);
            watch.Stop();
            double fileSeconds = (double)decoder.TotalFrames / decoder.SampleRate;
            return (watch.Elapsed.TotalMilliseconds, fileSeconds / watch.Elapsed.TotalSeconds);
        });
        // The test reports the cost only. A timing assert would depend on the machine load.
        TestContext.Out.WriteLine($"Vorbis backward seek to the end: {seekMs:F2} ms, decode speed {realTimeFactor:F0}x real time.");
        seekMs.ShouldBeGreaterThanOrEqualTo(0.0);
    }
    [Test]
    public void Wav16BitStereo_SeekToFrame_MatchesTheWholeFileDecode()
    {
        byte[] file = BuildWav(2, 8000, 16, false, 10000, Pattern);
        using WavStreamingDecoder decoder = new(new MemoryStream(file), leaveOpen: false);
        decoder.Channels.ShouldBe(2);
        decoder.SampleRate.ShouldBe(8000);
        decoder.TotalFrames.ShouldBe(10000);

        float[] whole = DecodeAll(decoder, 333);
        whole[2 * 1234 + 1].ShouldBe((float)Pattern(1234, 1), 1e-6);

        double maxError = MaxSeekError(decoder, whole, [9000, 0, 1, 4095, 9990, 5000], 400);
        maxError.ShouldBe(0.0);
    }

    [Test]
    public void Wav24BitAndFloat_DecodeTheSampleValues()
    {
        static double Ramp(int frame, int channel) => (frame - 500) / 1000.0;

        using WavStreamingDecoder pcm24 = new(new MemoryStream(BuildWav(1, 44100, 24, false, 1000, Ramp)), leaveOpen: false);
        using WavStreamingDecoder float32 = new(new MemoryStream(BuildWav(1, 44100, 32, true, 1000, Ramp)), leaveOpen: false);

        float[] a = DecodeAll(pcm24, 128);
        float[] b = DecodeAll(float32, 128);
        for (int f = 0; f < 1000; f++)
        {
            a[f].ShouldBe((float)Ramp(f, 0), 1e-6);
            b[f].ShouldBe((float)Ramp(f, 0), 1e-7);
        }
    }

    [Test]
    public void Read_IgnoresAPartialFrameAndStopsAtTheEnd()
    {
        using WavStreamingDecoder decoder = new(new MemoryStream(BuildWav(2, 8000, 16, false, 10, Pattern)), leaveOpen: false);
        float[] buffer = new float[7];
        decoder.Read(buffer).ShouldBe(3);

        decoder.SeekToFrame(8);
        decoder.Read(new float[20]).ShouldBe(2);
        decoder.Read(new float[20]).ShouldBe(0);

        decoder.SeekToFrame(50);
        decoder.Read(new float[20]).ShouldBe(0);
    }

    [Test]
    public void Load3rdParty_ReturnsFalseForFlacAndUnknownFormats()
    {
        new AudioData().Load3rdParty("music.flac").ShouldBeFalse();
        new AudioData().Load3rdParty("music.FLAC").ShouldBeFalse();
        new AudioData().Load3rdParty("music.aiff").ShouldBeFalse();
    }
}
