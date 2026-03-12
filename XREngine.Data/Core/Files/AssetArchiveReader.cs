using System.Text;
using XREngine.Data;

namespace XREngine.Core.Files;

public static class AssetArchiveReader
{
    private const int Magic = 0x4652454B;
    private const int CurrentVersion = 4;
    private const int FooterSize = sizeof(long) * 5;
    private const int TocEntrySize = sizeof(uint) + sizeof(int) + sizeof(long) + sizeof(int) + sizeof(long) + sizeof(ulong) + sizeof(long) + sizeof(byte) + 3;
    private static readonly Encoding StringEncoding = Encoding.UTF8;

    public static byte[] GetAsset(string archiveFilePath, string assetPath)
    {
        using FileStream stream = OpenReadStream(archiveFilePath);
        using BinaryReader reader = new(stream, StringEncoding, leaveOpen: false);

        ArchiveHeader header = ReadHeader(reader);
        ArchiveFooter footer = ReadFooter(reader);
        StringTable stringTable = ReadStringTable(reader, footer);

        return header.LookupMode switch
        {
            ArchiveLookupMode.HashBuckets => GetAssetFromBuckets(reader, header.FileCount, footer, stringTable, assetPath),
            ArchiveLookupMode.SortedByHash => GetAssetSorted(reader, header.FileCount, footer, stringTable, assetPath),
            _ => GetAssetLinear(reader, header.FileCount, footer.TocPosition, stringTable, assetPath),
        };
    }

    public static IReadOnlyList<string> GetAssetPaths(string archiveFilePath)
    {
        using FileStream stream = OpenReadStream(archiveFilePath);
        using BinaryReader reader = new(stream, StringEncoding, leaveOpen: false);

        ArchiveHeader header = ReadHeader(reader);
        ArchiveFooter footer = ReadFooter(reader);
        StringTable stringTable = ReadStringTable(reader, footer);

        return ReadAssetPaths(reader, header.FileCount, footer.TocPosition, stringTable);
    }

    private static FileStream OpenReadStream(string archiveFilePath)
        => new(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);

    private static ArchiveHeader ReadHeader(BinaryReader reader)
    {
        reader.BaseStream.Position = 0;

        int magic = reader.ReadInt32();
        if (magic != Magic)
            throw new InvalidOperationException("Invalid asset archive format.");

        int version = reader.ReadInt32();
        if (version != CurrentVersion)
            throw new InvalidOperationException($"Unsupported archive version '{version}'. Only V{CurrentVersion} is supported.");

        _ = reader.ReadInt32(); // flags
        var lookupMode = (ArchiveLookupMode)reader.ReadInt32();
        int fileCount = reader.ReadInt32();
        _ = reader.ReadInt64(); // build timestamp
        _ = reader.ReadInt64(); // dead bytes

        return new ArchiveHeader(lookupMode, fileCount);
    }

    private static ArchiveFooter ReadFooter(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        long saved = stream.Position;
        stream.Position = stream.Length - FooterSize;

        long tocPosition = reader.ReadInt64();
        long stringTableOffset = reader.ReadInt64();
        long dictionaryOffset = reader.ReadInt64();
        long indexTableOffset = reader.ReadInt64();
        _ = reader.ReadInt64(); // dead bytes

        stream.Position = saved;
        return new ArchiveFooter(tocPosition, stringTableOffset, dictionaryOffset, indexTableOffset);
    }

