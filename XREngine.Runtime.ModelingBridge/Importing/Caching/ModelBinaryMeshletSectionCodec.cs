using System.Text;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Canonical payload codec for the optional model-container meshlet section.
/// The section deliberately stores a payload per stable model/submesh/LOD key;
/// it never derives geometry, invokes a parser, or asks the mesh optimizer to
/// rebuild data while loading a warm model-cache hit.
/// </summary>
internal static class ModelBinaryMeshletSectionCodec
{
    private const uint FormatVersion = 1;
    private const int HeaderBytes = sizeof(uint) * 2;
    private const int MaxIdentityUtf8Bytes = 16 * 1024;

    /// <summary>
    /// Collects one explicit payload reference for every renderable model LOD.
    /// A missing payload is a cold-publication error rather than an inferred
    /// request to build during warm hydration.
    /// </summary>
    public static IReadOnlyList<ModelBinaryMeshletSourceReference> CollectReferences(string modelIdentity, Model model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentNullException.ThrowIfNull(model);
        List<ModelBinaryMeshletSourceReference> references = [];
        for (int subMeshIndex = 0; subMeshIndex < model.Meshes.Count; subMeshIndex++)
        {
            SubMesh subMesh = model.Meshes[subMeshIndex];
            int lodIndex = 0;
            foreach (SubMeshLOD lod in subMesh.LODs)
            {
                if (lod.Mesh is null)
                {
                    lodIndex++;
                    continue;
                }

                MeshletPayload payload = lod.Mesh.MeshletPayload
                    ?? throw new InvalidDataException(
                        $"Renderable model '{modelIdentity}' submesh {subMeshIndex} LOD {lodIndex} has no explicit meshlet payload state.");
                references.Add(new ModelBinaryMeshletSourceReference(
                    modelIdentity,
                    checked((uint)subMeshIndex),
                    checked((uint)lodIndex),
                    payload));
                lodIndex++;
            }
        }

        return references;
    }

    /// <summary>Creates the canonical optional Meshlets container chunk.</summary>
    public static ModelBinaryChunk CreateChunk(
        IEnumerable<ModelBinaryMeshletSectionEntry> entries,
        ModelCacheReadLimits? limits = null)
    {
        ModelBinaryMeshletSectionEntry[] materialized = entries?.ToArray()
            ?? throw new ArgumentNullException(nameof(entries));
        byte[] bytes = Serialize(materialized, limits);
        return new ModelBinaryChunk(
            ModelBinaryChunkType.Meshlets,
            ModelBinaryChunkFlags.None,
            instanceId: 0,
            bytes,
            elementCount: checked((ulong)materialized.Length));
    }

    public static ModelBinaryChunk CreateChunk(IEnumerable<ModelBinaryMeshletSourceReference> references, ModelCacheReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        return CreateChunk(references.Select(static reference => new ModelBinaryMeshletSectionEntry(
            new(reference.ModelIdentity, reference.SubMeshIndex, reference.LodIndex), reference.Payload)), limits);
    }

    public static byte[] Serialize(IEnumerable<ModelBinaryMeshletSectionEntry> entries, ModelCacheReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        limits ??= ModelCacheReadLimits.Default;

        ModelBinaryMeshletSectionEntry[] ordered = entries
            .OrderBy(static entry => entry.Key.ModelIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Key.SubMeshIndex)
            .ThenBy(static entry => entry.Key.LodIndex)
            .ToArray();
        if ((ulong)ordered.Length > limits.MaxElementCount || (ulong)ordered.Length > limits.MaxMeshletCount)
            throw new ArgumentException("The meshlet section entry count exceeds configured limits.", nameof(entries));

        ulong aggregateMeshlets = 0;

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(FormatVersion);
        writer.Write(ordered.Length);

        ModelBinaryMeshletSectionKey? previousKey = null;
        foreach (ModelBinaryMeshletSectionEntry entry in ordered)
        {
            ValidateEntry(entry, limits);
            aggregateMeshlets = checked(aggregateMeshlets + (ulong)entry.Payload.Meshlets.Length);
            if (aggregateMeshlets > limits.MaxMeshletCount)
                throw new ArgumentException("The meshlet section aggregate meshlet count exceeds configured limits.", nameof(entries));
            if (previousKey is { } previous && previous.CompareTo(entry.Key) == 0)
                throw new ArgumentException("The meshlet section contains duplicate stable model/submesh/LOD keys.", nameof(entries));

            WriteString(writer, entry.Key.ModelIdentity);
            writer.Write(entry.Key.SubMeshIndex);
            writer.Write(entry.Key.LodIndex);
            byte[] payloadBytes = XRMesh.SerializeMeshletPayloadToBytes(entry.Payload);
            if ((ulong)payloadBytes.Length > limits.MaxChunkBytes)
                throw new ArgumentException("A meshlet payload exceeds the configured chunk limit.", nameof(entries));
            writer.Write(payloadBytes.Length);
            writer.Write(payloadBytes);
            previousKey = entry.Key;
        }

        if ((ulong)stream.Length > limits.MaxChunkBytes)
            throw new ArgumentException("The meshlet section exceeds the configured chunk limit.", nameof(entries));
        return stream.ToArray();
    }

