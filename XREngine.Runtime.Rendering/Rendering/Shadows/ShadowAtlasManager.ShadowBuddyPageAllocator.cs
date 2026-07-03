namespace XREngine.Rendering.Shadows;

public sealed partial class ShadowAtlasManager
{
    private sealed class ShadowBuddyPageAllocator
    {
        private readonly int _pageSize;
        private readonly int[] _levelSizes;
        private readonly ulong[][] _freeBitsByLevel;

        public ShadowBuddyPageAllocator(int pageSize)
        {
            _pageSize = pageSize;
            int levelCount = CalculateLevelCount(pageSize);
            _levelSizes = new int[levelCount];
            _freeBitsByLevel = new ulong[levelCount][];
            int size = pageSize;
            for (int level = 0; level < levelCount; level++)
            {
                _levelSizes[level] = size;
                int slotsPerAxis = 1 << level;
                int slotCount = slotsPerAxis * slotsPerAxis;
                _freeBitsByLevel[level] = new ulong[(slotCount + 63) >> 6];
                size = Math.Max(1, size >> 1);
            }

            Reset();
        }

        public int LargestFreeBlockSize { get; private set; }
        public long FreeTexelCount { get; private set; }

        public void Reset()
        {
            for (int i = 0; i < _freeBitsByLevel.Length; i++)
                Array.Clear(_freeBitsByLevel[i]);

            LargestFreeBlockSize = 0;
            FreeTexelCount = 0L;
            AddFreeBlock(level: 0, slotIndex: 0);
        }

        public bool TryAllocate(int requestedSize, out int x, out int y)
        {
            int size = NormalizeBlockSize(requestedSize);
            int targetLevel = GetLevelForSize(size);
            if (!TryTakeFreeBlockAtOrAbove(targetLevel, out int level, out int slotIndex))
            {
                x = 0;
                y = 0;
                return false;
            }

            SplitToLevel(level, slotIndex, targetLevel, targetX: -1, targetY: -1, out int allocatedSlot);
            SlotToXY(targetLevel, allocatedSlot, out x, out y);
            return true;
        }

        public bool TryReserve(int x, int y, int requestedSize)
        {
            int size = NormalizeBlockSize(requestedSize);
            if (!IsValidAlignedRegion(x, y, size))
                return false;

            int targetLevel = GetLevelForSize(size);
            for (int searchLevel = targetLevel; searchLevel >= 0; searchLevel--)
            {
                int ancestorSize = _levelSizes[searchLevel];
                int ancestorX = AlignDown(x, ancestorSize);
                int ancestorY = AlignDown(y, ancestorSize);
                int ancestorSlot = XYToSlot(searchLevel, ancestorX, ancestorY);
                if (!TryTakeFreeBlock(searchLevel, ancestorSlot))
                    continue;

                SplitToLevel(searchLevel, ancestorSlot, targetLevel, x, y, out int reservedSlot);
                SlotToXY(targetLevel, reservedSlot, out int reservedX, out int reservedY);
                if (reservedX != x || reservedY != y)
                    throw new InvalidOperationException("Buddy reservation produced an unexpected block.");

                return true;
            }

            return false;
        }

        public bool TryReserveAlignedSubBlock(
            int x,
            int y,
            int requestedSize,
            out int reservedX,
            out int reservedY)
        {
            int size = NormalizeBlockSize(requestedSize);
            reservedX = AlignDown(x, size);
            reservedY = AlignDown(y, size);
            if (!IsValidAlignedRegion(reservedX, reservedY, size))
            {
                reservedX = 0;
                reservedY = 0;
                return false;
            }

            if (TryReserve(reservedX, reservedY, size))
                return true;

            reservedX = 0;
            reservedY = 0;
            return false;
        }

        public bool TryFree(int x, int y, int requestedSize)
        {
            int size = NormalizeBlockSize(requestedSize);
            if (!IsValidAlignedRegion(x, y, size))
                return false;

            int level = GetLevelForSize(size);
            int slotIndex = XYToSlot(level, x, y);
            if (IsFree(level, slotIndex))
                return false;

            AddFreeBlock(level, slotIndex);
            TryMergeFreeBlock(level, slotIndex);
            return true;
        }

