namespace XREngine.Core.Files
{

public static partial class AssetPacker
    {
        private readonly struct TocEntryData(uint hash, int stringOffset, long dataOffset, int compressedSize)
        {
            public uint Hash { get; } = hash;
            public int StringOffset { get; } = stringOffset;
            public long DataOffset { get; } = dataOffset;
            public int CompressedSize { get; } = compressedSize;
        }
    }
}
