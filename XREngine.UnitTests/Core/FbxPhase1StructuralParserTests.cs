using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxPhase1StructuralParserTests
{
    [Test]
    public void Binary7400_ParsesCompressedArrays_AndFooter()
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinary7400CompressedArrayDocument();

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);

        document.Header.Encoding.ShouldBe(FbxTransportEncoding.Binary);
        document.Header.BinaryVersion.ShouldBe(7400);
        document.Header.IsBigEndian.ShouldBeFalse();
        document.Footer.ShouldNotBeNull();
        document.Footer!.Value.Version.ShouldBe(7400);
        document.Footer.Value.VersionMatchesHeader.ShouldBeTrue();
        document.Nodes.Count.ShouldBe(1);
        document.Properties.Count.ShouldBe(3);
        document.ArrayWorkItems.Count.ShouldBe(1);

        FbxNodeRecord root = document.Nodes[0];
        document.GetNodeName(root).ShouldBe("Root");
        root.PropertyCount.ShouldBe(3);

        FbxPropertyRecord intProperty = document.Properties[0];
        BinaryPrimitives.ReadInt32LittleEndian(document.GetPropertyData(intProperty)).ShouldBe(42);

        FbxPropertyRecord stringProperty = document.Properties[1];
        Encoding.UTF8.GetString(document.GetPropertyData(stringProperty)).ShouldBe("Hello");

        byte[] decoded = FbxStructuralParser.DecodeArrayPayload(document, document.ArrayWorkItems[0]);
        decoded.Length.ShouldBe(sizeof(float) * 3);
        BitConverter.ToSingle(decoded, 0).ShouldBe(1.25f);
        BitConverter.ToSingle(decoded, 4).ShouldBe(2.5f);
        BitConverter.ToSingle(decoded, 8).ShouldBe(5.0f);
    }

    [Test]
    public void Binary7500_BigEndian_UsesWideNodeHeaders()
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinary7500BigEndianDocument();

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);

        document.Header.Encoding.ShouldBe(FbxTransportEncoding.Binary);
        document.Header.BinaryVersion.ShouldBe(7500);
        document.Header.IsBigEndian.ShouldBeTrue();
        document.Nodes.Count.ShouldBe(1);
        document.Properties.Count.ShouldBe(1);
        document.ArrayWorkItems.Count.ShouldBe(0);

        FbxNodeRecord root = document.Nodes[0];
        document.GetNodeName(root).ShouldBe("WideNode");
        root.EndOffset.ShouldBeGreaterThan(25L);

        FbxPropertyRecord property = document.Properties[0];
        BinaryPrimitives.ReadInt32BigEndian(document.GetPropertyData(property)).ShouldBe(77);
    }

    [Test]
    public void BinaryReader_CanSkipNamedSubtrees()
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinary7400WithAnimationSubtree();

        using FbxStructuralDocument document = FbxStructuralParser.Parse(
            data,
            new FbxReaderOptions
            {
                Strictness = FbxReaderStrictness.Strict,
                SkippedNodeNames = ["AnimationStack"],
            });

        document.Nodes.Count.ShouldBe(2);
        document.GetNodeName(document.Nodes[0]).ShouldBe("Root");
        document.GetNodeName(document.Nodes[1]).ShouldBe("AnimationStack");
        document.Nodes[1].Flags.ShouldBe(FbxNodeFlags.SkippedSubtree);
    }

    [Test]
    public void BinaryReader_FailsClosed_OnInvalidEndOffset()
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinary7400CompressedArrayDocument(includeFooter: false);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(27, 4), 31);

        Should.Throw<FbxParseException>(() => FbxStructuralParser.Parse(data)).Message.ShouldContain("endOffset");
    }

    [Test]
    public void BinaryReader_FailsClosed_OnTruncatedCompressedArrayPayload()
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinary7400CompressedArrayDocument(includeFooter: false);
        Array.Resize(ref data, data.Length - 2);

        Should.Throw<FbxParseException>(() => FbxStructuralParser.Parse(data)).Message.ShouldContain("Unexpected end of FBX data");
    }

    [Test]
    public void BinaryReader_FailsClosed_OnMissingSentinel()
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinary7400CompressedArrayDocument(includeFooter: false);
        Array.Resize(ref data, data.Length - FbxBinaryFixtureBuilder.NodeHeaderSize7400);

        Should.Throw<FbxParseException>(() => FbxStructuralParser.Parse(data)).Message.ShouldContain("Unexpected end of FBX data");
    }

    [TestCase(7400, false)]
    [TestCase(7500, true)]
    public void BinaryReader_AllowsBoundaryTerminatedLeafNodes_WithoutChildSentinel(int version, bool bigEndian)
    {
        byte[] data = FbxBinaryFixtureBuilder.CreateBinaryNestedLeafWithoutSentinel(version, bigEndian);

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);

        document.Header.Encoding.ShouldBe(FbxTransportEncoding.Binary);
        document.Header.BinaryVersion.ShouldBe(version);
        document.Header.IsBigEndian.ShouldBe(bigEndian);
        document.Nodes.Count.ShouldBe(2);

        FbxNodeRecord root = document.Nodes[0];
        FbxNodeRecord leaf = document.Nodes[1];
        document.GetNodeName(root).ShouldBe("Root");
        document.GetNodeName(leaf).ShouldBe("Leaf");
        leaf.ParentIndex.ShouldBe(root.Index);

        FbxPropertyRecord property = document.Properties[leaf.FirstPropertyIndex];
        if (bigEndian)
            BinaryPrimitives.ReadInt32BigEndian(document.GetPropertyData(property)).ShouldBe(123);
        else
            BinaryPrimitives.ReadInt32LittleEndian(document.GetPropertyData(property)).ShouldBe(123);
    }

    [Test]
    public void AsciiReader_ParsesObservedGrammarVariants()
    {
        byte[] data = Encoding.UTF8.GetBytes(
            """
            ; FBX 7.4.0 project file
            FBXHeaderExtension:  {
                FBXVersion: 7400
            }
            Relations:  {
                Model: "Model::Scene", "Null"
            }
            Geometry:  {
                Vertices: *3 {
                    a: 0.0, 1.0, 2.0
                }
            }
            Video:  {
                Content: , "YmFzZTY0"
            }
            Connections:  {
                C: "OO", 1, 0
                Connect: "OO", "Model::Child", "Model::Scene"
            }
            """);

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);

        document.Header.Encoding.ShouldBe(FbxTransportEncoding.Ascii);
        document.Header.VersionText.ShouldBe("7.4.0");
        document.Nodes.Count.ShouldBeGreaterThanOrEqualTo(8);
        document.Nodes.Any(node => document.GetNodeName(node) == "Relations").ShouldBeTrue();
        document.Nodes.Any(node => document.GetNodeName(node) == "Connections").ShouldBeTrue();
        document.Nodes.Any(node => document.GetNodeName(node) == "Connect").ShouldBeTrue();
        document.Nodes.Any(node => document.GetNodeName(node) == "C").ShouldBeTrue();

        FbxNodeRecord verticesNode = document.Nodes.First(node => document.GetNodeName(node) == "Vertices");
        FbxPropertyRecord arrayProperty = document.Properties[verticesNode.FirstPropertyIndex];
        arrayProperty.Kind.ShouldBe(FbxPropertyKind.AsciiArray);
        arrayProperty.ArrayLength.ShouldBe(3u);
        document.GetAsciiPropertyText(arrayProperty).ShouldContain("a:");
    }

    [Test]
    public void AsciiReader_Rejects_CheckedInMalformedFixtures()
    {
        foreach (FbxCorpusEntry entry in LoadManifest().Entries.Where(static entry => entry.Availability == FbxCorpusAvailability.SyntheticMalformed && entry.ExpectedEncoding == FbxTransportEncoding.Ascii))
        {
            string path = Path.Combine(ResolveWorkspaceRoot(), entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
            Should.Throw<FbxParseException>(() => FbxStructuralParser.ParseFile(path), $"Fixture '{entry.Id}' should be rejected.");
        }
    }

    [Test]
    public void AsciiReader_Parses_CheckedInAsciiCorpus()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        foreach (FbxCorpusEntry entry in LoadManifest().Entries.Where(static entry => entry.Availability == FbxCorpusAvailability.CheckedIn && entry.ExpectedEncoding == FbxTransportEncoding.Ascii))
        {
            string path = Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
            using FbxStructuralDocument document = FbxStructuralParser.ParseFile(path);

            document.Header.Encoding.ShouldBe(FbxTransportEncoding.Ascii, $"Entry '{entry.Id}' should parse as ASCII.");
            document.Header.VersionText.ShouldBe(entry.ExpectedVersionText, $"Entry '{entry.Id}' should preserve the ASCII version text.");
            document.Nodes.Count.ShouldBeGreaterThan(0, $"Entry '{entry.Id}' should have at least one structural node.");
            document.Properties.Count.ShouldBeGreaterThan(0, $"Entry '{entry.Id}' should have structural properties.");
            document.Nodes.Any(node => document.GetNodeName(node) == "Connections").ShouldBeTrue($"Entry '{entry.Id}' should contain a Connections block.");
        }
    }

    private static FbxCorpusManifest LoadManifest()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, FbxPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return FbxCorpusManifest.Load(manifestPath);
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the workspace root for the FBX phase 1 tests.");
    }

    private static class FbxBinaryFixtureBuilder
    {
        public const int NodeHeaderSize7400 = 13;

        public static byte[] CreateBinary7400CompressedArrayDocument(bool includeFooter = true)
        {
            BinaryNodeSpec root = new(
                "Root",
                [
                    BinaryPropertySpec.Int32(42),
                    BinaryPropertySpec.String("Hello"),
                    BinaryPropertySpec.FloatArray([1.25f, 2.5f, 5.0f], compress: true),
                ],
                []);

            return BuildDocument([root], version: 7400, bigEndian: false, includeFooter);
        }

        public static byte[] CreateBinary7500BigEndianDocument()
        {
            BinaryNodeSpec root = new(
                "WideNode",
                [BinaryPropertySpec.Int32(77)],
                []);

            return BuildDocument([root], version: 7500, bigEndian: true, includeFooter: false);
        }

        public static byte[] CreateBinary7400WithAnimationSubtree()
        {
            BinaryNodeSpec root = new(
                "Root",
                [],
                [
                    new BinaryNodeSpec(
                        "AnimationStack",
                        [BinaryPropertySpec.String("Walk")],
                        [new BinaryNodeSpec("AnimationLayer", [BinaryPropertySpec.Int32(1)], [])]),
                ]);

            return BuildDocument([root], version: 7400, bigEndian: false, includeFooter: false);
        }

        public static byte[] CreateBinaryNestedLeafWithoutSentinel(int version, bool bigEndian)
        {
            BinaryNodeSpec root = new(
                "Root",
                [],
                [new BinaryNodeSpec("Leaf", [BinaryPropertySpec.Int32(123)], [], IncludeSentinel: false)]);

            return BuildDocument([root], version, bigEndian, includeFooter: false);
        }

        private static byte[] BuildDocument(IReadOnlyList<BinaryNodeSpec> roots, int version, bool bigEndian, bool includeFooter)
        {
            using MemoryStream stream = new();
            WriteHeader(stream, version, bigEndian);

            long absoluteOffset = stream.Position;
            foreach (BinaryNodeSpec root in roots)
            {
                WriteNode(stream, root, ref absoluteOffset, version, bigEndian);
            }

            WriteSentinel(stream, version);
            absoluteOffset += GetNodeHeaderSize(version);

            if (includeFooter)
                WriteFooter(stream, version, bigEndian);

            return stream.ToArray();
        }

        private static void WriteHeader(Stream stream, int version, bool bigEndian)
        {
            byte[] magic =
            [
                (byte)'K', (byte)'a', (byte)'y', (byte)'d', (byte)'a', (byte)'r', (byte)'a', (byte)' ',
                (byte)'F', (byte)'B', (byte)'X', (byte)' ', (byte)'B', (byte)'i', (byte)'n', (byte)'a',
                (byte)'r', (byte)'y', (byte)' ', (byte)' ', 0x00, 0x1A,
            ];
            stream.Write(magic);
            stream.WriteByte(bigEndian ? (byte)1 : (byte)0);
            WriteInt32(stream, version, bigEndian);
        }

        private static void WriteNode(Stream stream, BinaryNodeSpec node, ref long absoluteOffset, int version, bool bigEndian)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(node.Name);
            ulong propertyCount = (ulong)node.Properties.Count;
            ulong propertyListLength = (ulong)node.Properties.Sum(static property => property.GetSerializedSize());
            long nodeSize = node.GetSerializedSize(version);
            ulong endOffset = checked((ulong)(absoluteOffset + nodeSize));

            WriteNodeHeader(stream, version, bigEndian, endOffset, propertyCount, propertyListLength, (byte)nameBytes.Length);
            stream.Write(nameBytes);
            foreach (BinaryPropertySpec property in node.Properties)
                property.Write(stream, bigEndian);

            long childAbsoluteOffset = absoluteOffset + GetNodeHeaderSize(version) + nameBytes.Length + (long)propertyListLength;
            foreach (BinaryNodeSpec child in node.Children)
                WriteNode(stream, child, ref childAbsoluteOffset, version, bigEndian);

            if (node.IncludeSentinel)
                WriteSentinel(stream, version);

            absoluteOffset = checked((long)endOffset);
        }

        private static void WriteNodeHeader(Stream stream, int version, bool bigEndian, ulong endOffset, ulong propertyCount, ulong propertyListLength, byte nameLength)
        {
            if (version >= 7500)
            {
                WriteUInt64(stream, endOffset, bigEndian);
                WriteUInt64(stream, propertyCount, bigEndian);
                WriteUInt64(stream, propertyListLength, bigEndian);
                stream.WriteByte(nameLength);
                return;
            }

            WriteUInt32(stream, checked((uint)endOffset), bigEndian);
            WriteUInt32(stream, checked((uint)propertyCount), bigEndian);
            WriteUInt32(stream, checked((uint)propertyListLength), bigEndian);
            stream.WriteByte(nameLength);
        }

        private static void WriteSentinel(Stream stream, int version)
        {
            byte[] zeros = GC.AllocateUninitializedArray<byte>(GetNodeHeaderSize(version));
            zeros.AsSpan().Clear();
            stream.Write(zeros);
        }

        private static void WriteFooter(Stream stream, int version, bool bigEndian)
        {
            byte[] footerId =
            [
                0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66,
                0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E,
            ];
            byte[] terminalMagic =
            [
                0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E,
                0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B,
            ];

            stream.Write(footerId);
            stream.Write(stackalloc byte[4]);

            int paddingLength = 16 - (int)(stream.Position % 16);
            if (paddingLength == 0)
                paddingLength = 16;

            stream.Write(new byte[paddingLength]);
            WriteInt32(stream, version, bigEndian);
            stream.Write(new byte[120]);
            stream.Write(terminalMagic);
        }

        private static int GetNodeHeaderSize(int version)
            => version >= 7500 ? 25 : 13;

        private static void WriteUInt32(Stream stream, uint value, bool bigEndian)
        {
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void WriteInt32(Stream stream, int value, bool bigEndian)
            => WriteUInt32(stream, unchecked((uint)value), bigEndian);

        private static void WriteUInt64(Stream stream, ulong value, bool bigEndian)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            if (bigEndian)
                BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            else
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private sealed record BinaryNodeSpec(string Name, IReadOnlyList<BinaryPropertySpec> Properties, IReadOnlyList<BinaryNodeSpec> Children, bool IncludeSentinel = true)
        {
            public long GetSerializedSize(int version)
            {
                int nameLength = Encoding.ASCII.GetByteCount(Name);
                long propertyBytes = Properties.Sum(static property => property.GetSerializedSize());
                long childBytes = Children.Sum(child => child.GetSerializedSize(version));
                long sentinelBytes = IncludeSentinel ? GetNodeHeaderSize(version) : 0;
                return GetNodeHeaderSize(version) + nameLength + propertyBytes + childBytes + sentinelBytes;
            }
        }

        private sealed class BinaryPropertySpec
        {
            private readonly Action<Stream, bool> _writer;

            private BinaryPropertySpec(int serializedSize, Action<Stream, bool> writer)
            {
                SerializedSize = serializedSize;
                _writer = writer;
            }

            public int SerializedSize { get; }

            public static BinaryPropertySpec Int32(int value)
                => new(1 + sizeof(int), (stream, bigEndian) =>
                {
                    stream.WriteByte((byte)'I');
                    WriteInt32(stream, value, bigEndian);
                });

            public static BinaryPropertySpec String(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                return new BinaryPropertySpec(1 + sizeof(uint) + bytes.Length, (stream, bigEndian) =>
                {
                    stream.WriteByte((byte)'S');
                    WriteUInt32(stream, checked((uint)bytes.Length), bigEndian);
                    stream.Write(bytes);
                });
            }

            public static BinaryPropertySpec FloatArray(float[] values, bool compress)
            {
                byte[] raw = new byte[values.Length * sizeof(float)];
                for (int index = 0; index < values.Length; index++)
                    BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(index * sizeof(float), sizeof(float)), values[index]);

                byte[] payload = compress ? Compress(raw) : raw;
                uint encoding = compress ? 1u : 0u;
                int size = 1 + (sizeof(uint) * 3) + payload.Length;
                return new BinaryPropertySpec(size, (stream, bigEndian) =>
                {
                    stream.WriteByte((byte)'f');
                    WriteUInt32(stream, checked((uint)values.Length), bigEndian);
                    WriteUInt32(stream, encoding, bigEndian);
                    WriteUInt32(stream, checked((uint)payload.Length), bigEndian);
                    stream.Write(payload);
                });
            }

            public int GetSerializedSize() => SerializedSize;

            public void Write(Stream stream, bool bigEndian) => _writer(stream, bigEndian);

            private static byte[] Compress(byte[] raw)
            {
                using MemoryStream stream = new();
                using (ZLibStream zlib = new(stream, CompressionLevel.SmallestSize, leaveOpen: true))
                    zlib.Write(raw);
                return stream.ToArray();
            }
        }
    }
}