        private void SplitToLevel(
            int sourceLevel,
            int sourceSlot,
            int targetLevel,
            int targetX,
            int targetY,
            out int allocatedSlot)
        {
            int currentLevel = sourceLevel;
            int currentSlot = sourceSlot;
            while (currentLevel < targetLevel)
            {
                int childLevel = currentLevel + 1;
                GetChildSlots(currentLevel, currentSlot, childLevel, out int child0, out int child1, out int child2, out int child3);
                int selectedChild = child0;
                if (targetX >= 0 && targetY >= 0)
                {
                    Span<int> children = [child0, child1, child2, child3];
                    for (int child = 0; child < children.Length; child++)
                    {
                        int candidate = children[child];
                        SlotToXY(childLevel, candidate, out int childX, out int childY);
                        if (new ShadowBlock(childX, childY, _levelSizes[childLevel]).Contains(targetX, targetY, _levelSizes[targetLevel]))
                        {
                            selectedChild = candidate;
                            break;
                        }
                    }
                }

                AddChildIfNotSelected(childLevel, child0, selectedChild);
                AddChildIfNotSelected(childLevel, child1, selectedChild);
                AddChildIfNotSelected(childLevel, child2, selectedChild);
                AddChildIfNotSelected(childLevel, child3, selectedChild);

                currentLevel = childLevel;
                currentSlot = selectedChild;
            }

            allocatedSlot = currentSlot;
        }

        private void AddChildIfNotSelected(int childLevel, int childSlot, int selectedChild)
        {
            if (childSlot != selectedChild)
                AddFreeBlock(childLevel, childSlot);
        }

        private void GetChildSlots(
            int parentLevel,
            int parentSlot,
            int childLevel,
            out int child0,
            out int child1,
            out int child2,
            out int child3)
        {
            SlotToXY(parentLevel, parentSlot, out int parentX, out int parentY);
            int half = _levelSizes[childLevel];
            child0 = XYToSlot(childLevel, parentX, parentY);
            child1 = XYToSlot(childLevel, parentX + half, parentY);
            child2 = XYToSlot(childLevel, parentX, parentY + half);
            child3 = XYToSlot(childLevel, parentX + half, parentY + half);
        }

        private void GetSiblingSlots(
            int level,
            int slotIndex,
            out int sibling0,
            out int sibling1,
            out int sibling2,
            out int sibling3,
            out int parentSlot)
        {
            SlotToXY(level, slotIndex, out int x, out int y);
            int parentLevel = level - 1;
            int parentSize = _levelSizes[parentLevel];
            int parentX = AlignDown(x, parentSize);
            int parentY = AlignDown(y, parentSize);
            parentSlot = XYToSlot(parentLevel, parentX, parentY);
            GetChildSlots(parentLevel, parentSlot, level, out sibling0, out sibling1, out sibling2, out sibling3);
        }

        private bool TryTakeFreeBlockAtOrAbove(int targetLevel, out int level, out int slotIndex)
        {
            for (level = targetLevel; level >= 0; level--)
            {
                if (!TryFindLowestFreeSlot(level, out slotIndex))
                    continue;

                RemoveFreeBlock(level, slotIndex);
                return true;
            }

            level = 0;
            slotIndex = 0;
            return false;
        }

        private bool TryTakeFreeBlock(int level, int slotIndex)
        {
            if (!IsFree(level, slotIndex))
                return false;

            RemoveFreeBlock(level, slotIndex);
            return true;
        }

        private void AddFreeBlock(int level, int slotIndex)
        {
            SetFree(level, slotIndex, true);
            int size = _levelSizes[level];
            FreeTexelCount += (long)size * size;
            if (size > LargestFreeBlockSize)
                LargestFreeBlockSize = size;
        }

        private void RemoveFreeBlock(int level, int slotIndex)
        {
            SetFree(level, slotIndex, false);
            int size = _levelSizes[level];
            FreeTexelCount -= (long)size * size;
            if (size == LargestFreeBlockSize && !AnyFreeAtLevel(level))
                LargestFreeBlockSize = FindLargestFreeBlockSize();
        }

        private void TryMergeFreeBlock(int level, int slotIndex)
        {
            int currentLevel = level;
            int currentSlot = slotIndex;
            while (currentLevel > 0)
            {
                GetSiblingSlots(
                    currentLevel,
                    currentSlot,
                    out int sibling0,
                    out int sibling1,
                    out int sibling2,
                    out int sibling3,
                    out int parentSlot);
                if (!AreFourChildrenFree(currentLevel, sibling0, sibling1, sibling2, sibling3))
                    return;

                RemoveFreeBlock(currentLevel, sibling0);
                RemoveFreeBlock(currentLevel, sibling1);
                RemoveFreeBlock(currentLevel, sibling2);
                RemoveFreeBlock(currentLevel, sibling3);

                currentLevel--;
                currentSlot = parentSlot;
                AddFreeBlock(currentLevel, currentSlot);
            }
        }

