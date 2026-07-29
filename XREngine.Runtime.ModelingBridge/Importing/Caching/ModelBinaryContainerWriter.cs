using System.Buffers.Binary;
using System.IO.Hashing;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Emits canonical little-endian model containers without runtime type metadata.
/// </summary>
internal static class ModelBinaryContainerWriter
{
    public static void Write(
        Stream destination,
        ModelBinaryContainerWriteRequest request,
        ModelCacheReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);
        limits ??= ModelCacheReadLimits.Default;

        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("The destination must be writable and seekable.", nameof(destination));

        ValidateHeaderAndManifest(request);
        ModelImportDependency[] dependencies = NormalizeDependencies(request.Dependencies);
        ValidateDependencyLimits(dependencies, limits);

        ModelBinaryStringPool stringPool = BuildStringPool(request, dependencies, limits);
        byte[] dependencyBytes = SerializeDependencies(dependencies, stringPool);
        byte[] manifestBytes = SerializeManifest(request.Manifest, dependencies.Length, stringPool);
        ModelBinaryChunk[] chunks = BuildCanonicalChunks(request.Chunks, dependencyBytes, manifestBytes, limits);

        ulong stringPoolOffset = ModelBinaryCacheFormat.Align((ulong)ModelBinaryCacheFormat.PreambleSize);
        ulong stringPoolLength = checked((ulong)stringPool.Bytes.Length);
        ulong chunkTableOffset = ModelBinaryCacheFormat.Align(checked(stringPoolOffset + stringPoolLength));
        ulong chunkTableLength = checked((ulong)chunks.Length * ModelBinaryCacheFormat.ChunkEntrySize);
        ulong nextChunkOffset = ModelBinaryCacheFormat.Align(checked(chunkTableOffset + chunkTableLength));

        ModelBinaryChunkEntry[] entries = new ModelBinaryChunkEntry[chunks.Length];
        ulong aggregateDecodedBytes = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            ModelBinaryChunk chunk = chunks[i];
            ulong length = checked((ulong)chunk.DecodedBytes.Length);
            aggregateDecodedBytes = checked(aggregateDecodedBytes + length);
            if (length > limits.MaxChunkBytes || aggregateDecodedBytes > limits.MaxAggregateDecodedBytes)
                throw new ArgumentException("Chunk bytes exceed the configured writer limits.", nameof(request));

            entries[i] = new ModelBinaryChunkEntry(
                chunk.TypeId,
                chunk.Version,
                chunk.Flags,
                chunk.Codec,
                chunk.InstanceId,
                nextChunkOffset,
                length,
                length,
                XxHash3.HashToUInt64(chunk.DecodedBytes.Span),
                chunk.ElementCount);

