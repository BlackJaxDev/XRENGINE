using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class CompressionReuseTests
{
    [Test]
    public void ReusedLzmaEncoder_CompressingSmallerPayloadAfterLargerPayload_ProducesCanonicalOutput()
    {
        byte[] largerPayload = CreatePayload(4096, seed: 17);
        byte[] smallerPayload = Encoding.UTF8.GetBytes("anim schema packet");

        SevenZip.Compression.LZMA.Encoder encoder = new();
        MemoryStream inputStream = new();
        MemoryStream outputStream = new();

        byte[] firstCompressed = Compression.Compress(largerPayload, ref encoder, ref inputStream, ref outputStream);
        byte[] reusedCompressed = Compression.Compress(smallerPayload, ref encoder, ref inputStream, ref outputStream);
        byte[] canonicalCompressed = Compression.Compress(smallerPayload);

        firstCompressed.Length.ShouldBeGreaterThan(0);
        reusedCompressed.ShouldBe(canonicalCompressed);
        Compression.Decompress(reusedCompressed).ShouldBe(smallerPayload);
    }

    [Test]
    public void ReusedLzmaDecoder_DecompressingSmallerPayloadAfterLargerPayload_RoundTripsExactly()
    {
        byte[] largerPayload = CreatePayload(4096, seed: 23);
        byte[] smallerPayload = Encoding.UTF8.GetBytes("replicated parameter schema");
        byte[] largerCompressed = Compression.Compress(largerPayload);
        byte[] smallerCompressed = Compression.Compress(smallerPayload);

        SevenZip.Compression.LZMA.Decoder decoder = new();
        MemoryStream inputStream = new();
        MemoryStream outputStream = new();
        byte[] decodeBuffer = new byte[largerPayload.Length];

        int firstLength = Compression.Decompress(largerCompressed, 0, largerCompressed.Length, decodeBuffer, 0, ref decoder, ref inputStream, ref outputStream);
        firstLength.ShouldBe(largerPayload.Length);
        decodeBuffer[..firstLength].ShouldBe(largerPayload);

        int secondLength = Compression.Decompress(smallerCompressed, 0, smallerCompressed.Length, decodeBuffer, 0, ref decoder, ref inputStream, ref outputStream);
        secondLength.ShouldBe(smallerPayload.Length);
        decodeBuffer[..secondLength].ShouldBe(smallerPayload);
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];
        Random random = new(seed);
        for (int index = 0; index < payload.Length; index++)
            payload[index] = (byte)((index * 31 + random.Next(0, 5)) & 0xFF);
        return payload;
    }
}