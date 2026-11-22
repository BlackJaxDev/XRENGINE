using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Persists the geometry pages of the cluster hierarchy meshes of one imported model, for geometry
/// streaming. The file sits next to the model's meshlet payload file
/// (<see cref="ModelImportMeshletPayloadCache"/>), with the same name and the
/// <c>.xrpages</c> extension.
/// </summary>
/// <remarks>
/// <para>
/// The file starts with a header and an index. The index holds one entry for each paged mesh: its
/// source mesh identity, the freshness hash of the payload that it was cooked from, a hash of the
/// packed vertex data, the resident tables (<see cref="MeshletPageTable"/>) and one
/// <see cref="MeshletPageFileEntry"/> for each page. The page blobs follow the index, each at a
/// <see cref="MeshletPageFormat.FileAlignment"/>-byte offset, so a reader can read one page with
/// one direct read.
/// </para>
/// <para>
/// An entry is used only when its payload freshness hash and vertex data hash match the mesh that
/// asks for it. The vertex data hash covers every packed attribute of a static mesh, so an edit
/// that changes only normals, UVs or colors also makes the entry stale. A stale or missing entry
/// costs one page cook, never stale data.
/// </para>
/// <para>
/// The file is written only when an import cooked pages or the file held entries that the import
/// did not use. It is written to a temporary file and moved into place, so a reader never sees a
/// partial file.
/// </para>
/// </remarks>
internal sealed class ModelImportMeshletPageCache
{
    /// <summary>The extension of a page file.</summary>
    public const string FileExtension = ".xrpages";

    /// <summary>The first word of a page file: "XRPF" in little-endian byte order.</summary>
    private const uint FileMagic = 0x46505258u;

    /// <summary>The version of the file layout (header, index and blob placement).</summary>
    private const uint FileVersion = 1u;

    private const int HeaderBytes = 32;

    private readonly Dictionary<string, StoredEntry> _stored;
    private readonly List<PendingEntry> _pending = [];
    private readonly HashSet<string> _pendingIdentities = new(StringComparer.Ordinal);

    private ModelImportMeshletPageCache(string filePath, Dictionary<string, StoredEntry> stored, string? loadFailure)
    {
        FilePath = filePath;
        _stored = stored;
        LoadFailure = loadFailure;
    }

    /// <summary>Gets the page file of this model.</summary>
    public string FilePath { get; }

    /// <summary>Gets why an existing file could not be read, or null.</summary>
    public string? LoadFailure { get; }

    /// <summary>Gets the number of meshes that the file held.</summary>
    public int StoredCount => _stored.Count;

    /// <summary>Gets the meshes that used stored pages.</summary>
    public int HitCount { get; private set; }

    /// <summary>Gets the meshes whose pages were cooked.</summary>
    public int CookCount { get; private set; }

    /// <summary>
    /// Opens the page file next to a meshlet payload file. Reads the header and the index; the page
    /// blobs are read on demand.
    /// </summary>
    /// <param name="payloadCacheFilePath">The path of the model's <c>.xrmeshlets</c> file.</param>
    public static ModelImportMeshletPageCache Open(string payloadCacheFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadCacheFilePath);
        string filePath = Path.ChangeExtension(payloadCacheFilePath, FileExtension);
        Dictionary<string, StoredEntry> stored = new(StringComparer.Ordinal);
        string? failure = null;
        if (File.Exists(filePath))
        {
            try
            {
                ReadIndex(filePath, stored);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or OverflowException)
            {
                stored.Clear();
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        return new ModelImportMeshletPageCache(filePath, stored, failure);
    }

    /// <summary>
    /// Returns the stored tables of a mesh when they were cooked from the same payload and vertex
    /// data, and records the mesh for the next save.
    /// </summary>
    /// <param name="sourceMeshIdentity">The source mesh identity of the payload.</param>
    /// <param name="payloadFreshnessHash">The freshness hash of the payload.</param>
    /// <param name="vertexDataHash">The hash of the packed vertex data (zero for a skinned mesh).</param>
    /// <param name="table">The stored tables, when the method returns true.</param>
    public bool TryUse(string sourceMeshIdentity, ulong payloadFreshnessHash, ulong vertexDataHash, out MeshletPageTable? table)
    {
        table = null;
        if (!_stored.TryGetValue(sourceMeshIdentity, out StoredEntry? entry) ||
            entry.PayloadFreshnessHash != payloadFreshnessHash ||
            entry.VertexDataHash != vertexDataHash)
            return false;

        table = entry.Table;
        if (_pendingIdentities.Add(sourceMeshIdentity))
            _pending.Add(new PendingEntry(sourceMeshIdentity, payloadFreshnessHash, vertexDataHash, entry.Table, null, entry));
        ++HitCount;
        return true;
    }

    /// <summary>Records a cooked page set for the next save.</summary>
    public void Record(string sourceMeshIdentity, ulong payloadFreshnessHash, ulong vertexDataHash, MeshletPageSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!_pendingIdentities.Add(sourceMeshIdentity))
            return;
        _pending.Add(new PendingEntry(sourceMeshIdentity, payloadFreshnessHash, vertexDataHash, set.Table, set.PageImages, null));
        ++CookCount;
    }