    public static IReadOnlyList<ModelBinaryMeshletSectionEntry> Deserialize(
        ReadOnlySpan<byte> bytes,
        ulong declaredElementCount,
        ModelCacheReadLimits? limits = null)
    {
        limits ??= ModelCacheReadLimits.Default;
        if ((ulong)bytes.Length > limits.MaxChunkBytes || bytes.Length < HeaderBytes)
            throw new InvalidDataException("The meshlet section is truncated or exceeds configured limits.");

        using MemoryStream stream = new(bytes.ToArray(), writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        uint version = reader.ReadUInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported model meshlet section version {version}.");

        int count = reader.ReadInt32();
        if (count < 0 || (ulong)count > limits.MaxElementCount || (ulong)count > limits.MaxMeshletCount
            || (declaredElementCount != 0 && (ulong)count != declaredElementCount))
        {
            throw new InvalidDataException("The meshlet section entry count is invalid.");
        }

        List<ModelBinaryMeshletSectionEntry> entries = new(count);
        ulong aggregateMeshlets = 0;
        ModelBinaryMeshletSectionKey? previousKey = null;
        for (int index = 0; index < count; index++)
        {
            string modelIdentity = ReadString(reader);
            uint subMeshIndex = reader.ReadUInt32();
            uint lodIndex = reader.ReadUInt32();
            int payloadLength = reader.ReadInt32();
            if (payloadLength <= 0 || (ulong)payloadLength > limits.MaxChunkBytes || payloadLength > stream.Length - stream.Position)
                throw new InvalidDataException($"Meshlet section payload {index} has an invalid byte range.");

            byte[] payloadBytes = reader.ReadBytes(payloadLength);
            if (payloadBytes.Length != payloadLength)
                throw new EndOfStreamException("The meshlet section payload is truncated.");

            MeshletPayload? payload = XRMesh.DeserializeMeshletPayloadFromBytes(payloadBytes);
            if (payload is null)
                throw new InvalidDataException("A model meshlet entry must explicitly encode a payload state.");

            ModelBinaryMeshletSectionEntry entry = new(new(modelIdentity, subMeshIndex, lodIndex), payload);
            ValidateEntry(entry, limits);
            aggregateMeshlets = checked(aggregateMeshlets + (ulong)payload.Meshlets.Length);
            if (aggregateMeshlets > limits.MaxMeshletCount)
                throw new InvalidDataException("The meshlet section aggregate meshlet count exceeds configured limits.");
            if (previousKey is { } previous && previous.CompareTo(entry.Key) >= 0)
                throw new InvalidDataException("Meshlet section keys must be unique and canonical-order sorted.");

            entries.Add(entry);
            previousKey = entry.Key;
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("The meshlet section has trailing bytes.");
        return entries;
    }

    /// <summary>
    /// Attaches validated warm-cache payloads before the owning meshes become
    /// visible to GPUScene. Unmatched keys are intentionally reported to the
    /// caller instead of falling back to a runtime build.
    /// </summary>
    public static ModelBinaryMeshletHydrationResult Hydrate(
        IEnumerable<ModelBinaryMeshletSectionEntry> entries,
        Func<ModelBinaryMeshletSectionKey, XRMesh?> resolveMesh)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(resolveMesh);

        int hydrated = 0;
        List<ModelBinaryMeshletSectionKey> unmatched = [];
        foreach (ModelBinaryMeshletSectionEntry entry in entries)
        {
            XRMesh? mesh = resolveMesh(entry.Key);
            if (mesh is null)
            {
                unmatched.Add(entry.Key);
                continue;
            }

            // Disabled and Empty are valid terminal cook results. Repairable
            // states remain attached so the import/cache policy can decide
            // whether a later offline repair is permitted.
            mesh.AttachValidatedCookedMeshletPayload(entry.Payload);
            hydrated++;
        }

        return new ModelBinaryMeshletHydrationResult(hydrated, unmatched);
    }

    private static void ValidateEntry(ModelBinaryMeshletSectionEntry entry, ModelCacheReadLimits limits)
    {
        if (string.IsNullOrWhiteSpace(entry.Key.ModelIdentity))
            throw new InvalidDataException("A meshlet section key requires a stable model identity.");
        if (Encoding.UTF8.GetByteCount(entry.Key.ModelIdentity) > MaxIdentityUtf8Bytes)
            throw new InvalidDataException("A meshlet section model identity exceeds its bounded length.");

        MeshletPayload payload = entry.Payload ?? throw new InvalidDataException("A meshlet section entry has no payload.");
        if (!payload.SourceMeshIdentity.Equals(entry.Key.ModelIdentity, StringComparison.Ordinal)
            && !payload.SourceMeshIdentity.StartsWith(entry.Key.ModelIdentity + "/", StringComparison.Ordinal))
            throw new InvalidDataException("A meshlet section payload identity is outside its model owner key.");
        if (!Enum.IsDefined(payload.State))
            throw new InvalidDataException("A meshlet section entry has an unknown generated-data state.");
        if ((ulong)payload.Meshlets.Length > limits.MaxMeshletCount
            || (ulong)payload.VertexIndices.Length > limits.MaxVertexCount
            || (ulong)payload.TriangleIndices.Length > limits.MaxIndexCount)
        {
            throw new InvalidDataException("A meshlet section payload exceeds configured stream limits.");
        }

        payload.ValidatePortablePayload();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxIdentityUtf8Bytes)
            throw new InvalidDataException("A meshlet section model identity exceeds its bounded length.");
        writer.Write(byteCount);
        writer.Write(Encoding.UTF8.GetBytes(value));
    }

    private static string ReadString(BinaryReader reader)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount <= 0 || byteCount > MaxIdentityUtf8Bytes)
            throw new InvalidDataException("A meshlet section model identity has an invalid byte length.");
        byte[] bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
            throw new EndOfStreamException("The meshlet section model identity is truncated.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
