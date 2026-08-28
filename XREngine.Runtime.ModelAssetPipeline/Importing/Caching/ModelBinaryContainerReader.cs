using System.Buffers.Binary;
using System.IO.Hashing;
using XREngine.Core.Files.Caching;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Defensive, bounded reader for manifest-only, selective, and publication-validation paths.
/// </summary>
internal static class ModelBinaryContainerReader
{
    public static ModelBinaryContainerReadResult ReadManifest(
        Stream source,
        ModelCacheReadLimits? limits = null)
        => ReadCore(source, [], validateAllRequired: false, limits);

    public static ModelBinaryContainerReadResult ReadSelected(
        Stream source,
        IEnumerable<ModelBinaryChunkKey> selectedChunks,
        ModelCacheReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(selectedChunks);
        return ReadCore(source, selectedChunks, validateAllRequired: false, limits);
    }

    public static ModelBinaryContainerReadResult ValidateRequiredChunks(
        Stream source,
        ModelCacheReadLimits? limits = null)
        => ReadCore(source, [], validateAllRequired: true, limits);

    /// <summary>
    /// Reads and semantically validates the optional meshlet section. A caller
    /// must hydrate the returned payloads before registering its meshes with
    /// GPUScene; this reader never triggers parser or meshlet-builder work.
    /// </summary>
    public static ModelBinaryContainerReadResult ReadMeshletSection(
        Stream source,
        out IReadOnlyList<ModelBinaryMeshletSectionEntry>? entries,
        ModelCacheReadLimits? limits = null)
    {
        entries = null;
        ModelBinaryContainerReadResult result = ReadSelected(
            source,
            [new ModelBinaryChunkKey((uint)ModelBinaryChunkType.Meshlets, 0)],
            limits);
        if (!result.IsSuccess)
            return result;

        try
        {
            ModelBinaryContainer container = result.Container!;
            ModelBinaryChunkKey key = new((uint)ModelBinaryChunkType.Meshlets, 0);
            if (!container.SelectedChunks.TryGetValue(key, out ReadOnlyMemory<byte> bytes))
                throw new InvalidDataException("The requested model meshlet section was not loaded.");
            ModelBinaryChunkEntry? entry = container.ChunkEntries.FirstOrDefault(candidate => candidate.Key == key);
            if (entry is null)
                throw new InvalidDataException("The model meshlet section has no matching chunk-table entry.");
            entries = ModelBinaryMeshletSectionCodec.Deserialize(bytes.Span, entry.ElementCount, limits);
            return result;
        }
        catch (InvalidDataException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(CacheRejectReason.InvalidChunkTable, exception.Message);
        }
        catch (EndOfStreamException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(CacheRejectReason.Truncated, exception.Message);
        }
    }

    /// <summary>Reads the optional section while distinguishing absence from rejection.</summary>
    public static ModelBinaryMeshletSectionReadResult ReadOptionalMeshletSection(
        Stream source, ModelCacheReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ModelBinaryContainerReadResult manifest = ReadManifest(source, limits);
        if (!manifest.IsSuccess)
            return ModelBinaryMeshletSectionReadResult.Rejected(
                manifest.Reason,
                manifest.Detail ?? "Model container rejected before optional meshlet lookup.");

        ModelBinaryChunkKey meshletKey = new((uint)ModelBinaryChunkType.Meshlets, 0);
        if (!manifest.Container!.ChunkEntries.Any(entry => entry.Key == meshletKey))
            return ModelBinaryMeshletSectionReadResult.Missing;

        IReadOnlyList<ModelBinaryMeshletSectionEntry>? entries;
        ModelBinaryContainerReadResult result = ReadMeshletSection(source, out entries, limits);
        if (!result.IsSuccess)
            return ModelBinaryMeshletSectionReadResult.Rejected(result.Reason, result.Detail ?? "Meshlet section rejected.");
        return entries is null
            ? ModelBinaryMeshletSectionReadResult.Missing
            : ModelBinaryMeshletSectionReadResult.Present(entries);
    }

    public static ModelBinaryMeshletSectionPublishResult LoadAndPublishMeshletSection(
        Stream source,
        Func<ModelBinaryMeshletSectionKey, XRMesh?> resolveMesh,
        Func<ModelBinaryMeshletSectionKey, XRMesh, MeshletPayload?>? repairFromCachedCore = null,
        Action<IReadOnlyList<ModelBinaryMeshletSectionEntry>>? republish = null,
        bool readOnly = false,
        ModelCacheReadLimits? limits = null,
        IEnumerable<ModelBinaryMeshletSectionKey>? expectedKeys = null,
        IEnumerable<ModelBinaryMeshletSectionEntry>? secondaryRepairEntries = null)
    {
        ModelBinaryMeshletSectionReadResult read = ReadOptionalMeshletSection(source, limits);
        if (read.State == ModelBinaryOptionalSectionState.Rejected &&
            (repairFromCachedCore is null || expectedKeys is null))
            throw new InvalidDataException(read.Detail ?? "Meshlet section rejected.");
        return ModelBinaryMeshletSectionService.LoadAndPublish(
            read.Entries ?? [],
            secondaryRepairEntries,
            resolveMesh,
            repairFromCachedCore,
            republish,
            readOnly,
            expectedKeys);
    }

    public static bool HasMagic(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek || source.Length < ModelBinaryCacheFormat.Magic.Length)
            return false;

