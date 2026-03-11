using NUnit.Framework;
using Shouldly;
using System.Runtime.InteropServices;
using XREngine.Audio;

namespace XREngine.UnitTests.Audio;

[TestFixture]
public sealed class OpenALTransportTests
{
    [TestCase(-1.0f, short.MinValue)]
    [TestCase(-0.5f, -16384)]
    [TestCase(0.0f, 0)]
    [TestCase(0.5f, 16384)]
    [TestCase(1.0f, short.MaxValue)]
    [TestCase(1.25f, short.MaxValue)]
    [TestCase(-1.25f, short.MinValue)]
    public void ConvertFloatSampleToInt16_ClampsAndScalesCorrectly(float sample, short expected)
    {
        OpenALTransport.ConvertFloatSampleToInt16(sample).ShouldBe(expected);
    }

    [Test]
    public void ConvertFloatPcmToInt16Pcm_ConvertsWholeBuffer()
    {
        float[] sourceSamples = [-1.0f, -0.25f, 0.0f, 0.25f, 1.0f];
        ReadOnlySpan<byte> pcm = MemoryMarshal.AsBytes<float>(sourceSamples);
        short[] destination = new short[sourceSamples.Length];

        int convertedCount = OpenALTransport.ConvertFloatPcmToInt16Pcm(pcm, destination);

        convertedCount.ShouldBe(sourceSamples.Length);
        destination.ShouldBe([short.MinValue, -8192, 0, 8192, short.MaxValue]);
    }

    [Test]
    public void ConvertFloatPcmToInt16Pcm_RejectsMisalignedInput()
    {
        byte[] pcm = [0x00, 0x00, 0x80];
        short[] destination = new short[1];

        Should.Throw<ArgumentException>(() => OpenALTransport.ConvertFloatPcmToInt16Pcm(pcm, destination))
            .ParamName.ShouldBe("pcm");
    }
}