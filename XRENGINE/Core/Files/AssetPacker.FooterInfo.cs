namespace XREngine.Core.Files
{

public static partial class AssetPacker
    {
        private readonly struct FooterInfo(long tocPosition, long stringTableOffset, long dictionaryOffset, long indexTableOffset)
        {
            public long TocPosition { get; } = tocPosition;
            public long StringTableOffset { get; } = stringTableOffset;
            public long DictionaryOffset { get; } = dictionaryOffset;
            public long IndexTableOffset { get; } = indexTableOffset;
        }
    }
}