    /// <summary>
    /// Reads and decodes one page of a stored mesh into <paramref name="destination"/>, which must
    /// hold at least <see cref="MeshletPageFormat.PageBytes"/> bytes.
    /// </summary>
    /// <returns>The bytes of the decoded page image.</returns>
    /// <exception cref="KeyNotFoundException">The file holds no entry for the mesh.</exception>
    /// <exception cref="InvalidDataException">The blob is damaged.</exception>
    public int ReadPage(string sourceMeshIdentity, int page, Span<byte> destination)
    {
        StoredEntry entry = _stored[sourceMeshIdentity];
        MeshletPageFileEntry fileEntry = entry.Files[page];
        byte[] stored = new byte[fileEntry.StoredBytes];
        using (SafeFileHandle handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            ReadExactly(handle, stored, fileEntry.FileOffset);
        MeshletPageBlobCodec.Decode(stored, fileEntry, destination);
        return (int)fileEntry.UncompressedBytes;
    }

    /// <summary>
    /// Writes the recorded meshes when they differ from the file: pages were cooked, or the file
    /// held entries that this import did not use. Stored blobs of reused entries are copied from
    /// the old file without decoding.
    /// </summary>
    /// <returns>True when the file was written.</returns>
    public bool SaveIfChanged()
    {
        bool sameSet = CookCount == 0 && LoadFailure is null && _pending.Count == _stored.Count;
        if (sameSet || _pending.Count == 0)
            return false;

        // Encode the blobs first: cooked images are compressed, reused blobs are copied as stored.
        List<byte[]>[] blobs = new List<byte[]>[_pending.Count];
        List<MeshletPageFileEntry>[] files = new List<MeshletPageFileEntry>[_pending.Count];
        using (SafeFileHandle? oldFile = _pending.Any(static p => p.Reused is not null) && File.Exists(FilePath)
            ? File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                PendingEntry pending = _pending[i];
                blobs[i] = new List<byte[]>(pending.Table.PageCount);
                files[i] = new List<MeshletPageFileEntry>(pending.Table.PageCount);
                for (int page = 0; page < pending.Table.PageCount; page++)
                {
                    if (pending.Reused is { } reused)
                    {
                        MeshletPageFileEntry old = reused.Files[page];
                        byte[] stored = new byte[old.StoredBytes];
                        ReadExactly(oldFile!, stored, old.FileOffset);
                        blobs[i].Add(stored);
                        files[i].Add(old);
                        continue;
                    }

                    byte[] image = pending.Images![page];
                    byte[] blob = MeshletPageBlobCodec.Encode(image, out uint hash);
                    blobs[i].Add(blob);
                    files[i].Add(new MeshletPageFileEntry(0u, (uint)blob.Length, (uint)image.Length, hash));
                }
            }
        }

        // The index has a fixed size for fixed contents, so write it once to find where the blobs
        // start, place the blobs, and then write it again with the final offsets.
        int indexBytes = WriteIndex(new MemoryStream(), files).Length;
        long offset = AlignFile(HeaderBytes + (long)indexBytes);
        for (int i = 0; i < files.Length; i++)
            for (int page = 0; page < files[i].Count; page++)
            {
                MeshletPageFileEntry entry = files[i][page];
                files[i][page] = entry with { FileOffsetBlocks = checked((uint)(offset / MeshletPageFormat.FileAlignment)) };
                offset = AlignFile(offset + entry.StoredBytes);
            }
        byte[] index = WriteIndex(new MemoryStream(indexBytes), files);

        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new(stream))
            {
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write((uint)MeshletPageFormat.Version);
                writer.Write((uint)MeshletPageBuilder.CookVersion);
                writer.Write(_pending.Count);
                writer.Write(index.Length);
                writer.Write(XxHash64.HashToUInt64(index));
                writer.Write(index);
                for (int i = 0; i < files.Length; i++)
                    for (int page = 0; page < files[i].Count; page++)
                    {
                        long start = files[i][page].FileOffset;
                        writer.Write(new byte[checked((int)(start - stream.Position))]);
                        writer.Write(blobs[i][page]);
                    }
            }
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>Writes the index of the pending entries with the given file entries.</summary>
    private byte[] WriteIndex(MemoryStream stream, List<MeshletPageFileEntry>[] files)
    {
        using BinaryWriter writer = new(stream);
        for (int i = 0; i < _pending.Count; i++)
        {
            PendingEntry pending = _pending[i];
            MeshletPageTable table = pending.Table;
            writer.Write(pending.Identity);
            writer.Write(pending.PayloadFreshnessHash);
            writer.Write(pending.VertexDataHash);
            writer.Write((ushort)table.Kind);
            writer.Write(table.RootPageCount);
            writer.Write(table.ClusterOrder.Length);
            writer.Write(table.Groups.Length);
            writer.Write(table.Pages.Length);
            writer.Write(table.Dependencies.Length);
            writer.Write(MemoryMarshal.AsBytes(table.ClusterOrder.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(table.ClusterRecords.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(table.Groups.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(table.Pages.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(table.Dependencies.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(files[i])));
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Reads the header and index of a page file into <paramref name="stored"/>. The header stores
    /// the 64-bit xxHash of the index, so a damaged index is rejected before it is parsed.
    /// </summary>
    private static void ReadIndex(string filePath, Dictionary<string, StoredEntry> stored)
    {
        long fileLength;
        byte[] index;
        int entryCount;
        using (FileStream file = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (BinaryReader header = new(file))
        {
            fileLength = file.Length;
            if (header.ReadUInt32() != FileMagic || header.ReadUInt32() != FileVersion)
                throw new InvalidDataException("The file is not a page file of this version.");
            if (header.ReadUInt32() != (uint)MeshletPageFormat.Version || header.ReadUInt32() != (uint)MeshletPageBuilder.CookVersion)
                throw new InvalidDataException("The file holds pages of another page format or page cook version.");
            entryCount = header.ReadInt32();
            int indexBytes = header.ReadInt32();
            ulong indexHash = header.ReadUInt64();
            if (entryCount < 0 || indexBytes < 0 || HeaderBytes + (long)indexBytes > fileLength)
                throw new InvalidDataException("The page file index is out of range.");
            index = new byte[indexBytes];
            file.ReadExactly(index);
            if (XxHash64.HashToUInt64(index) != indexHash)
                throw new InvalidDataException("The page file index does not match its hash.");
        }

        using MemoryStream stream = new(index, writable: false);
        using BinaryReader reader = new(stream);
        for (int i = 0; i < entryCount; i++)
        {
            string identity = reader.ReadString();
            ulong freshness = reader.ReadUInt64();
            ulong vertexHash = reader.ReadUInt64();
            EMeshletPageFlags kind = (EMeshletPageFlags)reader.ReadUInt16();
            int rootPageCount = reader.ReadInt32();
            int clusterCount = reader.ReadInt32();
            int groupCount = reader.ReadInt32();
            int pageCount = reader.ReadInt32();
            int dependencyCount = reader.ReadInt32();
            long tableBytes =
                clusterCount * (long)(sizeof(uint) + Unsafe.SizeOf<MeshletClusterLodRecord>()) +
                groupCount * (long)Unsafe.SizeOf<MeshletLodGroupRecord>() +
                pageCount * (long)(Unsafe.SizeOf<MeshletPageRecord>() + Unsafe.SizeOf<MeshletPageFileEntry>()) +
                dependencyCount * (long)sizeof(uint);
            if (clusterCount < 0 || groupCount < 0 || pageCount < 1 || dependencyCount < 0 ||
                rootPageCount < 1 || rootPageCount > pageCount ||
                tableBytes > stream.Length - stream.Position)
                throw new InvalidDataException($"Page file entry {identity} has invalid counts.");

            MeshletPageTable table = new()
            {
                Kind = kind,
                RootPageCount = rootPageCount,
                ClusterOrder = ReadArray<uint>(reader, clusterCount),
                ClusterRecords = ReadArray<MeshletClusterLodRecord>(reader, clusterCount),
                Groups = ReadArray<MeshletLodGroupRecord>(reader, groupCount),
                Pages = ReadArray<MeshletPageRecord>(reader, pageCount),
                Dependencies = ReadArray<uint>(reader, dependencyCount),
            };
            MeshletPageFileEntry[] files = ReadArray<MeshletPageFileEntry>(reader, pageCount);
            foreach (MeshletPageFileEntry file in files)
                if (file.FileOffset + file.StoredBytes > fileLength || file.UncompressedBytes > MeshletPageFormat.PageBytes)
                    throw new InvalidDataException($"Page file entry {identity} names a blob out of range.");
            stored[identity] = new StoredEntry(freshness, vertexHash, table, files);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("The page file index does not have its stored size.");
    }

    private static T[] ReadArray<T>(BinaryReader reader, int count) where T : unmanaged
    {
        T[] values = new T[count];
        Span<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
        reader.BaseStream.ReadExactly(bytes);
        return values;
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            int read = RandomAccess.Read(handle, destination, offset);
            if (read <= 0)
                throw new EndOfStreamException("A page blob ends past the end of the page file.");
            destination = destination[read..];
            offset += read;
        }
    }

    private static long AlignFile(long offset)
        => (offset + (MeshletPageFormat.FileAlignment - 1)) & ~(long)(MeshletPageFormat.FileAlignment - 1);

    /// <summary>One mesh entry read from the page file.</summary>
    private sealed record StoredEntry(ulong PayloadFreshnessHash, ulong VertexDataHash, MeshletPageTable Table, MeshletPageFileEntry[] Files);

    /// <summary>
    /// One mesh entry for the next save: either cooked images, or a stored entry whose blobs are
    /// copied from the old file.
    /// </summary>
    private sealed record PendingEntry(
        string Identity,
        ulong PayloadFreshnessHash,
        ulong VertexDataHash,
        MeshletPageTable Table,
        byte[][]? Images,
        StoredEntry? Reused);
}
