using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Data;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class ImaAdpcmTests
{
    private const int Rate = ImaAdpcmDecoder.ProxySampleRate;
    private const int Block = AudioProxyAsset.DefaultBlockSamples;

    // The limits come from the first run of SineRoundTrip_ErrorIsBelowTheLimit (max 0.009526, RMS 0.003659), with about
    // 25 percent margin. The encode is deterministic, so a change of the error means a change of the encoder.
    private const double SineMaxErrorLimit = 0.012;
    private const double SineRmsErrorLimit = 0.0046;

    // A full-scale square needs about 11 samples to reach the new level, and then rings down. The first run measured a
    // largest error of 0.114 from 12 samples after an edge and 0.0087 from 39 samples. The limits keep a small margin.
    private const int SquareSettleSamples = 12;
    private const float SquareSettledError = 0.13f;
    private const int SquareQuietSamples = 39;
    private const float SquareQuietError = 0.01f;

    private static float[] Sine(int frames, double frequency, double amplitude, int sampleRate = Rate)
    {
        float[] samples = new float[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate));
        return samples;
    }

    /// <summary>A mix of two tones and noise, so that the step index moves up and down.</summary>
    private static float[] Program(int frames)
    {
        Random random = new(1234);
        float[] samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double envelope = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 0.7 * i / Rate);
            samples[i] = (float)(envelope * (0.4 * Math.Sin(2.0 * Math.PI * 220.0 * i / Rate) + 0.2 * Math.Sin(2.0 * Math.PI * 3100.0 * i / Rate))
                + 0.05 * (random.NextDouble() * 2.0 - 1.0));
        }
        return samples;
    }

    private static float[] DecodeAll(IStreamingAudioDecoder decoder, int partFrames)
    {
        float[] all = new float[decoder.TotalFrames];
        float[] part = new float[partFrames];
        int frame = 0;
        while (true)
        {
            int read = decoder.Read(part);
            if (read == 0)
                break;
            Array.Copy(part, 0, all, frame, read);
            frame += read;
        }
        frame.ShouldBe((int)decoder.TotalFrames);
        return all;
    }

    private static byte[] BuildStereoWav(int sampleRate, int frames, Func<int, int, double> sample)
    {
        int dataLength = frames * 2 * 2;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4);
        writer.Write((ushort)4);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < 2; c++)
                writer.Write((short)Math.Clamp(Math.Round(sample(f, c) * 32768.0), short.MinValue, short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }

    private static WavStreamingDecoder OpenWav(byte[] wav)
        => new(new MemoryStream(wav), leaveOpen: false);

    // The low-pass filter holds the first and the last sample, so the frames near the ends are a short transient.
    private const int EdgeFrames = 64;

    private static double Rms(float[] samples, int skip)
    {
        double sum = 0.0;
        int count = 0;
        for (int i = skip; i < samples.Length - skip; i++, count++)
            sum += samples[i] * (double)samples[i];
        return count > 0 ? Math.Sqrt(sum / count) : 0.0;
    }

    private static float[] RoundTrip(float[] source)
    {
        using ImaAdpcmDecoder decoder = new(ImaAdpcmEncoder.Encode(source, Block), Block, source.Length, Rate);
        return DecodeAll(decoder, 1000);
    }

    [Test]
    public void RandomAccessDecode_EqualsSequentialDecode_Exactly()
    {
        float[] source = Program(Rate * 3 + 123);
        byte[] data = ImaAdpcmEncoder.Encode(source, Block);
        using ImaAdpcmDecoder decoder = new(data, Block, source.Length, Rate);
        float[] sequential = DecodeAll(decoder, 777);

        Random random = new(99);
        float[] buffer = new float[3000];
        for (int i = 0; i < 300; i++)
        {
            long frame = random.NextInt64(0, source.Length + 10);
            int length = random.Next(1, buffer.Length);
            decoder.SeekToFrame(frame);
            int read = decoder.Read(buffer.AsSpan(0, length));
            long start = Math.Min(frame, source.Length);
            read.ShouldBe((int)Math.Min(length, source.Length - start));
            buffer.AsSpan(0, read).SequenceEqual(sequential.AsSpan((int)start, read)).ShouldBeTrue($"the read of {length} frames at frame {frame}");
        }
    }

    [Test]
    public void SineRoundTrip_ErrorIsBelowTheLimit()
    {
        float[] source = Sine(Rate * 2, 440.0, 0.5);
        byte[] data = ImaAdpcmEncoder.Encode(source, Block);
        using ImaAdpcmDecoder decoder = new(data, Block, source.Length, Rate);
        float[] decoded = DecodeAll(decoder, 1000);

        double maxError = 0.0;
        double sumSquares = 0.0;
        for (int i = 0; i < source.Length; i++)
        {
            double error = Math.Abs(decoded[i] - source[i]);
            maxError = Math.Max(maxError, error);
            sumSquares += error * error;
        }
        double rms = Math.Sqrt(sumSquares / source.Length);
        TestContext.Out.WriteLine($"IMA ADPCM 440 Hz sine round trip: max error {maxError:0.######}, RMS error {rms:0.######}.");
        maxError.ShouldBeLessThan(SineMaxErrorLimit);
        rms.ShouldBeLessThan(SineRmsErrorLimit);
    }

    [Test]
    public void EncodedSize_IsAbout11KilobytesForEachSecond()
    {
        const int seconds = 10;
        float[] source = Sine(Rate * seconds, 440.0, 0.5);
        byte[] data = ImaAdpcmEncoder.Encode(source, Block);

        // 216 full blocks of 512 bytes, and one block of 828 samples with 4 + 414 bytes.
        data.Length.ShouldBe(216 * 512 + 418);
        ImaAdpcmEncoder.GetEncodedBytes(source.Length, Block).ShouldBe(data.Length);
        double bytesPerSecond = (double)data.Length / seconds;
        bytesPerSecond.ShouldBeInRange(10_800.0, 11_400.0);
    }

    [Test]
    public void TwoArgumentDecoder_TakesTheFrameCountFromTheData()
    {
        // 1017 + 828 frames: the last block holds 827 codes, so the data has room for one more frame.
        float[] odd = Sine(Block + 828, 300.0, 0.3);
        using ImaAdpcmDecoder fromData = new(ImaAdpcmEncoder.Encode(odd, Block), Block);
        fromData.TotalFrames.ShouldBe(odd.Length + 1);
        fromData.SampleRate.ShouldBe(Rate);
        fromData.Channels.ShouldBe(1);

        float[] even = Sine(Block + 829, 300.0, 0.3);
        using ImaAdpcmDecoder exact = new(ImaAdpcmEncoder.Encode(even, Block), Block);
        exact.TotalFrames.ShouldBe(even.Length);
    }

    [Test]
    public void Read_AllocatesNothing()
    {
        float[] source = Program(Rate * 2);
        using ImaAdpcmDecoder decoder = new(ImaAdpcmEncoder.Encode(source, Block), Block, source.Length, Rate);
        float[] buffer = new float[2048];
        long[] seeks = [0, 5000, 17, 40000, 44099, 22050, 1016, 1017];

        void Work()
        {
            for (int i = 0; i < seeks.Length; i++)
            {
                decoder.SeekToFrame(seeks[i]);
                decoder.Read(buffer);
                decoder.Read(buffer);
            }
        }

        Work();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Work();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0L);
    }

    [Test]
    public void Decoder_RejectsAnEvenBlockSizeAndABadStepIndex()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ImaAdpcmEncoder.Encode(new float[10], 1016));
        Should.Throw<ArgumentOutOfRangeException>(() => new ImaAdpcmDecoder(new byte[512], 1016));

        byte[] data = ImaAdpcmEncoder.Encode(Sine(Block * 2, 440.0, 0.5), Block);
        data[512 + 2] = 89;
        Should.Throw<InvalidDataException>(() => new ImaAdpcmDecoder(data, Block));
        Should.Throw<ArgumentOutOfRangeException>(() => new ImaAdpcmDecoder(new byte[512], Block, Block + 1, Rate));
    }

    [Test]
    public void Builder_MixesStereoToMono_AndResamplesLinearlyTo22050()
    {
        const int sourceRate = 44100;
        const int frames = sourceRate + 1;
        byte[] same = BuildStereoWav(sourceRate, frames, (f, c) => 0.5 * Math.Sin(2.0 * Math.PI * 440.0 * f / sourceRate));
        using (WavStreamingDecoder wav = OpenWav(same))
        {
            float[] mono = AudioProxyBuilder.ToMono(wav, Rate);

            // ceil(44101 * 22050 / 44100) frames. Each output frame is the filtered source frame at twice its index.
            // 440 Hz is far below the cutoff, so the filter keeps it.
            mono.Length.ShouldBe(22051);
            float[] expected = Sine(mono.Length, 440.0, 0.5);
            for (int i = EdgeFrames; i < mono.Length - EdgeFrames; i++)
                Math.Abs(mono[i] - expected[i]).ShouldBeLessThan(1e-3f, $"output frame {i}");
        }

        byte[] opposite = BuildStereoWav(sourceRate, frames, (f, c) => (c == 0 ? 0.5 : -0.5) * Math.Sin(2.0 * Math.PI * 440.0 * f / sourceRate));
        using (WavStreamingDecoder wav = OpenWav(opposite))
        {
            float[] mono = AudioProxyBuilder.ToMono(wav, Rate);
            for (int i = 0; i < mono.Length; i++)
                Math.Abs(mono[i]).ShouldBeLessThan(1e-4f);
        }
    }

    [Test]
    public void Builder_InterpolatesBetweenSourceFrames_At48000()
    {
        // A ramp stays a ramp under a symmetric FIR filter with a DC gain of one and under linear interpolation, so each
        // output frame away from the ends has a known value.
        const int sourceRate = 48000;
        const int frames = 48000;
        byte[] ramp = BuildStereoWav(sourceRate, frames, (f, c) => f / 65536.0);
        using WavStreamingDecoder wav = OpenWav(ramp);
        float[] mono = AudioProxyBuilder.ToMono(wav, Rate);

        mono.Length.ShouldBe(22050);
        for (int i = EdgeFrames; i < mono.Length - EdgeFrames; i++)
        {
            double position = (double)i * sourceRate / Rate;
            Math.Abs(mono[i] - position / 65536.0).ShouldBeLessThan(5e-5, $"output frame {i}");
        }
    }

    [Test]
    public void Proxy_FromAStereoFile_DecodesNearTheSource()
    {
        const int sourceRate = 44100;
        byte[] wav = BuildStereoWav(sourceRate, sourceRate * 2, (f, c) => 0.5 * Math.Sin(2.0 * Math.PI * 440.0 * f / sourceRate));
        using WavStreamingDecoder source = OpenWav(wav);
        AudioProxyAsset proxy = AudioProxyBuilder.Build(source, "game://Audio/Line.wav", 42UL);

        proxy.SampleRate.ShouldBe(Rate);
        proxy.BlockSamples.ShouldBe(Block);
        proxy.FrameCount.ShouldBe(Rate * 2);
        proxy.SourceReference.ShouldBe("game://Audio/Line.wav");
        proxy.SourceContentHash.ShouldBe(42UL);
        proxy.EncodedData.Length.ShouldBe((int)ImaAdpcmEncoder.GetEncodedBytes(Rate * 2, Block));

        using ImaAdpcmDecoder decoder = proxy.CreateDecoder();
        float[] decoded = DecodeAll(decoder, 1000);
        float[] expected = Sine(decoded.Length, 440.0, 0.5);
        for (int i = EdgeFrames; i < decoded.Length - EdgeFrames; i++)
            Math.Abs(decoded[i] - expected[i]).ShouldBeLessThan((float)SineMaxErrorLimit, $"frame {i}");
    }

    [Test]
    public void Builder_RemovesContentAboveTheTargetNyquist_BeforeTheDecimation()
    {
        // Without the filter, a 15 kHz tone at 44.1 kHz folds to 7.05 kHz at full level.
        const int sourceRate = 44100;
        byte[] high = BuildStereoWav(sourceRate, sourceRate, (f, c) => 0.5 * Math.Sin(2.0 * Math.PI * 15000.0 * f / sourceRate));
        using (WavStreamingDecoder wav = OpenWav(high))
            Rms(AudioProxyBuilder.ToMono(wav, Rate), EdgeFrames).ShouldBeLessThan(0.002);

        // A 5 kHz tone is in the pass band and keeps its level (RMS of a 0.5 sine is about 0.354).
        byte[] low = BuildStereoWav(sourceRate, sourceRate, (f, c) => 0.5 * Math.Sin(2.0 * Math.PI * 5000.0 * f / sourceRate));
        using (WavStreamingDecoder wav = OpenWav(low))
            Rms(AudioProxyBuilder.ToMono(wav, Rate), EdgeFrames).ShouldBeInRange(0.35, 0.357);
    }

    [Test]
    public void Builder_AtTheTargetRate_CopiesTheSamplesWithNoFilter()
    {
        byte[] wav = BuildStereoWav(Rate, 5000, (f, c) => ((f * 37) % 2000 - 1000) / 2048.0);
        using WavStreamingDecoder decoder = OpenWav(wav);
        float[] mono = AudioProxyBuilder.ToMono(decoder, Rate);

        mono.Length.ShouldBe(5000);
        for (int f = 0; f < mono.Length; f++)
            mono[f].ShouldBe((float)(Math.Round(((f * 37) % 2000 - 1000) / 2048.0 * 32768.0) / 32768.0), $"frame {f}");
    }

    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    public void Builder_VeryShortSources_GiveTheExpectedFrameCount(int sourceFrames, int expectedFrames)
    {
        byte[] wav = BuildStereoWav(44100, sourceFrames, (f, c) => 0.25);
        using WavStreamingDecoder decoder = OpenWav(wav);
        AudioProxyAsset proxy = AudioProxyBuilder.Build(decoder, "game://Audio/Short.wav", 0UL);

        proxy.FrameCount.ShouldBe(expectedFrames);
        using ImaAdpcmDecoder proxyDecoder = proxy.CreateDecoder();
        float[] decoded = DecodeAll(proxyDecoder, 16);
        foreach (float value in decoded)
            value.ShouldBe(0.25f, 1e-6f);
    }

    [Test]
    public void Codec_ZeroOneAndTwoSamples_EncodeAndDecode()
    {
        ImaAdpcmEncoder.Encode([], Block).Length.ShouldBe(0);
        using (ImaAdpcmDecoder empty = new([], Block))
        {
            empty.TotalFrames.ShouldBe(0);
            empty.Read(new float[8]).ShouldBe(0);
        }

        byte[] one = ImaAdpcmEncoder.Encode([0.5f], Block);
        one.Length.ShouldBe(4);
        RoundTrip([0.5f]).ShouldBe([0.5f]);
        using (ImaAdpcmDecoder headerOnly = new(one, Block))
            headerOnly.TotalFrames.ShouldBe(1);

        byte[] two = ImaAdpcmEncoder.Encode([0.0f, 0.5f], Block);
        two.Length.ShouldBe(5);
        float[] decoded = RoundTrip([0.0f, 0.5f]);
        decoded[0].ShouldBe(0.0f);
        Math.Abs(decoded[1] - 0.5f).ShouldBeLessThan(0.06f);
    }

    [Test]
    public void Codec_Silence_DecodesToExactZero()
    {
        float[] decoded = RoundTrip(new float[Block * 3 + 10]);
        foreach (float value in decoded)
            value.ShouldBe(0.0f);
    }

    [Test]
    public void Codec_ConstantAndOffsetSignals_StayNearTheSource()
    {
        // A constant value: each header holds it, and every code adds zero.
        float constant = ImaAdpcmEncoder.ToPcm16(0.3f) / 32768.0f;
        foreach (float value in RoundTrip(Enumerable.Repeat(0.3f, Block * 2 + 5).ToArray()))
            value.ShouldBe(constant);

        // Full scale clips to the 16 bit range.
        RoundTrip([1.0f, 1.0f, 1.0f])[2].ShouldBe(32767.0f / 32768.0f);
        RoundTrip([-1.0f, -1.0f, -1.0f])[2].ShouldBe(-1.0f);

        // A sine with a DC offset keeps the error of a plain sine.
        float[] offset = Sine(Rate, 440.0, 0.3);
        for (int i = 0; i < offset.Length; i++)
            offset[i] += 0.5f;
        float[] decoded = RoundTrip(offset);
        for (int i = 0; i < offset.Length; i++)
            Math.Abs(decoded[i] - offset[i]).ShouldBeLessThan((float)SineMaxErrorLimit, $"frame {i}");
    }

    [Test]
    public void Codec_FullScaleSquare_SettlesAfterEachEdge_AndNeverLeavesTheRange()
    {
        // 50 samples high, 50 samples low. The step must grow at each edge, so the error is large for a few samples.
        const int half = 50;
        float[] square = new float[Block * 4];
        for (int i = 0; i < square.Length; i++)
            square[i] = (i / half) % 2 == 0 ? 1.0f : -1.0f;

        float[] decoded = RoundTrip(square);
        double[] worst = new double[half];
        for (int i = 0; i < square.Length; i++)
            worst[i % half] = Math.Max(worst[i % half], Math.Abs(decoded[i] - square[i]));
        TestContext.Out.WriteLine($"Square error by samples after an edge: {string.Join(", ", worst.Select(e => e.ToString("0.####")))}");

        for (int i = 0; i < square.Length; i++)
        {
            decoded[i].ShouldBeInRange(-1.0f, 1.0f);
            float error = Math.Abs(decoded[i] - square[i]);
            if (i % half >= SquareQuietSamples)
                error.ShouldBeLessThan(SquareQuietError, $"sample {i}");
            else if (i % half >= SquareSettleSamples)
                error.ShouldBeLessThan(SquareSettledError, $"sample {i}");
        }
    }

    [Test]
    public void Proxy_RoundTripsWithTheCutsceneAsset_AndDoesNotChangeTheHash()
    {
        float[] source = Program(Rate / 2);
        CutsceneAsset asset = new() { Name = "Proxies", Duration = CutsceneTime.FromSeconds(1) };
        ulong hashBefore = CutsceneManifestHash.Compute(asset);
        asset.AudioProxies.Add(new AudioProxyAsset
        {
            EncodedData = ImaAdpcmEncoder.Encode(source, Block),
            FrameCount = source.Length,
            SourceReference = "game://Audio/Line.ogg",
            SourceContentHash = 0xFEDCBA9876543210UL,
        });
        CutsceneManifestHash.Compute(asset).ShouldBe(hashBefore);

        string yaml = AssetManager.Serializer.Serialize(asset, typeof(CutsceneAsset));
        // YAML stores the data as one base64 value, not as one sequence item for each byte.
        yaml.Length.ShouldBeLessThan(asset.AudioProxies[0].EncodedData.Length * 4 / 3 + 2000);
        CutsceneAsset? fromYaml = AssetManager.Deserializer.Deserialize<CutsceneAsset>(yaml);
        CutsceneAsset? fromCooked = CookedBinarySerializer.Deserialize(typeof(CutsceneAsset), CookedBinarySerializer.Serialize(asset)) as CutsceneAsset;

        foreach (CutsceneAsset? clone in new[] { fromYaml, fromCooked })
        {
            clone.ShouldNotBeNull();
            clone!.AudioProxies.Count.ShouldBe(1);
            AudioProxyAsset proxy = clone.AudioProxies[0];
            proxy.EncodedData.ShouldBe(asset.AudioProxies[0].EncodedData);
            proxy.SampleRate.ShouldBe(Rate);
            proxy.BlockSamples.ShouldBe(Block);
            proxy.FrameCount.ShouldBe(source.Length);
            proxy.SourceReference.ShouldBe("game://Audio/Line.ogg");
            proxy.SourceContentHash.ShouldBe(0xFEDCBA9876543210UL);
            CutsceneManifestHash.Compute(clone).ShouldBe(hashBefore);
        }
    }
}
