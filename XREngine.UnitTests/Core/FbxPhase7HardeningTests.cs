using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxPhase7HardeningTests
{
    [Test]
    public void BinaryArrayParse_FailsClosed_OnUnsupportedEncoding()
    {
        byte[] rawPayload = BuildFloatArrayPayload([1.0f, 2.0f, 3.0f]);
        byte[] data = BuildBinaryFloatArrayDocument(arrayLength: 3, encoding: 2, payload: rawPayload);

        Should.Throw<FbxParseException>(() => FbxStructuralParser.Parse(data))
            .Message.ShouldContain("Binary FBX array encoding must be 0 or 1");
    }

    [Test]
    public void BinaryArrayDecode_FailsClosed_OnDecodedLengthMismatch()
    {
        byte[] rawPayload = BuildFloatArrayPayload([1.0f, 2.0f]);
        byte[] compressedPayload = CompressZlib(rawPayload);
        byte[] data = BuildBinaryFloatArrayDocument(arrayLength: 3, encoding: 1, payload: compressedPayload);

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);
        document.ArrayWorkItems.Count.ShouldBe(1);

        Should.Throw<FbxParseException>(() => FbxStructuralParser.DecodeArrayPayload(document, document.ArrayWorkItems[0]))
            .Message.ShouldContain("Decoded FBX array payload length does not match the declared element count");
    }

    [Test]
    public void CheckedInPerformanceBaselineFixtures_SupportParallelFullRoundTrips()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        FbxCorpusEntry[] entries = LoadManifest().Entries
            .Where(static entry => entry.Availability == FbxCorpusAvailability.CheckedIn && entry.IncludeInPerformanceBaseline)
            .ToArray();

        entries.Length.ShouldBeGreaterThanOrEqualTo(2);

        ConcurrentQueue<Exception> failures = new();
        Parallel.For(
            0,
            12,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            },
            iteration =>
            {
                foreach (FbxCorpusEntry entry in entries)
                {
                    try
                    {
                        ValidateRoundTrip(workspaceRoot, entry);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(new InvalidOperationException($"Phase 7 FBX parallel roundtrip failed on iteration {iteration} for '{entry.Id}'.", ex));
                        return;
                    }
                }
            });

        if (!failures.IsEmpty)
            throw new AggregateException(failures);
    }

    private static void ValidateRoundTrip(string workspaceRoot, FbxCorpusEntry entry)
    {
        string path = Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(path);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = FbxAnimationParser.Parse(structural, semantic, deformers);

        byte[] binary = FbxBinaryExporter.Export(semantic, geometry, deformers, animations);

        using FbxStructuralDocument reparsedStructural = FbxStructuralParser.Parse(binary);
        FbxSemanticDocument reparsedSemantic = FbxSemanticParser.Parse(reparsedStructural);
        FbxGeometryDocument reparsedGeometry = FbxGeometryParser.Parse(reparsedStructural, reparsedSemantic);
        FbxDeformerDocument reparsedDeformers = FbxDeformerParser.Parse(reparsedStructural, reparsedSemantic);
        FbxAnimationDocument reparsedAnimations = FbxAnimationParser.Parse(reparsedStructural, reparsedSemantic, reparsedDeformers);

        reparsedStructural.Nodes.Count.ShouldBeGreaterThan(0, $"'{entry.Id}' should preserve structural nodes after binary export.");
        reparsedSemantic.Objects.Count.ShouldBeGreaterThan(0, $"'{entry.Id}' should preserve semantic objects after binary export.");
        reparsedGeometry.MeshesByObjectId.Count.ShouldBeGreaterThan(0, $"'{entry.Id}' should preserve mesh geometry after binary export.");

        if (entry.Scenarios.Contains(FbxCorpusScenario.SkinnedCharacter))
            reparsedDeformers.SkinsByGeometryObjectId.Count.ShouldBeGreaterThan(0, $"'{entry.Id}' should preserve skin bindings after binary export.");

        if (entry.Scenarios.Contains(FbxCorpusScenario.Blendshapes))
            reparsedDeformers.BlendShapeChannelsByGeometryObjectId.Count.ShouldBeGreaterThan(0, $"'{entry.Id}' should preserve blendshape bindings after binary export.");

        if (entry.Scenarios.Contains(FbxCorpusScenario.Animation))
            reparsedAnimations.Stacks.Count.ShouldBeGreaterThan(0, $"'{entry.Id}' should preserve animation stacks after binary export.");
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

        throw new DirectoryNotFoundException("Could not locate the workspace root for the FBX phase 7 tests.");
    }

    private static byte[] BuildBinaryFloatArrayDocument(uint arrayLength, uint encoding, byte[] payload)
    {
        using MemoryStream stream = new();
        WriteHeader(stream);

        byte[] name = Encoding.ASCII.GetBytes("Root");
        int propertyListLength = 1 + (sizeof(uint) * 3) + payload.Length;
        uint endOffset = checked((uint)(27 + 13 + name.Length + propertyListLength + 13));

        Span<byte> nodeHeader = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(nodeHeader[0..4], endOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(nodeHeader[4..8], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(nodeHeader[8..12], (uint)propertyListLength);
        nodeHeader[12] = (byte)name.Length;
        stream.Write(nodeHeader);
        stream.Write(name);

        stream.WriteByte((byte)'f');
        WriteUInt32(stream, arrayLength);
        WriteUInt32(stream, encoding);
        WriteUInt32(stream, checked((uint)payload.Length));
        stream.Write(payload);
        stream.Write(new byte[13]);
        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static void WriteHeader(Stream stream)
    {
        byte[] header =
        [
            (byte)'K', (byte)'a', (byte)'y', (byte)'d', (byte)'a', (byte)'r', (byte)'a', (byte)' ',
            (byte)'F', (byte)'B', (byte)'X', (byte)' ', (byte)'B', (byte)'i', (byte)'n', (byte)'a',
            (byte)'r', (byte)'y', (byte)' ', (byte)' ', 0x00, 0x1A, 0x00,
        ];

        stream.Write(header);
        WriteUInt32(stream, 7400);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static byte[] BuildFloatArrayPayload(float[] values)
    {
        byte[] payload = new byte[values.Length * sizeof(float)];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(index * sizeof(float), sizeof(float)), values[index]);
        return payload;
    }

    private static byte[] CompressZlib(byte[] payload)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
            stream.Write(payload);
        return output.ToArray();
    }
}
