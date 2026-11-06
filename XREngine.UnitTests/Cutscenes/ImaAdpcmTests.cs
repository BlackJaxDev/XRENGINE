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

            // ceil(44101 * 22050 / 44100) frames. Each output frame is the source frame at twice its index.
            mono.Length.ShouldBe(22051);
            float[] expected = Sine(mono.Length, 440.0, 0.5);
            for (int i = 0; i < mono.Length; i++)
                Math.Abs(mono[i] - expected[i]).ShouldBeLessThan(1e-4f);
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
        // A ramp stays a ramp under linear interpolation, so each output frame has a known value.
        const int sourceRate = 48000;
        const int frames = 48000;
        byte[] ramp = BuildStereoWav(sourceRate, frames, (f, c) => f / 65536.0);
        using WavStreamingDecoder wav = OpenWav(ramp);
        float[] mono = AudioProxyBuilder.ToMono(wav, Rate);

        mono.Length.ShouldBe(22050);
        for (int i = 0; i < mono.Length; i++)
        {
            double position = (double)i * sourceRate / Rate;
            Math.Abs(mono[i] - position / 65536.0).ShouldBeLessThan(2e-5, $"output frame {i}");
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
        for (int i = 0; i < decoded.Length; i++)
            Math.Abs(decoded[i] - expected[i]).ShouldBeLessThan((float)SineMaxErrorLimit);
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
