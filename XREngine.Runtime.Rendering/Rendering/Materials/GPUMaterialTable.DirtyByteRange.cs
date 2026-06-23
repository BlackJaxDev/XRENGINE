namespace XREngine.Rendering.Materials
{
public partial class GPUMaterialTable
    {
        private struct DirtyByteRange
        {
            public uint ByteOffset;
            public uint ByteEndExclusive;
            public bool HasValue;
            public uint ByteCount => HasValue && ByteEndExclusive > ByteOffset ? ByteEndExclusive - ByteOffset : 0u;

            public void Mark(uint byteOffset, uint byteCount)
            {
                if (byteCount == 0u)
                    return;

                uint end = byteOffset + byteCount;
                if (end < byteOffset)
                    throw new InvalidOperationException("GPU material table dirty byte range overflow.");

                if (!HasValue)
                {
                    ByteOffset = byteOffset;
                    ByteEndExclusive = end;
                    HasValue = true;
                    return;
                }

                ByteOffset = Math.Min(ByteOffset, byteOffset);
                ByteEndExclusive = Math.Max(ByteEndExclusive, end);
            }

            public GPUMaterialTableDirtyRange ToIndexRange(uint rowSize)
            {
                if (!HasValue || rowSize == 0u)
                    return default;

                uint firstIndex = ByteOffset / rowSize;
                uint lastIndexExclusive = (ByteEndExclusive + rowSize - 1u) / rowSize;
                return new GPUMaterialTableDirtyRange(
                    firstIndex,
                    lastIndexExclusive - firstIndex,
                    ByteOffset,
                    ByteCount);
            }

            public void Clear()
            {
                ByteOffset = 0u;
                ByteEndExclusive = 0u;
                HasValue = false;
            }
        }
    }
}
