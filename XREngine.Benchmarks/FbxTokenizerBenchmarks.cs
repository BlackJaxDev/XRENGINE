using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using XREngine.Fbx;

[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public sealed class FbxTokenizerBenchmarks
{
    private byte[] _ascii = null!;
    private byte[] _binary7400 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ascii = Encoding.UTF8.GetBytes(
            """
            ; FBX 7.4.0 project file
            FBXHeaderExtension:  {
                FBXVersion: 7400
            }
            Geometry:  {
                Vertices: *3 {
                    a: 0.0, 1.0, 2.0
                }
            }
            Connections:  {
                C: "OO", 1, 0
            }
            """);
        _binary7400 = BuildBinary7400();
    }

    [Benchmark(Baseline = true)]
    public int ParseBinary7400()
    {
        using FbxStructuralDocument document = FbxStructuralParser.Parse(_binary7400);
        return document.Nodes.Count;
    }

    [Benchmark]
    public int ParseAsciiSynthetic()
    {
        using FbxStructuralDocument document = FbxStructuralParser.Parse(_ascii);
        return document.Nodes.Count;
    }

    private static byte[] BuildBinary7400()
    {
        using MemoryStream stream = new();

        byte[] header =
        [
            (byte)'K', (byte)'a', (byte)'y', (byte)'d', (byte)'a', (byte)'r', (byte)'a', (byte)' ',
            (byte)'F', (byte)'B', (byte)'X', (byte)' ', (byte)'B', (byte)'i', (byte)'n', (byte)'a',
            (byte)'r', (byte)'y', (byte)' ', (byte)' ', 0x00, 0x1A, 0x00,
        ];
        stream.Write(header);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 7400);
        stream.Write(version);

        byte[] name = Encoding.ASCII.GetBytes("Root");
        byte[] text = Encoding.UTF8.GetBytes("Benchmark");
        byte[] rawFloats = new byte[sizeof(float) * 3];
        BinaryPrimitives.WriteSingleLittleEndian(rawFloats.AsSpan(0, sizeof(float)), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(rawFloats.AsSpan(4, sizeof(float)), 2.0f);
        BinaryPrimitives.WriteSingleLittleEndian(rawFloats.AsSpan(8, sizeof(float)), 3.0f);

        byte[] compressed;
        using (MemoryStream compressedStream = new())
        {
            using (ZLibStream zlib = new(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(rawFloats);
            compressed = compressedStream.ToArray();
        }

        int propertyListLength = (1 + 4 + text.Length) + (1 + 12 + compressed.Length);
        uint endOffset = (uint)(27 + 13 + name.Length + propertyListLength + 13);
        Span<byte> headerBuffer = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer[0..4], endOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer[4..8], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer[8..12], (uint)propertyListLength);
        headerBuffer[12] = (byte)name.Length;
        stream.Write(headerBuffer);
        stream.Write(name);

        stream.WriteByte((byte)'S');
        BinaryPrimitives.WriteUInt32LittleEndian(version, (uint)text.Length);
        stream.Write(version);
        stream.Write(text);

        stream.WriteByte((byte)'f');
        BinaryPrimitives.WriteUInt32LittleEndian(version, 3);
        stream.Write(version);
        BinaryPrimitives.WriteUInt32LittleEndian(version, 1);
        stream.Write(version);
        BinaryPrimitives.WriteUInt32LittleEndian(version, (uint)compressed.Length);
        stream.Write(version);
        stream.Write(compressed);
        stream.Write(new byte[13]);
        stream.Write(new byte[13]);

        return stream.ToArray();
    }
}