        long originalPosition = source.Position;
        Span<byte> magic = stackalloc byte[16];
        try
        {
            source.Position = 0;
            source.ReadExactly(magic);
            return magic.SequenceEqual(ModelBinaryCacheFormat.Magic);
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    private static ModelBinaryContainerReadResult ReadCore(
        Stream source,
        IEnumerable<ModelBinaryChunkKey> requestedChunks,
        bool validateAllRequired,
        ModelCacheReadLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= ModelCacheReadLimits.Default;

        try
        {
            if (!source.CanRead || !source.CanSeek)
                throw Reject(CacheRejectReason.Unreadable, "The model-cache stream must be readable and seekable.");

            ulong actualFileSize = checked((ulong)source.Length);
            if (actualFileSize < ModelBinaryCacheFormat.PreambleSize)
                throw Reject(CacheRejectReason.Truncated, "The file ends before the fixed preamble.");
            if (actualFileSize > limits.MaxFileBytes)
                throw Limit("The file exceeds the configured cache-size limit.");

            HashSet<ModelBinaryChunkKey> requested = new(requestedChunks);
            ModelBinaryRawPreamble rawPreamble = ReadPreamble(source, actualFileSize, limits);
            ValidateMetadataRegions(rawPreamble);

            byte[] stringPoolBytes = ReadRegion(
                source,
                rawPreamble.StringPoolOffset,
                rawPreamble.StringPoolLength,
                limits.MaxStringPoolBytes,
                "string pool");
            if (XxHash3.HashToUInt64(stringPoolBytes) != rawPreamble.StringPoolChecksum)
                throw Reject(CacheRejectReason.StringPoolChecksumMismatch, "The string-pool checksum does not match.");
            ModelBinaryStringPool stringPool = ModelBinaryStringPool.Parse(stringPoolBytes, limits);

            byte[] chunkTableBytes = ReadRegion(
                source,
                rawPreamble.ChunkTableOffset,
                rawPreamble.ChunkTableLength,
                checked((ulong)limits.MaxChunkCount * ModelBinaryCacheFormat.ChunkEntrySize),
                "chunk table");
            if (XxHash3.HashToUInt64(chunkTableBytes) != rawPreamble.ChunkTableChecksum)
                throw Reject(CacheRejectReason.ChunkTableChecksumMismatch, "The chunk-table checksum does not match.");

            ModelBinaryChunkEntry[] entries = ParseChunkTable(chunkTableBytes, rawPreamble, limits);
            ValidateChunkRanges(entries, rawPreamble);
            ValidateMandatoryChunks(entries);
            ModelBinaryCachePreamble preamble = ResolvePreamble(rawPreamble, stringPool);

            ModelBinaryChunkEntry dependencyEntry = GetRequiredEntry(entries, ModelBinaryChunkType.Dependencies);
            byte[] dependencyBytes = ReadAndValidateChunk(source, dependencyEntry);
            ValidateDependencyManifestChecksum(dependencyBytes, rawPreamble.DependencyManifestHash);
            ModelImportDependency[] dependencies = ParseDependencies(
                dependencyBytes,
                dependencyEntry,
                rawPreamble.DependencyCount,
                stringPool,
                limits);

            ModelBinaryChunkEntry manifestEntry = GetRequiredEntry(entries, ModelBinaryChunkType.Manifest);
            byte[] manifestBytes = ReadAndValidateChunk(source, manifestEntry);
            ModelBinaryManifest manifest = ParseManifest(
                manifestBytes,
                rawPreamble,
                stringPool,
                limits);

            Dictionary<ModelBinaryChunkKey, ReadOnlyMemory<byte>> loadedChunks =
                new(entries.Length)
                {
                    [dependencyEntry.Key] = dependencyBytes,
                    [manifestEntry.Key] = manifestBytes,
                };

            for (int i = 0; i < entries.Length; i++)
            {
                ModelBinaryChunkEntry entry = entries[i];
                if (loadedChunks.ContainsKey(entry.Key))
                    continue;

                bool shouldLoad = validateAllRequired
                    ? entry.IsRequired
                    : requested.Contains(entry.Key);
                if (!shouldLoad)
                    continue;

                loadedChunks.Add(entry.Key, ReadAndValidateChunk(source, entry));
            }

            if (!validateAllRequired)
            {
                foreach (ModelBinaryChunkKey requestedKey in requested)
                {
                    if (!loadedChunks.ContainsKey(requestedKey))
                        throw Reject(
                            CacheRejectReason.RequiredChunkMissing,
                            $"Requested chunk {requestedKey.TypeId}:{requestedKey.InstanceId} is absent.");
                }
            }

            return ModelBinaryContainerReadResult.Success(
                new ModelBinaryContainer(preamble, manifest, entries, dependencies, loadedChunks));
        }
        catch (ModelBinaryCacheFormatException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(exception.Reason, exception.Message);
        }
        catch (EndOfStreamException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(CacheRejectReason.Truncated, exception.Message);
        }
        catch (OverflowException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(
                CacheRejectReason.InvalidChunkRange,
                $"Checked container arithmetic overflowed: {exception.Message}");
        }
        catch (OutOfMemoryException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(
                CacheRejectReason.ResourceLimitExceeded,
                $"A bounded cache allocation could not be satisfied: {exception.Message}");
        }
        catch (IOException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return ModelBinaryContainerReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
        }
    }

    private static ModelBinaryRawPreamble ReadPreamble(
        Stream source,
        ulong actualFileSize,
        ModelCacheReadLimits limits)
    {
        byte[] bytes = ReadRegion(
            source,
            offset: 0,
            ModelBinaryCacheFormat.PreambleSize,
            ModelBinaryCacheFormat.PreambleSize,
            "preamble");
        ReadOnlySpan<byte> span = bytes;

        if (!span[..16].SequenceEqual(ModelBinaryCacheFormat.Magic))
            throw Reject(CacheRejectReason.InvalidPreamble, "The model-cache magic is invalid.");

        uint schemaVersion = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        if (schemaVersion != ModelBinaryCacheVersions.Schema)
            throw Reject(
                CacheRejectReason.SchemaVersionMismatch,
                $"Schema {schemaVersion} is not supported; expected {ModelBinaryCacheVersions.Schema}.");

        uint payloadVersion = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        if (payloadVersion != ModelBinaryCacheVersions.Payload)
            throw Reject(
                CacheRejectReason.PayloadVersionMismatch,
                $"Payload {payloadVersion} is not supported; expected {ModelBinaryCacheVersions.Payload}.");

        uint preambleSize = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
        if (preambleSize != ModelBinaryCacheFormat.PreambleSize)
            throw Reject(CacheRejectReason.InvalidPreamble, "The fixed preamble size is unsupported.");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
        if (flags != 0)
            throw Reject(CacheRejectReason.InvalidPreamble, "The preamble contains flags not defined by schema v1.");

        ulong declaredFileSize = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        if (declaredFileSize > limits.MaxFileBytes)
            throw Limit("The declared file size exceeds the configured cache-size limit.");
        if (declaredFileSize > actualFileSize)
            throw Reject(CacheRejectReason.Truncated, "The physical file is shorter than its declared size.");
        if (declaredFileSize != actualFileSize)
            throw Reject(CacheRejectReason.InvalidPreamble, "The physical file size differs from the declared size.");

        ulong expectedHeaderChecksum = BinaryPrimitives.ReadUInt64LittleEndian(
            span[ModelBinaryCacheFormat.HeaderChecksumOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ModelBinaryCacheFormat.HeaderChecksumOffset, sizeof(ulong)),
            0);
        if (XxHash3.HashToUInt64(bytes) != expectedHeaderChecksum)
            throw Reject(CacheRejectReason.HeaderChecksumMismatch, "The fixed-preamble checksum does not match.");

        span = bytes;
        if (!span[248..256].SequenceEqual(stackalloc byte[8]))
            throw Reject(CacheRejectReason.InvalidPreamble, "The dependency-checksum extension bytes must be zero in schema v1.");
        if (!span[ModelBinaryCacheFormat.ReservedOffset..].SequenceEqual(stackalloc byte[32]))
            throw Reject(CacheRejectReason.InvalidPreamble, "The preamble reserved bytes must be zero in schema v1.");

        uint chunkEntrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[100..]);
        if (chunkEntrySize != ModelBinaryCacheFormat.ChunkEntrySize)
            throw Reject(CacheRejectReason.InvalidChunkTable, "The chunk-entry size is unsupported.");

        uint chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(span[ModelBinaryCacheFormat.ChunkCountOffset..]);
        if (chunkCount > limits.MaxChunkCount)
            throw Limit("The chunk count exceeds the configured limit.");

        ulong expectedTableLength = checked((ulong)chunkCount * ModelBinaryCacheFormat.ChunkEntrySize);
        ulong chunkTableLength = BinaryPrimitives.ReadUInt64LittleEndian(
            span[ModelBinaryCacheFormat.ChunkTableLengthOffset..]);
        if (chunkTableLength != expectedTableLength)
            throw Reject(CacheRejectReason.InvalidChunkTable, "The chunk-table length does not match its count.");

        ulong stringPoolLength = BinaryPrimitives.ReadUInt64LittleEndian(
            span[ModelBinaryCacheFormat.StringPoolLengthOffset..]);
        if (stringPoolLength < sizeof(uint))
            throw Reject(CacheRejectReason.InvalidStringPool, "The string pool is too short for its reserved null entry.");
        if (stringPoolLength > limits.MaxStringPoolBytes || stringPoolLength > int.MaxValue)
            throw Limit("The string-pool byte length exceeds the configured limit.");
        if (chunkTableLength > int.MaxValue)
            throw Limit("The chunk table cannot be represented by a CLR buffer.");

        long sourceTicks = BinaryPrimitives.ReadInt64LittleEndian(span[112..]);
        if (sourceTicks < 0)
            throw Reject(CacheRejectReason.InvalidPreamble, "The entry-source timestamp is negative.");
        uint backendVersion = BinaryPrimitives.ReadUInt32LittleEndian(span[184..]);
        if (backendVersion == 0)
            throw Reject(CacheRejectReason.InvalidPreamble, "The actual backend version must be nonzero.");

        ModelBinarySourceHashMode sourceHashMode = ParseSourceHashMode(
            BinaryPrimitives.ReadUInt32LittleEndian(span[128..]));
        ulong sourceHash = BinaryPrimitives.ReadUInt64LittleEndian(span[120..]);
        if (sourceHashMode == ModelBinarySourceHashMode.None && sourceHash != 0)
            throw Reject(CacheRejectReason.InvalidPreamble, "A source hash is present without a source hash mode.");

        return new ModelBinaryRawPreamble
        {
            Flags = flags,
            FileSize = declaredFileSize,
            HeaderChecksum = expectedHeaderChecksum,
            StringPoolOffset = BinaryPrimitives.ReadUInt64LittleEndian(
                span[ModelBinaryCacheFormat.StringPoolOffsetOffset..]),
            StringPoolLength = stringPoolLength,
            ChunkTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(
                span[ModelBinaryCacheFormat.ChunkTableOffsetOffset..]),
            ChunkTableLength = chunkTableLength,
            ChunkTableChecksum = BinaryPrimitives.ReadUInt64LittleEndian(
                span[ModelBinaryCacheFormat.ChunkTableChecksumOffset..]),
            StringPoolChecksum = BinaryPrimitives.ReadUInt64LittleEndian(
                span[ModelBinaryCacheFormat.StringPoolChecksumOffset..]),
            ChunkCount = chunkCount,
            EntrySourceLength = BinaryPrimitives.ReadUInt64LittleEndian(span[104..]),
            EntrySourceLastWriteUtcTicks = sourceTicks,
            EntrySourceHash = sourceHash,
            EntrySourceHashMode = sourceHashMode,
            AssetType = ParseAssetType(BinaryPrimitives.ReadUInt32LittleEndian(span[132..])),
            RequestedPolicyHash = new ModelBinaryHash128(span[136..152]),
            BackendResolutionHash = new ModelBinaryHash128(span[152..168]),
            ActualBackendKeyHash = new ModelBinaryHash128(span[168..184]),
            ActualBackendVersion = backendVersion,
            ActualBackendIdOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[188..]),
            VariantFingerprint = new ModelBinaryHash128(span[192..208]),
            ImportOptionsHash = new ModelBinaryHash128(span[208..224]),
            ModelCookSettingsHash = new ModelBinaryHash128(span[224..240]),
            DependencyManifestHash = new ModelBinaryHash128(
                span.Slice(ModelBinaryCacheFormat.DependencyManifestHashOffset, ModelBinaryHash128.Size)),
            DependencyCount = BinaryPrimitives.ReadUInt32LittleEndian(
                span[ModelBinaryCacheFormat.DependencyCountOffset..]),
            MaterialPolicyVersion = BinaryPrimitives.ReadUInt32LittleEndian(span[260..]),
            SourceIdentityOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                span[ModelBinaryCacheFormat.SourceIdentityOffset..]),
            EngineBuildIdentity = BinaryPrimitives.ReadUInt64LittleEndian(
                span[ModelBinaryCacheFormat.EngineBuildIdentityOffset..]),
        };
    }

    private static void ValidateMetadataRegions(ModelBinaryRawPreamble preamble)
    {
        ValidateAlignedRegion(
            preamble.StringPoolOffset,
            preamble.StringPoolLength,
            preamble.FileSize,
            "string pool",
            CacheRejectReason.InvalidStringPool);
        ValidateAlignedRegion(
            preamble.ChunkTableOffset,
            preamble.ChunkTableLength,
            preamble.FileSize,
            "chunk table",
            CacheRejectReason.InvalidChunkTable);

        if (RangesOverlap(
            0,
            ModelBinaryCacheFormat.PreambleSize,
            preamble.StringPoolOffset,
            preamble.StringPoolLength)
            || RangesOverlap(
                0,
                ModelBinaryCacheFormat.PreambleSize,
                preamble.ChunkTableOffset,
                preamble.ChunkTableLength))
            throw Reject(CacheRejectReason.InvalidChunkRange, "A metadata region overlaps the fixed preamble.");

        if (RangesOverlap(
            preamble.StringPoolOffset,
            preamble.StringPoolLength,
            preamble.ChunkTableOffset,
            preamble.ChunkTableLength))
            throw Reject(CacheRejectReason.OverlappingChunkRange, "The string pool and chunk table overlap.");
    }

    private static ModelBinaryChunkEntry[] ParseChunkTable(
        byte[] bytes,
        ModelBinaryRawPreamble preamble,
        ModelCacheReadLimits limits)
    {
        ModelBinaryChunkEntry[] entries = new ModelBinaryChunkEntry[checked((int)preamble.ChunkCount)];
        ulong aggregateDecodedBytes = 0;
        uint previousTypeId = 0;
        ulong previousInstanceId = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlySpan<byte> record = bytes.AsSpan(
                i * ModelBinaryCacheFormat.ChunkEntrySize,
                ModelBinaryCacheFormat.ChunkEntrySize);
            uint typeId = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            uint rawFlags = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            uint rawCodec = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            ulong instanceId = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
            ulong storedLength = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
            ulong decodedLength = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
            ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
            ulong elementCount = BinaryPrimitives.ReadUInt64LittleEndian(record[56..]);

            if (typeId == 0)
                throw Reject(CacheRejectReason.InvalidChunkTable, "Chunk type zero is reserved.");
            if ((rawFlags & ~(uint)ModelBinaryChunkFlags.Required) != 0)
                throw Reject(CacheRejectReason.InvalidChunkTable, "A chunk uses flags not defined by schema v1.");
            if (rawCodec != (uint)ModelBinaryChunkCodec.None)
                throw Reject(CacheRejectReason.UnsupportedChunkCodec, "Schema v1 supports only uncompressed chunks.");

            ModelBinaryChunkFlags flags = (ModelBinaryChunkFlags)rawFlags;
            bool known = ModelBinaryCacheFormat.IsKnownChunkType(typeId);
            if (!known && (flags & ModelBinaryChunkFlags.Required) != 0)
                throw Reject(CacheRejectReason.UnknownRequiredChunk, $"Required chunk type {typeId} is unknown.");
            if (known && version != ModelBinaryCacheFormat.GetChunkVersion(typeId))
                throw Reject(CacheRejectReason.ChunkVersionMismatch, $"Chunk type {typeId} has an unsupported version.");
            if (known && ModelBinaryCacheFormat.IsSingletonChunk(typeId) && instanceId != 0)
                throw Reject(CacheRejectReason.InvalidChunkTable, $"Singleton chunk type {typeId} has a nonzero instance ID.");
            if (storedLength != decodedLength)
                throw Reject(CacheRejectReason.UnsupportedChunkCodec, "Uncompressed chunk lengths must match.");
            if (storedLength > limits.MaxChunkBytes || decodedLength > limits.MaxChunkBytes)
                throw Limit($"Chunk {typeId}:{instanceId} exceeds the configured byte limit.");
            if (elementCount > GetElementLimit(typeId, limits))
                throw Limit($"Chunk {typeId}:{instanceId} exceeds its configured element-count limit.");
            if (known && storedLength == 0 && !ModelBinaryCacheFormat.AllowsEmptyChunk(typeId))
                throw Reject(CacheRejectReason.InvalidChunkRange, $"Chunk type {typeId} may not be empty.");
            if (offset % ModelBinaryCacheFormat.Alignment != 0)
                throw Reject(CacheRejectReason.InvalidChunkRange, $"Chunk {typeId}:{instanceId} is not aligned.");
            if (!RangeFits(offset, storedLength, preamble.FileSize))
                throw Reject(CacheRejectReason.InvalidChunkRange, $"Chunk {typeId}:{instanceId} extends beyond the file.");

            ValidateKnownRequiredFlag(typeId, flags);
            if (i > 0
                && (typeId < previousTypeId
                    || typeId == previousTypeId && instanceId <= previousInstanceId))
                throw Reject(
                    CacheRejectReason.InvalidChunkTable,
                    "Chunk entries are not strictly ordered by type and instance ID.");

            aggregateDecodedBytes = checked(aggregateDecodedBytes + decodedLength);
            if (aggregateDecodedBytes > limits.MaxAggregateDecodedBytes)
                throw Limit("The aggregate decoded-byte budget is exceeded.");

            entries[i] = new ModelBinaryChunkEntry(
                typeId,
                version,
                flags,
                ModelBinaryChunkCodec.None,
                instanceId,
                offset,
                storedLength,
                decodedLength,
                checksum,
                elementCount);
            previousTypeId = typeId;
            previousInstanceId = instanceId;
        }

        return entries;
    }

    private static void ValidateChunkRanges(
        IReadOnlyList<ModelBinaryChunkEntry> entries,
        ModelBinaryRawPreamble preamble)
    {
        ModelBinaryChunkEntry[] nonEmpty = entries
            .Where(static entry => entry.StoredLength != 0)
            .OrderBy(static entry => entry.Offset)
            .ToArray();
        ulong previousEnd = 0;

        for (int i = 0; i < nonEmpty.Length; i++)
        {
            ModelBinaryChunkEntry entry = nonEmpty[i];
            if (RangesOverlap(
                entry.Offset,
                entry.StoredLength,
                0,
                ModelBinaryCacheFormat.PreambleSize)
                || RangesOverlap(
                    entry.Offset,
                    entry.StoredLength,
                    preamble.StringPoolOffset,
                    preamble.StringPoolLength)
                || RangesOverlap(
                    entry.Offset,
                    entry.StoredLength,
                    preamble.ChunkTableOffset,
                    preamble.ChunkTableLength))
                throw Reject(
                    CacheRejectReason.OverlappingChunkRange,
                    $"Chunk {entry.TypeId}:{entry.InstanceId} overlaps a metadata region.");

            if (i > 0 && entry.Offset < previousEnd)
                throw Reject(CacheRejectReason.OverlappingChunkRange, "Two chunk body ranges overlap.");
            previousEnd = checked(entry.Offset + entry.StoredLength);
        }
    }

    private static void ValidateMandatoryChunks(IReadOnlyList<ModelBinaryChunkEntry> entries)
    {
        _ = GetRequiredEntry(entries, ModelBinaryChunkType.Dependencies);
        _ = GetRequiredEntry(entries, ModelBinaryChunkType.Manifest);
        _ = GetRequiredEntry(entries, ModelBinaryChunkType.PrefabGraph);
        _ = GetRequiredEntry(entries, ModelBinaryChunkType.ImportedEntityTable);
    }

    private static ModelBinaryCachePreamble ResolvePreamble(
        ModelBinaryRawPreamble raw,
        ModelBinaryStringPool stringPool)
    {
        string actualBackendId = stringPool.GetRequired(raw.ActualBackendIdOffset, "actualBackendName");
        string sourceIdentity = stringPool.GetRequired(raw.SourceIdentityOffset, "sourceIdentity");
        if (!ModelBinaryHash128.HashUtf8(actualBackendId).Equals(raw.ActualBackendKeyHash))
            throw Reject(CacheRejectReason.ImporterBackendMismatch, "The full producer ID does not match its fixed hash.");

        return new ModelBinaryCachePreamble
        {
            Flags = raw.Flags,
            FileSize = raw.FileSize,
            HeaderChecksum = raw.HeaderChecksum,
            StringPoolOffset = raw.StringPoolOffset,
            StringPoolLength = raw.StringPoolLength,
            ChunkTableOffset = raw.ChunkTableOffset,
            ChunkTableLength = raw.ChunkTableLength,
            ChunkTableChecksum = raw.ChunkTableChecksum,
            StringPoolChecksum = raw.StringPoolChecksum,
            ChunkCount = raw.ChunkCount,
            EntrySourceLength = raw.EntrySourceLength,
            EntrySourceLastWriteUtcTicks = raw.EntrySourceLastWriteUtcTicks,
            EntrySourceHash = raw.EntrySourceHash,
            EntrySourceHashMode = raw.EntrySourceHashMode,
            AssetType = raw.AssetType,
            RequestedPolicyHash = raw.RequestedPolicyHash,
            BackendResolutionHash = raw.BackendResolutionHash,
            ActualBackendKeyHash = raw.ActualBackendKeyHash,
            ActualBackendVersion = raw.ActualBackendVersion,
            ActualBackendId = actualBackendId,
            VariantFingerprint = raw.VariantFingerprint,
            ImportOptionsHash = raw.ImportOptionsHash,
            ModelCookSettingsHash = raw.ModelCookSettingsHash,
            DependencyManifestHash = raw.DependencyManifestHash,
            DependencyCount = raw.DependencyCount,
            MaterialPolicyVersion = raw.MaterialPolicyVersion,
            SourceIdentity = sourceIdentity,
            EngineBuildIdentity = raw.EngineBuildIdentity,
        };
    }

    private static ModelImportDependency[] ParseDependencies(
        byte[] bytes,
        ModelBinaryChunkEntry entry,
        uint preambleDependencyCount,
        ModelBinaryStringPool stringPool,
        ModelCacheReadLimits limits)
    {
        if (bytes.Length < ModelBinaryCacheFormat.DependencyHeaderSize)
            throw Reject(CacheRejectReason.Truncated, "The Dependencies chunk is shorter than its header.");

        ReadOnlySpan<byte> span = bytes;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span);
        uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (version != ModelBinaryChunkVersions.Dependencies)
            throw Reject(CacheRejectReason.ChunkVersionMismatch, "The dependency-manifest payload version is unsupported.");
        if (recordSize != ModelBinaryCacheFormat.DependencyRecordSize || reserved != 0)
            throw Reject(CacheRejectReason.InvalidChunkTable, "The dependency-manifest record layout is invalid.");
        if (count != preambleDependencyCount || entry.ElementCount != count)
            throw Reject(CacheRejectReason.InvalidPreamble, "Dependency counts disagree across the preamble, table, and chunk.");
        if (count > limits.MaxElementCount || count > int.MaxValue)
            throw Limit("The dependency count exceeds the configured limit.");

        ulong expectedLength = checked(
            (ulong)ModelBinaryCacheFormat.DependencyHeaderSize
            + (ulong)count * ModelBinaryCacheFormat.DependencyRecordSize);
        if (expectedLength != (ulong)bytes.Length)
            throw Reject(CacheRejectReason.InvalidChunkRange, "The Dependencies chunk length does not match its record count.");

        ModelImportDependency[] dependencies = new ModelImportDependency[checked((int)count)];
        string? previousPath = null;
        uint previousKindId = 0;
        string? previousProducerKey = null;

        int offset = ModelBinaryCacheFormat.DependencyHeaderSize;
        for (int i = 0; i < dependencies.Length; i++)
        {
            ReadOnlySpan<byte> record = span.Slice(offset, ModelBinaryCacheFormat.DependencyRecordSize);
            string path = stringPool.GetRequired(
                BinaryPrimitives.ReadUInt32LittleEndian(record),
                $"dependency[{i}].path");
            string? producerKey = stringPool.GetOptional(
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                $"dependency[{i}].producerKey");
            string? contentHash = stringPool.GetOptional(
                BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
                $"dependency[{i}].contentHash");
            uint kindId = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            uint hashModeId = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(record[32..]);

            if ((flags & ~ModelBinaryCacheFormat.DependencyRequiredFlag) != 0)
                throw Reject(CacheRejectReason.InvalidChunkTable, "A dependency record contains unknown flags.");
            if (length > long.MaxValue || ticks < 0)
                throw Limit("A dependency freshness value cannot be represented by the runtime.");

            ModelImportDependencyKind kind = ParseDependencyKind(kindId);
            ModelImportDependencyHashMode hashMode = ParseDependencyHashMode(hashModeId);
            if ((contentHash is null) != (hashMode == ModelImportDependencyHashMode.None))
                throw Reject(CacheRejectReason.InvalidChunkTable, "A dependency content hash and its mode disagree.");
            if (contentHash is not null && !contentHash.Equals(contentHash.ToLowerInvariant(), StringComparison.Ordinal))
                throw Reject(CacheRejectReason.InvalidChunkTable, "Dependency content hashes must use lowercase canonical text.");

            if (previousPath is not null
                && CompareDependencyKeys(
                    previousPath,
                    previousKindId,
                    previousProducerKey,
                    path,
                    kindId,
                    producerKey) >= 0)
                throw Reject(CacheRejectReason.InvalidChunkTable, "Dependency records are not strictly canonical and unique.");

            dependencies[i] = new ModelImportDependency(
                path,
                kind,
                (flags & ModelBinaryCacheFormat.DependencyRequiredFlag) != 0,
                checked((long)length),
                ticks,
                contentHash,
                producerKey,
                hashMode);
            previousPath = path;
            previousKindId = kindId;
            previousProducerKey = producerKey;
            offset += ModelBinaryCacheFormat.DependencyRecordSize;
        }

        return dependencies;
    }

    private static ModelBinaryManifest ParseManifest(
        byte[] bytes,
        ModelBinaryRawPreamble preamble,
        ModelBinaryStringPool stringPool,
        ModelCacheReadLimits limits)
    {
        if (bytes.Length < ModelBinaryCacheFormat.ManifestHeaderSize)
            throw Reject(CacheRejectReason.Truncated, "The Manifest chunk is shorter than its header.");

        ReadOnlySpan<byte> span = bytes;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != ModelBinaryCacheFormat.ManifestFormatVersion
            || headerSize != ModelBinaryCacheFormat.ManifestHeaderSize)
            throw Reject(CacheRejectReason.ChunkVersionMismatch, "The Manifest payload layout is unsupported.");
        if (!span[52..56].SequenceEqual(stackalloc byte[4])
            || !span[136..144].SequenceEqual(stackalloc byte[8]))
            throw Reject(CacheRejectReason.InvalidChunkTable, "The Manifest reserved bytes must be zero.");

        uint candidateCount = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
        uint dependencyCount = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
        uint sourceEntityCount = BinaryPrimitives.ReadUInt32LittleEndian(span[44..]);
        uint referenceCount = BinaryPrimitives.ReadUInt32LittleEndian(span[48..]);
        if (candidateCount == 0 || candidateCount > limits.MaxChunkCount)
            throw Limit("The resolver candidate count exceeds the configured limit.");
        if (dependencyCount != preamble.DependencyCount)
            throw Reject(CacheRejectReason.InvalidPreamble, "Manifest and preamble dependency counts differ.");
        if (sourceEntityCount > limits.MaxElementCount || referenceCount > limits.MaxElementCount)
            throw Limit("A Manifest entity/reference count exceeds the configured limit.");

        ulong expectedLength = checked(
            (ulong)ModelBinaryCacheFormat.ManifestHeaderSize
            + (ulong)candidateCount * ModelBinaryCacheFormat.ManifestCandidateRecordSize);
        if (expectedLength != (ulong)bytes.Length)
            throw Reject(CacheRejectReason.InvalidChunkRange, "The Manifest length does not match its candidate count.");

        ulong nodeCount = ReadAndValidateCount(span, 56, limits.MaxNodeCount, "node");
        ulong modelCount = ReadAndValidateCount(span, 64, limits.MaxModelCount, "model");
        ulong subMeshCount = ReadAndValidateCount(span, 72, limits.MaxSubMeshCount, "submesh");
        ulong meshCount = ReadAndValidateCount(span, 80, limits.MaxMeshCount, "mesh");
        ulong vertexCount = ReadAndValidateCount(span, 88, limits.MaxVertexCount, "vertex");
        ulong indexCount = ReadAndValidateCount(span, 96, limits.MaxIndexCount, "index");
        ulong boneCount = ReadAndValidateCount(span, 104, limits.MaxBoneCount, "bone");
        ulong morphTargetCount = ReadAndValidateCount(span, 112, limits.MaxMorphTargetCount, "morph-target");
        ulong lodCount = ReadAndValidateCount(span, 120, limits.MaxLodCount, "LOD");
        ulong meshletCount = ReadAndValidateCount(span, 128, limits.MaxMeshletCount, "meshlet");

        string producerId = stringPool.GetRequired(
            BinaryPrimitives.ReadUInt32LittleEndian(span[16..]),
            "manifest.actualProducerId");
        string sourceExtension = stringPool.GetOptional(
            BinaryPrimitives.ReadUInt32LittleEndian(span[20..]),
            "manifest.sourceExtension") ?? string.Empty;
        if (!producerId.Equals(
            stringPool.GetRequired(preamble.ActualBackendIdOffset, "actualBackendName"),
            StringComparison.Ordinal))
            throw Reject(CacheRejectReason.ImporterBackendMismatch, "The Manifest and preamble name different producers.");

        ModelBinaryManifestCandidate[] candidates = new ModelBinaryManifestCandidate[checked((int)candidateCount)];
        HashSet<string> candidateIds = new(StringComparer.Ordinal);
        int offset = ModelBinaryCacheFormat.ManifestHeaderSize;
        bool foundActualProducer = false;
        for (int i = 0; i < candidates.Length; i++)
        {
            ReadOnlySpan<byte> record = span.Slice(offset, ModelBinaryCacheFormat.ManifestCandidateRecordSize);
            string stableId = stringPool.GetRequired(
                BinaryPrimitives.ReadUInt32LittleEndian(record),
                $"manifest.candidate[{i}].stableId");
            uint implementationVersion = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            ModelImportBackendCapabilities capabilities = ParseBackendCapabilities(
                BinaryPrimitives.ReadUInt32LittleEndian(record[8..]));
            if (BinaryPrimitives.ReadUInt32LittleEndian(record[12..]) != 0)
                throw Reject(CacheRejectReason.InvalidChunkTable, "A Manifest candidate reserved field is nonzero.");
            if (implementationVersion == 0 || !candidateIds.Add(stableId))
                throw Reject(CacheRejectReason.InvalidChunkTable, "Manifest candidate identities must be nonzero and unique.");

            candidates[i] = new ModelBinaryManifestCandidate(stableId, implementationVersion, capabilities);
            if (stableId.Equals(producerId, StringComparison.Ordinal))
            {
                if (implementationVersion != preamble.ActualBackendVersion)
                    throw Reject(CacheRejectReason.ImporterBackendVersionMismatch, "The actual producer version differs.");
                foundActualProducer = true;
            }
            offset += ModelBinaryCacheFormat.ManifestCandidateRecordSize;
        }

        if (!foundActualProducer)
            throw Reject(CacheRejectReason.ImporterBackendMismatch, "The actual producer is absent from the candidate list.");

        return new ModelBinaryManifest(
            BinaryPrimitives.ReadUInt64LittleEndian(span[8..]),
            producerId,
            sourceExtension,
            BinaryPrimitives.ReadUInt32LittleEndian(span[24..]),
            ParseBackendPolicy(BinaryPrimitives.ReadUInt32LittleEndian(span[28..])),
            ParseBackendPolicy(BinaryPrimitives.ReadUInt32LittleEndian(span[32..])),
            candidates,
            sourceEntityCount,
            referenceCount,
            nodeCount,
            modelCount,
            subMeshCount,
            meshCount,
            vertexCount,
            indexCount,
            boneCount,
            morphTargetCount,
            lodCount,
            meshletCount);
    }

    private static byte[] ReadAndValidateChunk(Stream source, ModelBinaryChunkEntry entry)
    {
        byte[] bytes = ReadRegion(
            source,
            entry.Offset,
            entry.StoredLength,
            ModelCacheReadLimits.HardMaxChunkBytes,
            $"chunk {entry.TypeId}:{entry.InstanceId}");
        if (XxHash3.HashToUInt64(bytes) != entry.DecodedChecksum)
            throw Reject(
                CacheRejectReason.ChunkChecksumMismatch,
                $"Chunk {entry.TypeId}:{entry.InstanceId} failed checksum validation.");
        return bytes;
    }

    private static void ValidateDependencyManifestChecksum(
        ReadOnlySpan<byte> dependencyBytes,
        ModelBinaryHash128 expected)
    {
        Span<byte> actualBytes = stackalloc byte[ModelBinaryHash128.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(actualBytes, XxHash3.HashToUInt64(dependencyBytes));
        ModelBinaryHash128 actual = new(actualBytes);
        if (!actual.Equals(expected))
            throw Reject(
                CacheRejectReason.DependencyManifestChecksumMismatch,
                "The fixed dependency-manifest checksum does not match.");
    }

    private static ModelBinaryChunkEntry GetRequiredEntry(
        IReadOnlyList<ModelBinaryChunkEntry> entries,
        ModelBinaryChunkType type)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            ModelBinaryChunkEntry entry = entries[i];
            if (entry.TypeId != (uint)type || entry.InstanceId != 0)
                continue;
            if (!entry.IsRequired)
                throw Reject(CacheRejectReason.RequiredChunkMissing, $"{type} is not marked required.");
            return entry;
        }

        throw Reject(CacheRejectReason.RequiredChunkMissing, $"The required {type} chunk is absent.");
    }

    private static byte[] ReadRegion(
        Stream source,
        ulong offset,
        ulong length,
        ulong maximumLength,
        string regionName)
    {
        if (length > maximumLength || length > int.MaxValue)
            throw Limit($"The {regionName} region exceeds its bounded allocation.");

        byte[] bytes = new byte[checked((int)length)];
        source.Position = checked((long)offset);
        source.ReadExactly(bytes);
        return bytes;
    }

    private static void ValidateAlignedRegion(
        ulong offset,
        ulong length,
        ulong fileSize,
        string name,
        CacheRejectReason reason)
    {
        if (offset % ModelBinaryCacheFormat.Alignment != 0)
            throw Reject(reason, $"The {name} offset is not aligned.");
        if (!RangeFits(offset, length, fileSize))
            throw Reject(reason, $"The {name} range extends beyond the file.");
    }

    private static bool RangeFits(ulong offset, ulong length, ulong fileSize)
        => offset <= fileSize && length <= fileSize - offset;

    private static bool RangesOverlap(
        ulong firstOffset,
        ulong firstLength,
        ulong secondOffset,
        ulong secondLength)
    {
        if (firstLength == 0 || secondLength == 0)
            return false;

        ulong firstEnd = checked(firstOffset + firstLength);
        ulong secondEnd = checked(secondOffset + secondLength);
        return firstOffset < secondEnd && secondOffset < firstEnd;
    }

    private static void ValidateKnownRequiredFlag(uint typeId, ModelBinaryChunkFlags flags)
    {
        if (!ModelBinaryCacheFormat.IsKnownChunkType(typeId))
            return;

        ModelBinaryChunkType type = (ModelBinaryChunkType)typeId;
        bool required = (flags & ModelBinaryChunkFlags.Required) != 0;
        if (type is ModelBinaryChunkType.Dependencies
            or ModelBinaryChunkType.Manifest
            or ModelBinaryChunkType.PrefabGraph
            or ModelBinaryChunkType.ImportedEntityTable)
        {
            if (!required)
                throw Reject(CacheRejectReason.RequiredChunkMissing, $"{type} must be marked required.");
        }
        else if (type is ModelBinaryChunkType.ColliderHints or ModelBinaryChunkType.Diagnostics)
        {
            if (required)
                throw Reject(CacheRejectReason.InvalidChunkTable, $"{type} may not be marked required in schema v1.");
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

    private static ulong ReadAndValidateCount(
        ReadOnlySpan<byte> span,
        int offset,
        ulong limit,
        string name)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(span[offset..]);
        if (value > limit)
            throw Limit($"The Manifest {name} count exceeds the configured limit.");
        return value;
    }

    private static int CompareDependencyKeys(
        string firstPath,
        uint firstKind,
        string? firstProducerKey,
        string secondPath,
        uint secondKind,
        string? secondProducerKey)
    {
        int comparison = StringComparer.Ordinal.Compare(firstPath, secondPath);
        if (comparison != 0)
            return comparison;
        comparison = firstKind.CompareTo(secondKind);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(firstProducerKey, secondProducerKey);
    }

    private static ModelImportDependencyKind ParseDependencyKind(uint value)
        => value switch
        {
            1 => ModelImportDependencyKind.EntrySource,
            2 => ModelImportDependencyKind.Structural,
            3 => ModelImportDependencyKind.ReferencedTexture,
            4 => ModelImportDependencyKind.ReferencedAnimation,
            5 => ModelImportDependencyKind.ReferencedAsset,
            _ => throw Reject(CacheRejectReason.InvalidChunkTable, $"Dependency kind {value} is unsupported."),
        };

    private static ModelImportDependencyHashMode ParseDependencyHashMode(uint value)
        => value switch
        {
            0 => ModelImportDependencyHashMode.None,
            1 => ModelImportDependencyHashMode.Sha256,
            2 => ModelImportDependencyHashMode.XxHash3_64,
            3 => ModelImportDependencyHashMode.ProducerDefined,
            _ => throw Reject(CacheRejectReason.InvalidChunkTable, $"Dependency hash mode {value} is unsupported."),
        };

    private static ModelImportBackendPolicy ParseBackendPolicy(uint value)
        => value switch
        {
            1 => ModelImportBackendPolicy.Auto,
            2 => ModelImportBackendPolicy.Native,
            3 => ModelImportBackendPolicy.Assimp,
            _ => throw Reject(CacheRejectReason.InvalidChunkTable, $"Backend policy {value} is unsupported."),
        };

    private static ModelImportBackendCapabilities ParseBackendCapabilities(uint value)
    {
        if ((value & ~0xFu) != 0)
            throw Reject(CacheRejectReason.InvalidChunkTable, "A Manifest candidate contains unknown capability bits.");

        ModelImportBackendCapabilities capabilities = ModelImportBackendCapabilities.None;
        if ((value & (1u << 0)) != 0)
            capabilities |= ModelImportBackendCapabilities.NativeParser;
        if ((value & (1u << 1)) != 0)
            capabilities |= ModelImportBackendCapabilities.GeneralPurposeFallback;
        if ((value & (1u << 2)) != 0)
            capabilities |= ModelImportBackendCapabilities.StableSourceEntityIds;
        if ((value & (1u << 3)) != 0)
            capabilities |= ModelImportBackendCapabilities.StructuralDependencyDiscovery;
        return capabilities;
    }

    private static ModelBinarySourceHashMode ParseSourceHashMode(uint value)
        => value switch
        {
            0 => ModelBinarySourceHashMode.None,
            1 => ModelBinarySourceHashMode.XxHash3_64,
            _ => throw Reject(CacheRejectReason.InvalidPreamble, $"Source hash mode {value} is unsupported."),
        };

    private static ModelBinaryAssetType ParseAssetType(uint value)
        => value switch
        {
            1 => ModelBinaryAssetType.PrefabSource,
            _ => throw Reject(CacheRejectReason.AssetTypeMismatch, $"Asset type {value} is unsupported."),
        };

    private static ModelBinaryCacheFormatException Reject(CacheRejectReason reason, string message)
        => new(reason, message);

    private static ModelBinaryCacheFormatException Limit(string message)
        => Reject(CacheRejectReason.ResourceLimitExceeded, message);
}
