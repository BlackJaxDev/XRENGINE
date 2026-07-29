using System.Buffers.Binary;
using System.IO.Hashing;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files.Caching;
using XREngine.ModelCaching;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class ModelBinaryContainerTests
{
    private const uint UnknownOptionalChunkType = 0x8000_0001;
    private const ulong MeshInstanceId = 7;

    [Test]
    public void FixedLayout_IsLockedToSchemaV1()
    {
        ModelBinaryCacheFormat.Magic.Length.ShouldBe(16);
        ModelBinaryCacheFormat.PreambleSize.ShouldBe(308);
        ModelBinaryCacheFormat.HeaderChecksumOffset.ShouldBe(40);
        ModelBinaryCacheFormat.DependencyManifestHashOffset.ShouldBe(240);
        ModelBinaryCacheFormat.EngineBuildIdentityOffset.ShouldBe(268);
        (ModelBinaryCacheFormat.ReservedOffset + 32).ShouldBe(ModelBinaryCacheFormat.PreambleSize);
        ModelBinaryCacheFormat.ChunkEntrySize.ShouldBe(64);
        Enum.GetValues<ModelBinaryChunkType>().Length.ShouldBe(20);
    }

    [Test]
    public void WriteThenReadManifest_RoundTripsFixedMetadataAndDependencies()
    {
        byte[] bytes = WriteContainer();

        using MemoryStream stream = new(bytes, writable: false);
        ModelBinaryContainerReadResult result = ModelBinaryContainerReader.ReadManifest(stream);

        result.IsSuccess.ShouldBeTrue(result.Detail);
        ModelBinaryContainer container = result.Container!;
        container.Preamble.FileSize.ShouldBe((ulong)bytes.Length);
        container.Preamble.ActualBackendId.ShouldBe("xrengine.native-gltf");
        container.Preamble.SourceIdentity.ShouldBe("project:assets/models/hero.gltf");
        container.Preamble.DependencyCount.ShouldBe(2u);
        container.Dependencies.Count.ShouldBe(2);
        container.Dependencies[0].Kind.ShouldBe(ModelImportDependencyKind.Structural);
        container.Dependencies[0].ContentHashMode.ShouldBe(ModelImportDependencyHashMode.Sha256);
        container.Dependencies[1].Kind.ShouldBe(ModelImportDependencyKind.EntrySource);
        container.Manifest.ActualProducerId.ShouldBe("xrengine.native-gltf");
        container.Manifest.Candidates.Select(static candidate => candidate.StableId).ShouldBe(
        [
            "xrengine.native-gltf",
            "assimp",
        ]);
        container.Manifest.VertexCount.ShouldBe(3UL);
        container.SelectedChunks.Keys.ShouldBe(
        [
            new ModelBinaryChunkKey((uint)ModelBinaryChunkType.Dependencies, 0),
            new ModelBinaryChunkKey((uint)ModelBinaryChunkType.Manifest, 0),
        ], ignoreOrder: true);
    }

    [Test]
    public void Writer_EmitsIdenticalBytesForReorderedDependenciesAndChunks()
    {
        byte[] canonical = WriteContainer(reverseInputs: false);
        byte[] reordered = WriteContainer(reverseInputs: true);

        reordered.ShouldBe(canonical);
    }

    [Test]
    public void ManifestOnly_SkipsCorruptHeavyBody_WhileSelectiveReadValidatesIt()
    {
        byte[] bytes = WriteContainer();
        ModelBinaryChunkKey meshKey = new((uint)ModelBinaryChunkType.MeshCoreStreams, MeshInstanceId);
        int meshEntryOffset = FindChunkEntryOffset(bytes, meshKey.TypeId, meshKey.InstanceId);
        ulong meshOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(meshEntryOffset + 24));
        bytes[checked((int)meshOffset)] ^= 0x5A;

        using MemoryStream manifestStream = new(bytes, writable: false);
        ModelBinaryContainerReadResult manifestResult =
            ModelBinaryContainerReader.ReadManifest(manifestStream);
        manifestResult.IsSuccess.ShouldBeTrue(manifestResult.Detail);
        manifestResult.Container!.SelectedChunks.ContainsKey(meshKey).ShouldBeFalse();

        using MemoryStream selectedStream = new(bytes, writable: false);
        ModelBinaryContainerReadResult selectedResult =
            ModelBinaryContainerReader.ReadSelected(selectedStream, [meshKey]);
        selectedResult.IsSuccess.ShouldBeFalse();
        selectedResult.Reason.ShouldBe(CacheRejectReason.ChunkChecksumMismatch);
    }

    [Test]
    public void PublicationValidation_ReadsEveryRequiredChunk()
    {
        byte[] bytes = WriteContainer();
        int prefabEntryOffset = FindChunkEntryOffset(bytes, (uint)ModelBinaryChunkType.PrefabGraph, 0);
        ulong prefabOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(prefabEntryOffset + 24));
        bytes[checked((int)prefabOffset)] ^= 0x3C;

        using MemoryStream manifestStream = new(bytes, writable: false);
        ModelBinaryContainerReader.ReadManifest(manifestStream).IsSuccess.ShouldBeTrue();

        using MemoryStream validationStream = new(bytes, writable: false);
        ModelBinaryContainerReadResult result =
            ModelBinaryContainerReader.ValidateRequiredChunks(validationStream);
        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.ChunkChecksumMismatch);
    }

    [Test]
    public void Reader_RejectsTruncationAtEveryCriticalRegion()
    {
        byte[] bytes = WriteContainer();
        ulong stringPoolOffset = ReadUInt64(bytes, ModelBinaryCacheFormat.StringPoolOffsetOffset);
        ulong stringPoolLength = ReadUInt64(bytes, ModelBinaryCacheFormat.StringPoolLengthOffset);
        ulong tableOffset = ReadUInt64(bytes, ModelBinaryCacheFormat.ChunkTableOffsetOffset);
        ulong tableLength = ReadUInt64(bytes, ModelBinaryCacheFormat.ChunkTableLengthOffset);
        int[] cutPoints =
        [
            0,
            15,
            ModelBinaryCacheFormat.PreambleSize - 1,
            checked((int)(stringPoolOffset + stringPoolLength / 2)),
            checked((int)(tableOffset + tableLength / 2)),
            bytes.Length - 1,
        ];

        foreach (int cutPoint in cutPoints.Distinct())
        {
            using MemoryStream stream = new(bytes[..cutPoint], writable: false);
            ModelBinaryContainerReadResult result = ModelBinaryContainerReader.ReadManifest(stream);
            result.IsSuccess.ShouldBeFalse($"cut point {cutPoint} must not be accepted");
            result.Reason.ShouldBe(CacheRejectReason.Truncated, $"cut point {cutPoint}");
        }
    }

    [Test]
    public void Reader_RejectsEachChecksumBoundary()
    {
        byte[] headerCorruption = WriteContainer();
        headerCorruption[104] ^= 0x01;
        ReadManifest(headerCorruption).Reason.ShouldBe(CacheRejectReason.HeaderChecksumMismatch);

        byte[] poolCorruption = WriteContainer();
        int poolDataOffset = checked((int)ReadUInt64(
            poolCorruption,
            ModelBinaryCacheFormat.StringPoolOffsetOffset)) + sizeof(uint) + sizeof(uint);
        poolCorruption[poolDataOffset] ^= 0x01;
        ReadManifest(poolCorruption).Reason.ShouldBe(CacheRejectReason.StringPoolChecksumMismatch);

        byte[] tableCorruption = WriteContainer();
        int tableOffset = checked((int)ReadUInt64(
            tableCorruption,
            ModelBinaryCacheFormat.ChunkTableOffsetOffset));
        tableCorruption[tableOffset + 48] ^= 0x01;
        ReadManifest(tableCorruption).Reason.ShouldBe(CacheRejectReason.ChunkTableChecksumMismatch);

        byte[] dependencyHeaderCorruption = WriteContainer();
        dependencyHeaderCorruption[ModelBinaryCacheFormat.DependencyManifestHashOffset] ^= 0x01;
        RefreshHeaderChecksum(dependencyHeaderCorruption);
        ReadManifest(dependencyHeaderCorruption).Reason.ShouldBe(
            CacheRejectReason.DependencyManifestChecksumMismatch);
    }

    [Test]
    public void Reader_RejectsOverlappingChunkBodies()
    {
        byte[] bytes = WriteContainer();
        int prefabEntry = FindChunkEntryOffset(bytes, (uint)ModelBinaryChunkType.PrefabGraph, 0);
        int entityEntry = FindChunkEntryOffset(bytes, (uint)ModelBinaryChunkType.ImportedEntityTable, 0);
        ulong prefabOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(prefabEntry + 24));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entityEntry + 24), prefabOffset);
        RefreshChunkTableAndHeaderChecksums(bytes);

        ModelBinaryContainerReadResult result = ReadManifest(bytes);

        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.OverlappingChunkRange);
    }

    [Test]
    public void Reader_RejectsAbsurdChunkCountBeforeAllocating()
    {
        byte[] bytes = WriteContainer();
        uint absurdCount = ModelCacheReadLimits.Default.MaxChunkCount + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.ChunkCountOffset),
            absurdCount);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.ChunkTableLengthOffset),
            (ulong)absurdCount * ModelBinaryCacheFormat.ChunkEntrySize);
        RefreshHeaderChecksum(bytes);

        ModelBinaryContainerReadResult result = ReadManifest(bytes);

        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.ResourceLimitExceeded);
    }

    [Test]
    public void Reader_AcceptsAndSkipsUnknownOptionalChunk()
    {
        byte[] bytes = WriteContainer(includeUnknownOptional: true);
        int entryOffset = FindChunkEntryOffset(bytes, UnknownOptionalChunkType, 9);
        ulong bodyOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entryOffset + 24));
        bytes[checked((int)bodyOffset)] ^= 0x7F;

        ModelBinaryContainerReadResult manifestResult = ReadManifest(bytes);

        manifestResult.IsSuccess.ShouldBeTrue(manifestResult.Detail);
        manifestResult.Container!.ChunkEntries.Any(
            entry => entry.TypeId == UnknownOptionalChunkType).ShouldBeTrue();
        manifestResult.Container.SelectedChunks.Keys.Any(
            key => key.TypeId == UnknownOptionalChunkType).ShouldBeFalse();

        using MemoryStream selectedStream = new(bytes, writable: false);
        ModelBinaryContainerReadResult selectedResult = ModelBinaryContainerReader.ReadSelected(
            selectedStream,
            [new ModelBinaryChunkKey(UnknownOptionalChunkType, 9)]);
        selectedResult.Reason.ShouldBe(CacheRejectReason.ChunkChecksumMismatch);
    }

    [Test]
    public void Reader_RejectsUnknownRequiredChunk()
    {
        byte[] bytes = WriteContainer(includeUnknownOptional: true);
        int entryOffset = FindChunkEntryOffset(bytes, UnknownOptionalChunkType, 9);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(entryOffset + 8),
            (uint)ModelBinaryChunkFlags.Required);
        RefreshChunkTableAndHeaderChecksums(bytes);

        ModelBinaryContainerReadResult result = ReadManifest(bytes);

        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.UnknownRequiredChunk);
    }

    [Test]
    public void Reader_RejectsCompressionFieldInSchemaV1()
    {
        byte[] bytes = WriteContainer();
        int meshEntry = FindChunkEntryOffset(
            bytes,
            (uint)ModelBinaryChunkType.MeshCoreStreams,
            MeshInstanceId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(meshEntry + 12), 1);
        RefreshChunkTableAndHeaderChecksums(bytes);

        ModelBinaryContainerReadResult result = ReadManifest(bytes);

        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.UnsupportedChunkCodec);
    }

    [Test]
    public void Reader_RejectsInvalidUtf8OrNulAfterPoolChecksumPasses()
    {
        byte[] bytes = WriteContainer();
        int stringOffset = checked((int)ReadUInt64(
            bytes,
            ModelBinaryCacheFormat.StringPoolOffsetOffset)) + sizeof(uint) + sizeof(uint);
        bytes[stringOffset] = 0;
        RefreshStringPoolAndHeaderChecksums(bytes);

        ModelBinaryContainerReadResult result = ReadManifest(bytes);

        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.InvalidStringPool);
    }

    [Test]
    public void Reader_RejectsSchemaVersionMismatch()
    {
        byte[] bytes = WriteContainer();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), ModelBinaryCacheVersions.Schema + 1);

        ModelBinaryContainerReadResult result = ReadManifest(bytes);

        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldBe(CacheRejectReason.SchemaVersionMismatch);
    }

    [Test]
    public void Writer_RejectsCompressionAndMissingMandatoryChunks()
    {
        ModelBinaryContainerWriteRequest valid = CreateWriteRequest();
        ModelBinaryChunk compressed = new(
            (uint)ModelBinaryChunkType.ColliderHints,
            ModelBinaryChunkVersions.ColliderHints,
            ModelBinaryChunkFlags.None,
            instanceId: 0,
            decodedBytes: [],
            codec: (ModelBinaryChunkCodec)1);
        ModelBinaryContainerWriteRequest compressedRequest = new(
            valid.Header,
            valid.Manifest,
            valid.Dependencies,
            valid.Chunks.Concat([compressed]));
        Should.Throw<ArgumentException>(
            () => ModelBinaryContainerWriter.Write(new MemoryStream(), compressedRequest));

        ModelBinaryContainerWriteRequest missingPrefab = new(
            valid.Header,
            valid.Manifest,
            valid.Dependencies,
            valid.Chunks.Where(chunk => chunk.TypeId != (uint)ModelBinaryChunkType.PrefabGraph));
        Should.Throw<ArgumentException>(
            () => ModelBinaryContainerWriter.Write(new MemoryStream(), missingPrefab));
    }

    [Test]
    public void ExclusiveCodec_RecognizesBinaryManifestAndAppliesSourceGate()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"model-binary-codec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "hero.gltf");
            string cachePath = Path.Combine(directory, "hero.asset");
            DateTime sourceTimestampUtc = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
            File.WriteAllBytes(sourcePath, new byte[123]);
            File.SetLastWriteTimeUtc(sourcePath, sourceTimestampUtc);
            File.WriteAllBytes(cachePath, WriteContainer());

            ModelBinaryCacheCodec codec = new();
            CacheReadResult validManifest = codec.Read(cachePath, sourcePath, sourceTimestampUtc);
            validManifest.Status.ShouldBe(CacheReadStatus.Rejected);
            validManifest.Reason.ShouldBe(CacheRejectReason.CodecUnavailable);

            File.WriteAllBytes(sourcePath, new byte[124]);
            CacheReadResult changedSource = codec.Read(cachePath, sourcePath, sourceTimestampUtc);
            changedSource.Reason.ShouldBe(CacheRejectReason.SourceLengthMismatch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelBinaryContainerReadResult ReadManifest(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        return ModelBinaryContainerReader.ReadManifest(stream);
    }

    private static byte[] WriteContainer(
        bool reverseInputs = false,
        bool includeUnknownOptional = false)
    {
        ModelBinaryContainerWriteRequest request = CreateWriteRequest(
            reverseInputs,
            includeUnknownOptional);
        using MemoryStream stream = new();
        ModelBinaryContainerWriter.Write(stream, request);
        return stream.ToArray();
    }

    private static ModelBinaryContainerWriteRequest CreateWriteRequest(
        bool reverseInputs = false,
        bool includeUnknownOptional = false)
    {
        ModelBinaryCacheWriteHeader header = new(
            entrySourceLength: 123,
            entrySourceLastWriteUtcTicks: new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc).Ticks,
            entrySourceHash: 0,
            ModelBinarySourceHashMode.None,
            ModelBinaryAssetType.PrefabSource,
            ModelBinaryHash128.HashUtf8("requested:auto"),
            ModelBinaryHash128.HashUtf8("resolution:gltf-assimp"),
            actualBackendId: "xrengine.native-gltf",
            actualBackendVersion: 1,
            ModelBinaryHash128.HashUtf8("variant"),
            ModelBinaryHash128.HashUtf8("import-options"),
            ModelBinaryHash128.HashUtf8("cook-settings"),
            materialPolicyVersion: 1,
            sourceIdentity: "project:assets/models/hero.gltf",
            engineBuildIdentity: 0x1020_3040_5060_7080);

        ModelBinaryManifest manifest = new(
            featureFlags: 0x5,
            actualProducerId: "xrengine.native-gltf",
            sourceExtension: ".gltf",
            resolverPolicyVersion: 1,
            requestedPolicy: ModelImportBackendPolicy.Auto,
            hostPreference: ModelImportBackendPolicy.Auto,
            candidates:
            [
                new(
                    "xrengine.native-gltf",
                    1,
                    ModelImportBackendCapabilities.NativeParser
                    | ModelImportBackendCapabilities.StableSourceEntityIds
                    | ModelImportBackendCapabilities.StructuralDependencyDiscovery),
                new(
                    "assimp",
                    1,
                    ModelImportBackendCapabilities.GeneralPurposeFallback),
            ],
            sourceEntityCount: 4,
            referenceCount: 2,
            nodeCount: 2,
            modelCount: 1,
            subMeshCount: 1,
            meshCount: 1,
            vertexCount: 3,
            indexCount: 3,
            boneCount: 0,
            morphTargetCount: 0,
            lodCount: 1,
            meshletCount: 1);

        ModelImportDependency[] dependencies =
        [
            new(
                "c:/project/assets/models/hero.gltf",
                ModelImportDependencyKind.EntrySource,
                isRequired: true,
                length: 123,
                lastWriteTimeUtcTicks: header.EntrySourceLastWriteUtcTicks),
            new(
                "c:/project/assets/models/hero.bin",
                ModelImportDependencyKind.Structural,
                isRequired: true,
                length: 456,
                lastWriteTimeUtcTicks: header.EntrySourceLastWriteUtcTicks - TimeSpan.TicksPerSecond,
                contentHash: new string('a', 64),
                producerKey: "buffer:0",
                contentHashMode: ModelImportDependencyHashMode.Sha256),
        ];

        List<ModelBinaryChunk> chunks =
        [
            new(
                ModelBinaryChunkType.PrefabGraph,
                ModelBinaryChunkFlags.Required,
                instanceId: 0,
                "prefab-graph"u8,
                elementCount: 2),
            new(
                ModelBinaryChunkType.ImportedEntityTable,
                ModelBinaryChunkFlags.Required,
                instanceId: 0,
                "entity-table"u8,
                elementCount: 4),
            new(
                ModelBinaryChunkType.MeshCoreStreams,
                ModelBinaryChunkFlags.Required,
                MeshInstanceId,
                Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray(),
                elementCount: 3),
            new(
                ModelBinaryChunkType.Diagnostics,
                ModelBinaryChunkFlags.None,
                instanceId: 0,
                decodedBytes: []),
        ];

        if (includeUnknownOptional)
        {
            chunks.Add(new ModelBinaryChunk(
                UnknownOptionalChunkType,
                version: 73,
                ModelBinaryChunkFlags.None,
                instanceId: 9,
                "future-optional"u8));
        }

        if (reverseInputs)
        {
            Array.Reverse(dependencies);
            chunks.Reverse();
        }

        return new ModelBinaryContainerWriteRequest(header, manifest, dependencies, chunks);
    }

    private static int FindChunkEntryOffset(byte[] bytes, uint typeId, ulong instanceId)
    {
        int tableOffset = checked((int)ReadUInt64(bytes, ModelBinaryCacheFormat.ChunkTableOffsetOffset));
        uint chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.ChunkCountOffset));
        for (int i = 0; i < chunkCount; i++)
        {
            int entryOffset = checked(tableOffset + i * ModelBinaryCacheFormat.ChunkEntrySize);
            ReadOnlySpan<byte> entry = bytes.AsSpan(entryOffset, ModelBinaryCacheFormat.ChunkEntrySize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) == typeId
                && BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]) == instanceId)
                return entryOffset;
        }

        throw new AssertionException($"Chunk {typeId}:{instanceId} was not found.");
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));

    private static void RefreshChunkTableAndHeaderChecksums(byte[] bytes)
    {
        int tableOffset = checked((int)ReadUInt64(bytes, ModelBinaryCacheFormat.ChunkTableOffsetOffset));
        int tableLength = checked((int)ReadUInt64(bytes, ModelBinaryCacheFormat.ChunkTableLengthOffset));
        ulong tableChecksum = XxHash3.HashToUInt64(bytes.AsSpan(tableOffset, tableLength));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.ChunkTableChecksumOffset),
            tableChecksum);
        RefreshHeaderChecksum(bytes);
    }

    private static void RefreshStringPoolAndHeaderChecksums(byte[] bytes)
    {
        int poolOffset = checked((int)ReadUInt64(bytes, ModelBinaryCacheFormat.StringPoolOffsetOffset));
        int poolLength = checked((int)ReadUInt64(bytes, ModelBinaryCacheFormat.StringPoolLengthOffset));
        ulong poolChecksum = XxHash3.HashToUInt64(bytes.AsSpan(poolOffset, poolLength));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.StringPoolChecksumOffset),
            poolChecksum);
        RefreshHeaderChecksum(bytes);
    }

    private static void RefreshHeaderChecksum(byte[] bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.HeaderChecksumOffset),
            0);
        ulong checksum = XxHash3.HashToUInt64(bytes.AsSpan(0, ModelBinaryCacheFormat.PreambleSize));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.HeaderChecksumOffset),
            checksum);
    }
}
