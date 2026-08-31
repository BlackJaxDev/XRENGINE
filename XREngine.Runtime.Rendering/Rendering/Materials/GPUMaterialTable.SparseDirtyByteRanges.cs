using System;

namespace XREngine.Rendering.Materials;

public partial class GPUMaterialTable
{
    internal struct SparseDirtyByteRanges
    {
        private DirtyByteRange[]? _ranges;
        private DirtyByteRange[]? _lastPublishedRanges;
        private int _count;
        private int _lastPublishedCount;

        public bool IsFullDirty { get; private set; }
        public ReadOnlySpan<DirtyByteRange> Ranges => _ranges.AsSpan(0, _count);

        public void Mark(uint byteOffset, uint byteCount)
        {
            if (IsFullDirty || byteCount == 0u)
                return;

            uint byteEndExclusive = checked(byteOffset + byteCount);
            for (int index = 0; index < _count; ++index)
            {
                DirtyByteRange existing = _ranges![index];
                if (byteEndExclusive < existing.ByteOffset || byteOffset > existing.ByteEndExclusive)
                    continue;

                MergeOverlaps(index, Math.Min(existing.ByteOffset, byteOffset), Math.Max(existing.ByteEndExclusive, byteEndExclusive));
                return;
            }

            EnsureCapacity(_count + 1);
            _ranges![_count++] = new DirtyByteRange
            {
                ByteOffset = byteOffset,
                ByteEndExclusive = byteEndExclusive,
                HasValue = true,
            };
        }

        public void MarkFull(uint byteCount)
        {
            _count = 0;
            IsFullDirty = byteCount != 0u;
            if (IsFullDirty)
            {
                EnsureCapacity(1);
                _ranges![0] = new DirtyByteRange
                {
                    ByteOffset = 0u,
                    ByteEndExclusive = byteCount,
                    HasValue = true,
                };
                _count = 1;
            }
        }

        public PublicationRangeCounts GetPublicationRangeCounts(uint rowSize)
        {
            if (_count == 0 || rowSize == 0u)
                return default;

            ulong byteCount = 0ul;
            uint rowCount = 0u;
            for (int index = 0; index < _count; ++index)
            {
                DirtyByteRange range = _ranges![index];
                byteCount += range.ByteCount;
                rowCount += checked((range.ByteCount + rowSize - 1u) / rowSize);
            }

            return new PublicationRangeCounts(_count, rowCount, byteCount);
        }

        public void CapturePublishedRanges()
        {
            EnsureLastPublishedCapacity(_count);
            if (_count > 0)
                Array.Copy(_ranges!, _lastPublishedRanges!, _count);
            _lastPublishedCount = _count;
        }

        public int CopyLastPublishedRanges(Span<GPUMaterialTableDirtyRange> destination, uint rowSize)
        {
            int copied = Math.Min(destination.Length, _lastPublishedCount);
            for (int index = 0; index < copied; ++index)
            {
                DirtyByteRange range = _lastPublishedRanges![index];
                destination[index] = range.ToIndexRange(rowSize);
            }

            return _lastPublishedCount;
        }

        public void Clear()
        {
            _count = 0;
            IsFullDirty = false;
        }

        public bool Intersects(uint byteOffset, uint byteCount)
        {
            if (IsFullDirty)
                return true;

            uint byteEndExclusive = checked(byteOffset + byteCount);
            for (int index = 0; index < _count; ++index)
            {
                DirtyByteRange range = _ranges![index];
                if (range.ByteEndExclusive > byteOffset && range.ByteOffset < byteEndExclusive)
                    return true;
            }

            return false;
        }

        private void MergeOverlaps(int mergedIndex, uint byteOffset, uint byteEndExclusive)
        {
            bool mergedAnotherRange;
            do
            {
                mergedAnotherRange = false;
                for (int index = _count - 1; index >= 0; --index)
                {
                    if (index == mergedIndex)
                        continue;

                    DirtyByteRange candidate = _ranges![index];
                    if (candidate.ByteEndExclusive < byteOffset || candidate.ByteOffset > byteEndExclusive)
                        continue;

                    byteOffset = Math.Min(byteOffset, candidate.ByteOffset);
                    byteEndExclusive = Math.Max(byteEndExclusive, candidate.ByteEndExclusive);
                    int lastIndex = --_count;
                    if (index != lastIndex)
                        _ranges[index] = _ranges[lastIndex];
                    if (lastIndex == mergedIndex)
                        mergedIndex = index;
                    mergedAnotherRange = true;
                    break;
                }
            }
            while (mergedAnotherRange);

            _ranges![mergedIndex] = new DirtyByteRange
            {
                ByteOffset = byteOffset,
                ByteEndExclusive = byteEndExclusive,
                HasValue = true,
            };
        }

        private void EnsureCapacity(int required)
        {
            if (_ranges is not null && _ranges.Length >= required)
                return;

            int capacity = _ranges is null ? 8 : _ranges.Length * 2;
            while (capacity < required)
                capacity *= 2;
            Array.Resize(ref _ranges, capacity);
        }

        private void EnsureLastPublishedCapacity(int required)
        {
            if (_lastPublishedRanges is not null && _lastPublishedRanges.Length >= required)
                return;

            int capacity = _lastPublishedRanges is null ? 8 : _lastPublishedRanges.Length * 2;
            while (capacity < required)
                capacity *= 2;
            Array.Resize(ref _lastPublishedRanges, capacity);
        }
    }

    internal readonly record struct PublicationRangeCounts(int RangeCount, uint RowCount, ulong ByteCount)
    {
        public static PublicationRangeCounts Full(uint byteCount, uint rowSize)
            => new(1, rowSize == 0u ? 0u : byteCount / rowSize, byteCount);
    }
}
