using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Audio;

public sealed class Audio2Face3DNativeBridgeTests
{
    [Test]
    public void ConvertToPcm16Mono_ConvertsUnsigned8BitPcm()
    {
        short[] output = Audio2Face3DNativeBridgeAudioConverter.ConvertToPcm16Mono([0, 128, 255], bitsPerSample: 8, sourceSampleRate: 16000, targetSampleRate: 16000);

        output.Length.ShouldBe(3);
        output[0].ShouldBeLessThan((short)-30000);
        output[1].ShouldBe((short)0);
        output[2].ShouldBeGreaterThan((short)32000);
    }

    [Test]
    public void ConvertToPcm16Mono_Resamples16BitPcmToBridgeRate()
    {
        byte[] source = new byte[sizeof(short) * 2];
        Buffer.BlockCopy(new short[] { 0, short.MaxValue }, 0, source, 0, source.Length);

        short[] output = Audio2Face3DNativeBridgeAudioConverter.ConvertToPcm16Mono(source, bitsPerSample: 16, sourceSampleRate: 8000, targetSampleRate: 16000);

        output.Length.ShouldBe(4);
        output[0].ShouldBe((short)0);
        output[1].ShouldBeGreaterThan((short)15000);
        output[2].ShouldBeGreaterThan(output[1]);
        output[3].ShouldBe(short.MaxValue);
    }
}