            nextChunkOffset = ModelBinaryCacheFormat.Align(checked(nextChunkOffset + length));
        }

        ulong fileSize = entries.Length == 0
            ? nextChunkOffset
            : checked(entries[^1].Offset + entries[^1].StoredLength);
        if (fileSize > limits.MaxFileBytes || fileSize > long.MaxValue)
            throw new ArgumentException("The model-cache file exceeds the configured writer limit.", nameof(request));

        byte[] chunkTable = SerializeChunkTable(entries);
        ulong dependencyChecksum = XxHash3.HashToUInt64(dependencyBytes);
        byte[] preamble = SerializePreamble(
            request.Header,
            checked((uint)dependencies.Length),
            stringPool,
            stringPoolOffset,
            chunkTableOffset,
            chunkTable,
            fileSize,
            dependencyChecksum);

        destination.Position = 0;
        destination.SetLength(0);
        destination.Write(preamble);
        WritePadding(destination, stringPoolOffset);
        destination.Write(stringPool.Bytes.Span);
        WritePadding(destination, chunkTableOffset);
        destination.Write(chunkTable);

        for (int i = 0; i < entries.Length; i++)
        {
            WritePadding(destination, entries[i].Offset);
            destination.Write(chunks[i].DecodedBytes.Span);
        }

        destination.SetLength(checked((long)fileSize));
        destination.Position = 0;
    }

    private static void ValidateHeaderAndManifest(ModelBinaryContainerWriteRequest request)
    {
        ModelBinaryCacheWriteHeader header = request.Header;
        ModelBinaryManifest manifest = request.Manifest;
        if (header.EntrySourceHashMode == ModelBinarySourceHashMode.None
            && header.EntrySourceHash != 0)
            throw new ArgumentException("A source hash requires an explicit source hash mode.", nameof(request));
        if (!header.ActualBackendId.Equals(manifest.ActualProducerId, StringComparison.Ordinal))
            throw new ArgumentException("The fixed header and manifest must name the same actual producer.", nameof(request));

        ModelBinaryManifestCandidate? producer = manifest.Candidates.FirstOrDefault(
            candidate => candidate.StableId.Equals(manifest.ActualProducerId, StringComparison.Ordinal));
        if (producer is null || producer.ImplementationVersion != header.ActualBackendVersion)
            throw new ArgumentException("The actual producer version must match its manifest candidate.", nameof(request));
    }

    private static ModelImportDependency[] NormalizeDependencies(
        IReadOnlyList<ModelImportDependency> dependencies)
    {
        ModelImportDependency[] ordered = dependencies
            .OrderBy(static dependency => dependency.NormalizedPath, StringComparer.Ordinal)
            .ThenBy(static dependency => GetDependencyKindId(dependency.Kind))
            .ThenBy(static dependency => dependency.ProducerKey, StringComparer.Ordinal)
            .ToArray();

        for (int i = 1; i < ordered.Length; i++)
        {
            ModelImportDependency previous = ordered[i - 1];
            ModelImportDependency current = ordered[i];
            if (previous.Kind == current.Kind
                && previous.NormalizedPath.Equals(current.NormalizedPath, StringComparison.Ordinal)
                && string.Equals(previous.ProducerKey, current.ProducerKey, StringComparison.Ordinal))
                throw new ArgumentException("The dependency manifest contains a duplicate stable dependency key.", nameof(dependencies));
        }

        return ordered;
    }

    private static void ValidateDependencyLimits(
        IReadOnlyList<ModelImportDependency> dependencies,
        ModelCacheReadLimits limits)
    {
        if ((uint)dependencies.Count > limits.MaxElementCount || dependencies.Count > int.MaxValue)
            throw new ArgumentException("The dependency count exceeds the configured writer limit.", nameof(dependencies));
    }

    private static ModelBinaryStringPool BuildStringPool(
        ModelBinaryContainerWriteRequest request,
        IReadOnlyList<ModelImportDependency> dependencies,
        ModelCacheReadLimits limits)
    {
        List<string?> values =
        [
            request.Header.ActualBackendId,
            request.Header.SourceIdentity,
            request.Manifest.ActualProducerId,
            request.Manifest.SourceExtension,
        ];

        for (int i = 0; i < request.Manifest.Candidates.Count; i++)
            values.Add(request.Manifest.Candidates[i].StableId);

        for (int i = 0; i < dependencies.Count; i++)
        {
            ModelImportDependency dependency = dependencies[i];
            values.Add(dependency.NormalizedPath);
            values.Add(dependency.ContentHash);
            values.Add(dependency.ProducerKey);
        }

        return ModelBinaryStringPool.Build(values, limits);
    }

    private static byte[] SerializeDependencies(
        IReadOnlyList<ModelImportDependency> dependencies,
        ModelBinaryStringPool stringPool)
    {
        int length = checked(
            ModelBinaryCacheFormat.DependencyHeaderSize
            + dependencies.Count * ModelBinaryCacheFormat.DependencyRecordSize);
        byte[] bytes = new byte[length];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span, ModelBinaryChunkVersions.Dependencies);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ModelBinaryCacheFormat.DependencyRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], checked((uint)dependencies.Count));

        int offset = ModelBinaryCacheFormat.DependencyHeaderSize;
        for (int i = 0; i < dependencies.Count; i++)
        {
            ModelImportDependency dependency = dependencies[i];
            Span<byte> record = span.Slice(offset, ModelBinaryCacheFormat.DependencyRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, stringPool.GetOffset(dependency.NormalizedPath));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], stringPool.GetOffset(dependency.ProducerKey));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], stringPool.GetOffset(dependency.ContentHash));
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], GetDependencyKindId(dependency.Kind));
            BinaryPrimitives.WriteUInt32LittleEndian(
                record[16..],
                dependency.IsRequired ? ModelBinaryCacheFormat.DependencyRequiredFlag : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], GetDependencyHashModeId(dependency.ContentHashMode));
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], checked((ulong)dependency.Length));
            BinaryPrimitives.WriteInt64LittleEndian(record[32..], dependency.LastWriteTimeUtcTicks);
            offset += ModelBinaryCacheFormat.DependencyRecordSize;
        }

        return bytes;
    }

    private static byte[] SerializeManifest(
        ModelBinaryManifest manifest,
        int dependencyCount,
        ModelBinaryStringPool stringPool)
    {
        int length = checked(
            ModelBinaryCacheFormat.ManifestHeaderSize
            + manifest.Candidates.Count * ModelBinaryCacheFormat.ManifestCandidateRecordSize);
        byte[] bytes = new byte[length];
        Span<byte> span = bytes;

        BinaryPrimitives.WriteUInt32LittleEndian(span, ModelBinaryCacheFormat.ManifestFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ModelBinaryCacheFormat.ManifestHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], manifest.FeatureFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], stringPool.GetOffset(manifest.ActualProducerId));
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], stringPool.GetOffset(manifest.SourceExtension));
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], manifest.ResolverPolicyVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], GetBackendPolicyId(manifest.RequestedPolicy));
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], GetBackendPolicyId(manifest.HostPreference));
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], checked((uint)manifest.Candidates.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], checked((uint)dependencyCount));
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], manifest.SourceEntityCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], manifest.ReferenceCount);

        WriteManifestCount(span, 56, manifest.NodeCount);
        WriteManifestCount(span, 64, manifest.ModelCount);
        WriteManifestCount(span, 72, manifest.SubMeshCount);
        WriteManifestCount(span, 80, manifest.MeshCount);
        WriteManifestCount(span, 88, manifest.VertexCount);
        WriteManifestCount(span, 96, manifest.IndexCount);
        WriteManifestCount(span, 104, manifest.BoneCount);
        WriteManifestCount(span, 112, manifest.MorphTargetCount);
        WriteManifestCount(span, 120, manifest.LodCount);
        WriteManifestCount(span, 128, manifest.MeshletCount);

        int offset = ModelBinaryCacheFormat.ManifestHeaderSize;
        for (int i = 0; i < manifest.Candidates.Count; i++)
        {
            ModelBinaryManifestCandidate candidate = manifest.Candidates[i];
            Span<byte> record = span.Slice(offset, ModelBinaryCacheFormat.ManifestCandidateRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, stringPool.GetOffset(candidate.StableId));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], candidate.ImplementationVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], GetBackendCapabilityIds(candidate.Capabilities));
            offset += ModelBinaryCacheFormat.ManifestCandidateRecordSize;
        }

        return bytes;
    }

    private static ModelBinaryChunk[] BuildCanonicalChunks(
        IReadOnlyList<ModelBinaryChunk> suppliedChunks,
        byte[] dependencyBytes,
        byte[] manifestBytes,
        ModelCacheReadLimits limits)
    {
        List<ModelBinaryChunk> chunks = new(suppliedChunks.Count + 2)
        {
            new(
                ModelBinaryChunkType.Dependencies,
                ModelBinaryChunkFlags.Required,
                instanceId: 0,
                dependencyBytes,
                elementCount: BinaryPrimitives.ReadUInt32LittleEndian(dependencyBytes.AsSpan(8))),
            new(
                ModelBinaryChunkType.Manifest,
                ModelBinaryChunkFlags.Required,
                instanceId: 0,
                manifestBytes),
        };

        for (int i = 0; i < suppliedChunks.Count; i++)
        {
            ModelBinaryChunk chunk = suppliedChunks[i];
            if (chunk.TypeId is (uint)ModelBinaryChunkType.Dependencies or (uint)ModelBinaryChunkType.Manifest)
                throw new ArgumentException("Dependencies and Manifest chunks are generated by the container writer.", nameof(suppliedChunks));
            chunks.Add(chunk);
        }

        ModelBinaryChunk[] ordered = chunks
            .OrderBy(static chunk => chunk.TypeId)
            .ThenBy(static chunk => chunk.InstanceId)
            .ToArray();
        if ((uint)ordered.Length > limits.MaxChunkCount)
            throw new ArgumentException("The chunk count exceeds the configured writer limit.", nameof(suppliedChunks));

        HashSet<ModelBinaryChunkKey> keys = [];
        for (int i = 0; i < ordered.Length; i++)
        {
            ValidateChunkContract(ordered[i], limits);
            if (!keys.Add(ordered[i].Key))
                throw new ArgumentException("The container contains a duplicate chunk key.", nameof(suppliedChunks));
        }

        RequireChunk(ordered, ModelBinaryChunkType.PrefabGraph);
        RequireChunk(ordered, ModelBinaryChunkType.ImportedEntityTable);
        return ordered;
    }

    private static void ValidateChunkContract(ModelBinaryChunk chunk, ModelCacheReadLimits limits)
    {
        if ((chunk.Flags & ~ModelBinaryChunkFlags.Required) != 0)
            throw new ArgumentException("A chunk contains flags not defined by schema v1.", nameof(chunk));
        if (chunk.Codec != ModelBinaryChunkCodec.None)
            throw new ArgumentException("Schema v1 does not support compressed chunks.", nameof(chunk));
        if ((ulong)chunk.DecodedBytes.Length > limits.MaxChunkBytes)
            throw new ArgumentException("A chunk exceeds the configured byte limit.", nameof(chunk));
        if (chunk.ElementCount > GetElementLimit(chunk.TypeId, limits))
            throw new ArgumentException("A chunk element count exceeds the configured limit.", nameof(chunk));

        if (!ModelBinaryCacheFormat.IsKnownChunkType(chunk.TypeId))
        {
            if ((chunk.Flags & ModelBinaryChunkFlags.Required) != 0)
                throw new ArgumentException("The writer cannot emit an unknown required chunk.", nameof(chunk));
            return;
        }

        if (chunk.Version != ModelBinaryCacheFormat.GetChunkVersion(chunk.TypeId))
            throw new ArgumentException("A known chunk uses an unsupported version.", nameof(chunk));
        if (ModelBinaryCacheFormat.IsSingletonChunk(chunk.TypeId) && chunk.InstanceId != 0)
            throw new ArgumentException("A singleton chunk must use instance ID zero.", nameof(chunk));
        if (chunk.DecodedBytes.IsEmpty && !ModelBinaryCacheFormat.AllowsEmptyChunk(chunk.TypeId))
            throw new ArgumentException("This chunk contract does not permit an empty payload.", nameof(chunk));

        ModelBinaryChunkType type = (ModelBinaryChunkType)chunk.TypeId;
        bool required = (chunk.Flags & ModelBinaryChunkFlags.Required) != 0;
        if (type is ModelBinaryChunkType.Dependencies
            or ModelBinaryChunkType.Manifest
            or ModelBinaryChunkType.PrefabGraph
            or ModelBinaryChunkType.ImportedEntityTable)
        {
            if (!required)
                throw new ArgumentException("A mandatory schema-v1 chunk must be marked required.", nameof(chunk));
        }
        else if (type is ModelBinaryChunkType.ColliderHints or ModelBinaryChunkType.Diagnostics)
        {
            if (required)
                throw new ArgumentException("ColliderHints and Diagnostics are optional schema-v1 chunks.", nameof(chunk));
        }
    }

    private static void RequireChunk(IReadOnlyList<ModelBinaryChunk> chunks, ModelBinaryChunkType type)
    {
        if (!chunks.Any(chunk => chunk.TypeId == (uint)type && chunk.InstanceId == 0))
            throw new ArgumentException($"The required {type} chunk is missing.", nameof(chunks));
    }

    private static byte[] SerializeChunkTable(IReadOnlyList<ModelBinaryChunkEntry> entries)
    {
        byte[] table = new byte[checked(entries.Count * ModelBinaryCacheFormat.ChunkEntrySize)];
        for (int i = 0; i < entries.Count; i++)
        {
            ModelBinaryChunkEntry entry = entries[i];
            Span<byte> record = table.AsSpan(
                i * ModelBinaryCacheFormat.ChunkEntrySize,
                ModelBinaryCacheFormat.ChunkEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, entry.TypeId);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], entry.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)entry.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)entry.Codec);
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], entry.InstanceId);
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], entry.Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(record[32..], entry.StoredLength);
            BinaryPrimitives.WriteUInt64LittleEndian(record[40..], entry.DecodedLength);
            BinaryPrimitives.WriteUInt64LittleEndian(record[48..], entry.DecodedChecksum);
            BinaryPrimitives.WriteUInt64LittleEndian(record[56..], entry.ElementCount);
        }

        return table;
    }

    private static byte[] SerializePreamble(
        ModelBinaryCacheWriteHeader header,
        uint dependencyCount,
        ModelBinaryStringPool stringPool,
        ulong stringPoolOffset,
        ulong chunkTableOffset,
        byte[] chunkTable,
        ulong fileSize,
        ulong dependencyChecksum)
    {
        byte[] bytes = new byte[ModelBinaryCacheFormat.PreambleSize];
        Span<byte> span = bytes;
        ModelBinaryCacheFormat.Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], ModelBinaryCacheVersions.Schema);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], ModelBinaryCacheVersions.Payload);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], ModelBinaryCacheFormat.PreambleSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[ModelBinaryCacheFormat.StringPoolOffsetOffset..], stringPoolOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.StringPoolLengthOffset..],
            checked((ulong)stringPool.Bytes.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(span[ModelBinaryCacheFormat.ChunkTableOffsetOffset..], chunkTableOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.ChunkTableLengthOffset..],
            checked((ulong)chunkTable.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.ChunkTableChecksumOffset..],
            XxHash3.HashToUInt64(chunkTable));
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.StringPoolChecksumOffset..],
            XxHash3.HashToUInt64(stringPool.Bytes.Span));
        BinaryPrimitives.WriteUInt32LittleEndian(
            span[ModelBinaryCacheFormat.ChunkCountOffset..],
            checked((uint)(chunkTable.Length / ModelBinaryCacheFormat.ChunkEntrySize)));
        BinaryPrimitives.WriteUInt32LittleEndian(span[100..], ModelBinaryCacheFormat.ChunkEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[104..], header.EntrySourceLength);
        BinaryPrimitives.WriteInt64LittleEndian(span[112..], header.EntrySourceLastWriteUtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(span[120..], header.EntrySourceHash);
        BinaryPrimitives.WriteUInt32LittleEndian(span[128..], GetSourceHashModeId(header.EntrySourceHashMode));
        BinaryPrimitives.WriteUInt32LittleEndian(span[132..], GetAssetTypeId(header.AssetType));
        header.RequestedPolicyHash.CopyTo(span[136..152]);
        header.BackendResolutionHash.CopyTo(span[152..168]);
        header.ActualBackendKeyHash.CopyTo(span[168..184]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[184..], header.ActualBackendVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[188..], stringPool.GetOffset(header.ActualBackendId));
        header.VariantFingerprint.CopyTo(span[192..208]);
        header.ImportOptionsHash.CopyTo(span[208..224]);
        header.ModelCookSettingsHash.CopyTo(span[224..240]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.DependencyManifestHashOffset..],
            dependencyChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ModelBinaryCacheFormat.DependencyCountOffset..], dependencyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[260..], header.MaterialPolicyVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            span[ModelBinaryCacheFormat.SourceIdentityOffset..],
            stringPool.GetOffset(header.SourceIdentity));
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.EngineBuildIdentityOffset..],
            header.EngineBuildIdentity);

        ulong headerChecksum = XxHash3.HashToUInt64(span);
        BinaryPrimitives.WriteUInt64LittleEndian(
            span[ModelBinaryCacheFormat.HeaderChecksumOffset..],
            headerChecksum);
        return bytes;
    }

    private static void WriteManifestCount(Span<byte> span, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], value);

    private static void WritePadding(Stream destination, ulong targetOffset)
    {
        ulong current = checked((ulong)destination.Position);
        if (current > targetOffset)
            throw new InvalidOperationException("A container region overlaps its predecessor.");

        Span<byte> zeroes = stackalloc byte[ModelBinaryCacheFormat.Alignment];
        while (current < targetOffset)
        {
            int count = checked((int)Math.Min((ulong)zeroes.Length, targetOffset - current));
            destination.Write(zeroes[..count]);
            current += checked((uint)count);
        }
    }

    private static ulong GetElementLimit(uint typeId, ModelCacheReadLimits limits)
        => (ModelBinaryChunkType)typeId switch
        {
            ModelBinaryChunkType.PrefabGraph => limits.MaxNodeCount,
            ModelBinaryChunkType.Models => limits.MaxModelCount,
            ModelBinaryChunkType.SubMeshes => limits.MaxSubMeshCount,
            ModelBinaryChunkType.MeshDirectory => limits.MaxMeshCount,
            ModelBinaryChunkType.MeshCoreStreams => limits.MaxVertexCount,
            ModelBinaryChunkType.Skinning => limits.MaxBoneCount,
            ModelBinaryChunkType.Skeletons => limits.MaxBoneCount,
            ModelBinaryChunkType.MorphTargets => limits.MaxMorphTargetCount,
            ModelBinaryChunkType.LodTables => limits.MaxLodCount,
            ModelBinaryChunkType.Meshlets => limits.MaxMeshletCount,
            _ => limits.MaxElementCount,
        };

    private static uint GetDependencyKindId(ModelImportDependencyKind kind)
        => kind switch
        {
            ModelImportDependencyKind.EntrySource => 1,
            ModelImportDependencyKind.Structural => 2,
            ModelImportDependencyKind.ReferencedTexture => 3,
            ModelImportDependencyKind.ReferencedAnimation => 4,
            ModelImportDependencyKind.ReferencedAsset => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static uint GetDependencyHashModeId(ModelImportDependencyHashMode mode)
        => mode switch
        {
            ModelImportDependencyHashMode.None => 0,
            ModelImportDependencyHashMode.Sha256 => 1,
            ModelImportDependencyHashMode.XxHash3_64 => 2,
            ModelImportDependencyHashMode.ProducerDefined => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static uint GetBackendPolicyId(ModelImportBackendPolicy policy)
        => policy switch
        {
            ModelImportBackendPolicy.Auto => 1,
            ModelImportBackendPolicy.Native => 2,
            ModelImportBackendPolicy.Assimp => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

    private static uint GetBackendCapabilityIds(ModelImportBackendCapabilities capabilities)
    {
        const ModelImportBackendCapabilities known =
            ModelImportBackendCapabilities.NativeParser
            | ModelImportBackendCapabilities.GeneralPurposeFallback
            | ModelImportBackendCapabilities.StableSourceEntityIds
            | ModelImportBackendCapabilities.StructuralDependencyDiscovery;
        if ((capabilities & ~known) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));

        uint result = 0;
        if ((capabilities & ModelImportBackendCapabilities.NativeParser) != 0)
            result |= 1 << 0;
        if ((capabilities & ModelImportBackendCapabilities.GeneralPurposeFallback) != 0)
            result |= 1 << 1;
        if ((capabilities & ModelImportBackendCapabilities.StableSourceEntityIds) != 0)
            result |= 1 << 2;
        if ((capabilities & ModelImportBackendCapabilities.StructuralDependencyDiscovery) != 0)
            result |= 1 << 3;
        return result;
    }

    private static uint GetSourceHashModeId(ModelBinarySourceHashMode mode)
        => mode switch
        {
            ModelBinarySourceHashMode.None => 0,
            ModelBinarySourceHashMode.XxHash3_64 => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static uint GetAssetTypeId(ModelBinaryAssetType assetType)
        => assetType switch
        {
            ModelBinaryAssetType.PrefabSource => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(assetType)),
        };
}