    private static StringTable ReadStringTable(BinaryReader reader, ArchiveFooter footer)
    {
        Stream stream = reader.BaseStream;
        stream.Position = ResolveDictionaryOffset(footer);

        int commonSubstringCount = reader.ReadInt32();
        List<string> commonSubstrings = new(commonSubstringCount);
        for (int i = 0; i < commonSubstringCount; i++)
        {
            int length = reader.ReadByte();
            commonSubstrings.Add(StringEncoding.GetString(reader.ReadBytes(length)));
        }

        int stringCount = reader.ReadInt32();
        List<string> strings = new(stringCount);
        for (int i = 0; i < stringCount; i++)
        {
            int flags = reader.ReadByte();
            string value;

            if ((flags & 0x80) != 0)
            {
                int prefixLength = reader.ReadByte();
                string prefix = strings[i - 1][..prefixLength];
                var builder = new StringBuilder();
                while (true)
                {
                    byte partType = reader.ReadByte();
                    if (partType == 0xFF)
                        break;

                    if ((partType & 0x80) != 0)
                    {
                        int index = partType & 0x7F;
                        builder.Append(index < commonSubstrings.Count ? commonSubstrings[index] : string.Empty);
                    }
                    else
                    {
                        int length = partType;
                        builder.Append(StringEncoding.GetString(reader.ReadBytes(length)));
                    }
                }

                value = prefix + builder;
            }
            else
            {
                int length = reader.ReadUInt16();
                value = StringEncoding.GetString(reader.ReadBytes(length));
            }

            strings.Add(value);
        }

        return new StringTable(strings.ToArray());
    }

    private static IReadOnlyList<string> ReadAssetPaths(BinaryReader reader, int fileCount, long tocPosition, StringTable stringTable)
    {
        Stream stream = reader.BaseStream;
        stream.Position = tocPosition;

        List<string> paths = new(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            TocEntry entry = ReadTocEntry(reader);
            paths.Add(NormalizePath(stringTable.GetString(entry.StringOffset)));
        }

        return paths;
    }

    private static byte[] GetAssetLinear(BinaryReader reader, int fileCount, long tocPosition, StringTable stringTable, string assetPath)
    {
        string normalizedPath = NormalizePath(assetPath);
        uint targetHash = FastHash(normalizedPath);

        Stream stream = reader.BaseStream;
        stream.Position = tocPosition;
        for (int i = 0; i < fileCount; i++)
        {
            TocEntry entry = ReadTocEntry(reader);
            if (TryReadAsset(reader, stringTable, entry, normalizedPath, targetHash, out byte[] data))
                return data;
        }

        throw new FileNotFoundException($"Asset {assetPath} not found");
    }

    private static byte[] GetAssetFromBuckets(BinaryReader reader, int fileCount, ArchiveFooter footer, StringTable stringTable, string assetPath)
    {
        if (footer.IndexTableOffset == 0)
            return GetAssetLinear(reader, fileCount, footer.TocPosition, stringTable, assetPath);

        Stream stream = reader.BaseStream;
        stream.Position = footer.IndexTableOffset;

        int bucketCount = reader.ReadInt32();
        if (bucketCount <= 0 || (bucketCount & (bucketCount - 1)) != 0)
            return GetAssetLinear(reader, fileCount, footer.TocPosition, stringTable, assetPath);

        string normalizedPath = NormalizePath(assetPath);
        uint targetHash = FastHash(normalizedPath);
        int bucketIndex = (int)(targetHash & (bucketCount - 1));

        stream.Position = footer.IndexTableOffset + sizeof(int) + bucketIndex * sizeof(int) * 2L;
        int start = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (count <= 0)
            throw new FileNotFoundException($"Asset {assetPath} not found");

        stream.Position = footer.TocPosition + start * (long)TocEntrySize;
        for (int i = 0; i < count; i++)
        {
            TocEntry entry = ReadTocEntry(reader);
            if (TryReadAsset(reader, stringTable, entry, normalizedPath, targetHash, out byte[] data))
                return data;
        }

        throw new FileNotFoundException($"Asset {assetPath} not found");
    }