        private int NormalizeBlockSize(int requestedSize)
        {
            int size = 1;
            requestedSize = Math.Clamp(requestedSize, 1, _pageSize);
            while (size < requestedSize)
                size <<= 1;
            return Math.Min(size, _pageSize);
        }

        private int GetLevelForSize(int size)
        {
            if (size <= 0 ||
                size > _pageSize ||
                (size & (size - 1)) != 0)
            {
                throw new InvalidOperationException($"Shadow buddy allocator cannot represent block size {size}.");
            }

            int pageLog2 = BitOperations.Log2((uint)_pageSize);
            int sizeLog2 = BitOperations.Log2((uint)size);
            int level = pageLog2 - sizeLog2;
            if ((uint)level >= (uint)_levelSizes.Length || _levelSizes[level] != size)
                throw new InvalidOperationException($"Shadow buddy allocator cannot represent block size {size}.");

            return level;
        }

        private int FindLargestFreeBlockSize()
        {
            for (int i = 0; i < _freeBitsByLevel.Length; i++)
                if (AnyFreeAtLevel(i))
                    return _levelSizes[i];

            return 0;
        }

        private bool TryFindLowestFreeSlot(int level, out int slotIndex)
        {
            ulong[] bits = _freeBitsByLevel[level];
            for (int wordIndex = 0; wordIndex < bits.Length; wordIndex++)
            {
                ulong word = bits[wordIndex];
                if (word == 0UL)
                    continue;

                slotIndex = (wordIndex << 6) + BitOperations.TrailingZeroCount(word);
                return true;
            }

            slotIndex = -1;
            return false;
        }

        private bool AreFourChildrenFree(int level, int slot0, int slot1, int slot2, int slot3)
            => IsFree(level, slot0) &&
               IsFree(level, slot1) &&
               IsFree(level, slot2) &&
               IsFree(level, slot3);

        private bool AnyFreeAtLevel(int level)
        {
            ulong[] bits = _freeBitsByLevel[level];
            for (int i = 0; i < bits.Length; i++)
                if (bits[i] != 0UL)
                    return true;

            return false;
        }

        private bool IsFree(int level, int slotIndex)
        {
            ulong[] bits = _freeBitsByLevel[level];
            int wordIndex = slotIndex >> 6;
            if ((uint)wordIndex >= (uint)bits.Length)
                return false;

            ulong mask = 1UL << (slotIndex & 63);
            return (bits[wordIndex] & mask) != 0UL;
        }

        private void SetFree(int level, int slotIndex, bool free)
        {
            ulong[] bits = _freeBitsByLevel[level];
            int wordIndex = slotIndex >> 6;
            ulong mask = 1UL << (slotIndex & 63);
            if (free)
                bits[wordIndex] |= mask;
            else
                bits[wordIndex] &= ~mask;
        }

        private int XYToSlot(int level, int x, int y)
        {
            int size = _levelSizes[level];
            int slotsPerAxis = 1 << level;
            return (y / size * slotsPerAxis) + (x / size);
        }

        private void SlotToXY(int level, int slotIndex, out int x, out int y)
        {
            int size = _levelSizes[level];
            int slotsPerAxis = 1 << level;
            int slotX = slotIndex & (slotsPerAxis - 1);
            int slotY = slotIndex / slotsPerAxis;
            x = slotX * size;
            y = slotY * size;
        }

        private bool IsValidAlignedRegion(int x, int y, int size)
            => size > 0 &&
               x >= 0 &&
               y >= 0 &&
               x + size <= _pageSize &&
               y + size <= _pageSize &&
               x % size == 0 &&
               y % size == 0;

        private static int AlignDown(int value, int alignment)
            => alignment <= 1 ? value : value - (value % alignment);

        private static int CalculateLevelCount(int pageSize)
        {
            int levels = 1;
            int size = Math.Max(1, pageSize);
            while (size > 1)
            {
                levels++;
                size >>= 1;
            }

            return levels;
        }
    }
}