    private static byte[] GetAssetSorted(BinaryReader reader, int fileCount, ArchiveFooter footer, StringTable stringTable, string assetPath)
    {
        string normalizedPath = NormalizePath(assetPath);
        uint targetHash = FastHash(normalizedPath);
        int left = 0;
        int right = fileCount - 1;

        while (left <= right)
        {
            int mid = left + ((right - left) / 2);
            TocEntry midEntry = ReadTocEntryAt(reader, footer.TocPosition, mid);
            if (midEntry.Hash == targetHash)
            {
                int first = mid;
                while (first > 0)
                {
                    TocEntry previousEntry = ReadTocEntryAt(reader, footer.TocPosition, first - 1);
                    if (previousEntry.Hash != targetHash)
                        break;
                    first--;
                }

                int last = mid;
                while (last < fileCount - 1)
                {
                    TocEntry nextEntry = ReadTocEntryAt(reader, footer.TocPosition, last + 1);
                    if (nextEntry.Hash != targetHash)
                        break;
                    last++;
                }

                for (int i = first; i <= last; i++)
                {
                    TocEntry entry = ReadTocEntryAt(reader, footer.TocPosition, i);
                    if (TryReadAsset(reader, stringTable, entry, normalizedPath, targetHash, out byte[] data))
                        return data;
                }

                break;
            }

            if (midEntry.Hash < targetHash)
                left = mid + 1;
            else
                right = mid - 1;
        }

        throw new FileNotFoundException($"Asset {assetPath} not found");
    }

    private static bool TryReadAsset(BinaryReader reader, StringTable stringTable, TocEntry entry, string normalizedPath, uint targetHash, out byte[] data)
    {
        if (entry.Hash != targetHash)
        {
            data = Array.Empty<byte>();
            return false;
        }

        string currentPath = NormalizePath(stringTable.GetString(entry.StringOffset));
        if (!string.Equals(currentPath, normalizedPath, StringComparison.Ordinal))
        {
            data = Array.Empty<byte>();
            return false;
        }

        Stream stream = reader.BaseStream;
        long saved = stream.Position;
        stream.Position = entry.DataOffset;
        byte[] compressed = reader.ReadBytes(entry.CompressedSize);
        stream.Position = saved;

        if (compressed.Length != entry.CompressedSize)
            throw new EndOfStreamException($"Archive entry '{normalizedPath}' ended unexpectedly.");

        data = Compression.Decompress(compressed, entry.Codec, checked((int)entry.UncompressedSize));
        return true;
    }

    private static TocEntry ReadTocEntry(BinaryReader reader)
    {
        uint hash = reader.ReadUInt32();
        int stringOffset = reader.ReadInt32();
        long dataOffset = reader.ReadInt64();
        int compressedSize = reader.ReadInt32();
        long uncompressedSize = reader.ReadInt64();
        _ = reader.ReadUInt64(); // content hash
        _ = reader.ReadInt64(); // source timestamp
        var codec = (CompressionCodec)reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        return new TocEntry(hash, stringOffset, dataOffset, compressedSize, uncompressedSize, codec);
    }

    private static TocEntry ReadTocEntryAt(BinaryReader reader, long tocPosition, int index)
    {
        Stream stream = reader.BaseStream;
        long saved = stream.Position;
        stream.Position = tocPosition + index * (long)TocEntrySize;
        TocEntry entry = ReadTocEntry(reader);
        stream.Position = saved;
        return entry;
    }

    private static long ResolveDictionaryOffset(ArchiveFooter footer)
        => footer.DictionaryOffset < footer.StringTableOffset
            ? footer.StringTableOffset + footer.DictionaryOffset
            : footer.DictionaryOffset;

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private static uint FastHash(string input)
    {
        uint hash = 5381;
        foreach (char c in input)
            hash = ((hash << 5) + hash) ^ c;
        return hash;
    }

    private enum ArchiveLookupMode
    {
        Linear = 0,
        HashBuckets = 1,
        SortedByHash = 2,
    }

    private readonly record struct ArchiveHeader(ArchiveLookupMode LookupMode, int FileCount);

    private readonly record struct ArchiveFooter(
        long TocPosition,
        long StringTableOffset,
        long DictionaryOffset,
        long IndexTableOffset);

    private readonly record struct TocEntry(
        uint Hash,
        int StringOffset,
        long DataOffset,
        int CompressedSize,
        long UncompressedSize,
        CompressionCodec Codec);

    private sealed class StringTable(string[] strings)
    {
        private readonly string[] _strings = strings;

        public string GetString(int index)
            => _strings[index];
    }
}
