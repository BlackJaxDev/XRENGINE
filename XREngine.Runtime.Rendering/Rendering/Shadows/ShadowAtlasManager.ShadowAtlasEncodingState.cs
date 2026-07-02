namespace XREngine.Rendering.Shadows;

public sealed partial class ShadowAtlasManager
{
    private sealed class ShadowAtlasEncodingState(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
    {
        private readonly List<ShadowAtlasPageResource> _pages = new();
        private readonly List<ShadowBuddyPageAllocator> _allocators = new();
        private XRTexture2DArray? _textureArray;
        private XRTexture2DArray? _rasterDepthTextureArray;
        private int _allocatedLayerCount;

        public EShadowAtlasKind AtlasKind { get; } = atlasKind;
        public EShadowMapEncoding Encoding { get; } = encoding;
        public SkipReason LastFailureReason { get; private set; }
        public int PageCount => _pages.Count;
        public long ResidentBytes { get; private set; }
        public int LargestFreeRect { get; private set; }
        public long FreeTexelCount { get; private set; }

        public void BeginFrame()
        {
            LastFailureReason = SkipReason.None;
        }

        public void ResetOccupancy()
        {
            for (int i = 0; i < _allocators.Count; i++)
                _allocators[i].Reset();
        }

        public bool TryReserve(int pageIndex, int x, int y, int size)
        {
            if ((uint)pageIndex >= (uint)_allocators.Count)
                return false;

            return _allocators[pageIndex].TryReserve(x, y, size);
        }

        public bool TryReserveAlignedSubBlock(
            int pageIndex,
            int x,
            int y,
            int size,
            out int reservedX,
            out int reservedY)
        {
            if ((uint)pageIndex >= (uint)_allocators.Count)
            {
                reservedX = 0;
                reservedY = 0;
                return false;
            }

            return _allocators[pageIndex].TryReserveAlignedSubBlock(x, y, size, out reservedX, out reservedY);
        }

        public bool TryFree(in ShadowAtlasAllocation allocation)
        {
            if ((uint)allocation.PageIndex >= (uint)_allocators.Count ||
                allocation.Resolution == 0u)
            {
                return false;
            }

            return _allocators[allocation.PageIndex].TryFree(
                allocation.PixelRect.X,
                allocation.PixelRect.Y,
                checked((int)allocation.Resolution));
        }

        public bool TryAllocate(
            int size,
            ShadowAtlasManagerSettings settings,
            long currentTotalResidentBytes,
            out int pageIndex,
            out int x,
            out int y,
            out SkipReason skipReason)
        {
            for (int i = 0; i < _allocators.Count; i++)
            {
                if (_allocators[i].TryAllocate(size, out x, out y))
                {
                    pageIndex = i;
                    skipReason = SkipReason.None;
                    return true;
                }
            }

            if (!CanCreatePage(settings, currentTotalResidentBytes, out skipReason))
            {
                LastFailureReason = skipReason;
                pageIndex = -1;
                x = 0;
                y = 0;
                return false;
            }

            CreatePage(settings);
            pageIndex = _allocators.Count - 1;
            if (_allocators[pageIndex].TryAllocate(size, out x, out y))
            {
                skipReason = SkipReason.None;
                return true;
            }

            skipReason = SkipReason.AllocationFailed;
            LastFailureReason = skipReason;
            return false;
        }

        public bool TryAllocateContiguousGrid(
            int tileSize,
            int tilesPerAxis,
            ShadowAtlasManagerSettings settings,
            long currentTotalResidentBytes,
            out int pageIndex,
            out int x,
            out int y,
            out SkipReason skipReason)
        {
            if (tileSize <= 0 || tilesPerAxis <= 0)
            {
                pageIndex = -1;
                x = 0;
                y = 0;
                skipReason = SkipReason.InvalidRequest;
                LastFailureReason = skipReason;
                return false;
            }

            int groupSize = checked(tileSize * tilesPerAxis);
            if (groupSize > checked((int)settings.PageSize))
            {
                pageIndex = -1;
                x = 0;
                y = 0;
                skipReason = SkipReason.AllocationFailed;
                LastFailureReason = skipReason;
                return false;
            }

            for (int i = 0; i < _allocators.Count; i++)
            {
                if (_allocators[i].TryAllocate(groupSize, out x, out y))
                {
                    pageIndex = i;
                    skipReason = SkipReason.None;
                    return true;
                }
            }

            if (!CanCreatePage(settings, currentTotalResidentBytes, out skipReason))
            {
                LastFailureReason = skipReason;
                pageIndex = -1;
                x = 0;
                y = 0;
                return false;
            }

            CreatePage(settings);
            pageIndex = _allocators.Count - 1;
            if (_allocators[pageIndex].TryAllocate(groupSize, out x, out y))
            {
                skipReason = SkipReason.None;
                return true;
            }

            skipReason = SkipReason.AllocationFailed;
            LastFailureReason = skipReason;
            x = 0;
            y = 0;
            return false;
        }

        public void AppendPageDescriptors(List<ShadowAtlasPageDescriptor> output)
        {
            for (int i = 0; i < _pages.Count; i++)
                output.Add(_pages[i].Descriptor);

            LargestFreeRect = 0;
            FreeTexelCount = 0L;
            for (int i = 0; i < _allocators.Count; i++)
            {
                LargestFreeRect = Math.Max(LargestFreeRect, _allocators[i].LargestFreeBlockSize);
                FreeTexelCount += _allocators[i].FreeTexelCount;
            }
        }

        public void ResetResources()
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].FrameBuffer.Destroy();

            _pages.Clear();
            _allocators.Clear();
            _textureArray?.Destroy();
            _textureArray = null;
            _rasterDepthTextureArray?.Destroy();
            _rasterDepthTextureArray = null;
            _allocatedLayerCount = 0;
            ResidentBytes = 0L;
            LargestFreeRect = 0;
            FreeTexelCount = 0L;
            LastFailureReason = SkipReason.None;
        }

        private bool CanCreatePage(ShadowAtlasManagerSettings settings, long currentTotalResidentBytes, out SkipReason skipReason)
        {
            int pageLimit = GetPageLimit(settings);
            if (_pages.Count >= pageLimit)
            {
                skipReason = SkipReason.PageBudgetExceeded;
                return false;
            }

            int requiredLayers = _pages.Count + 1;
            long allocationBytes = EstimateAdditionalArrayBytes(settings, requiredLayers);
            if (settings.MaxMemoryBytes > 0 && currentTotalResidentBytes + allocationBytes > settings.MaxMemoryBytes)
            {
                skipReason = SkipReason.MemoryBudgetExceeded;
                return false;
            }

            skipReason = SkipReason.None;
            return true;
        }

        private static int GetPageLimit(ShadowAtlasManagerSettings settings)
            => Math.Max(1, settings.MaxPages);

        private void CreatePage(ShadowAtlasManagerSettings settings)
        {
            EnsureTextureArrays(settings, _pages.Count + 1);
            ShadowAtlasPageDescriptor descriptor = CreateDescriptor(AtlasKind, Encoding, _pages.Count, settings.PageSize);
            _pages.Add(new ShadowAtlasPageResource(descriptor, _textureArray, _rasterDepthTextureArray!));
            _allocators.Add(new ShadowBuddyPageAllocator(checked((int)settings.PageSize)));
        }

        public bool TryGetPageResource(int pageIndex, out ShadowAtlasPageResource? resource)
        {
            if ((uint)pageIndex < (uint)_pages.Count)
            {
                resource = _pages[pageIndex];
                return true;
            }

            resource = null;
            return false;
        }

        private long EstimateAdditionalArrayBytes(ShadowAtlasManagerSettings settings, int requiredLayers)
        {
            int targetLayers = CalculateLayerCapacity(settings, requiredLayers);
            if (targetLayers <= _allocatedLayerCount)
                return 0L;

            return checked(GetLayerBytes(settings.PageSize) * (targetLayers - _allocatedLayerCount));
        }

        private void EnsureTextureArrays(ShadowAtlasManagerSettings settings, int requiredLayers)
        {
            int layerCapacity = CalculateLayerCapacity(settings, requiredLayers);
            if (_rasterDepthTextureArray is not null &&
                _allocatedLayerCount >= layerCapacity &&
                (UsesDepthOnlyPages || _textureArray is not null))
            {
                return;
            }

            ShadowAtlasPageDescriptor descriptor = CreateDescriptor(AtlasKind, Encoding, 0, settings.PageSize);
            ShadowMapFormatDescriptor format = ShadowMapResourceFactory.GetPreferredFormat(Encoding);
            ETexMinFilter minFilter = format.RequiresLinearFiltering ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = format.RequiresLinearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            XRTexture2DArray? oldColor = _textureArray;
            XRTexture2DArray? oldDepth = _rasterDepthTextureArray;

            _textureArray = UsesDepthOnlyPages
                ? null
                : new XRTexture2DArray(
                    checked((uint)layerCapacity),
                    descriptor.PageSize,
                    descriptor.PageSize,
                    descriptor.InternalFormat,
                    descriptor.PixelFormat,
                    descriptor.PixelType,
                    allocateData: false)
                {
                    SamplerName = $"ShadowAtlas_{AtlasKind}_{Encoding}",
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    MinFilter = minFilter,
                    MagFilter = magFilter,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                };
            _rasterDepthTextureArray = new XRTexture2DArray(
                checked((uint)layerCapacity),
                descriptor.PageSize,
                descriptor.PageSize,
                EPixelInternalFormat.DepthComponent24,
                EPixelFormat.DepthComponent,
                EPixelType.UnsignedInt,
                allocateData: false)
            {
                SamplerName = $"ShadowAtlasDepth_{AtlasKind}_{Encoding}",
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
            };

            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].FrameBuffer.Destroy();
                _pages[i] = new ShadowAtlasPageResource(_pages[i].Descriptor, _textureArray, _rasterDepthTextureArray);
            }

            oldColor?.Destroy();
            oldDepth?.Destroy();
            _allocatedLayerCount = layerCapacity;
            ResidentBytes = checked(GetLayerBytes(settings.PageSize) * _allocatedLayerCount);
        }

        private bool UsesDepthOnlyPages
            => AtlasKind == EShadowAtlasKind.Directional &&
               Encoding == EShadowMapEncoding.Depth;

        private long GetLayerBytes(uint pageSize)
        {
            ShadowMapFormatDescriptor format = ShadowMapResourceFactory.GetPreferredFormat(Encoding);
            long rasterDepthBytes = checked((long)pageSize * pageSize * 4L);
            long colorBytes = UsesDepthOnlyPages
                ? 0L
                : checked((long)pageSize * pageSize * format.BytesPerTexel);
            return checked(colorBytes + rasterDepthBytes);
        }

        private static int CalculateLayerCapacity(ShadowAtlasManagerSettings settings, int requiredLayers)
        {
            int maxLayers = GetPageLimit(settings);
            int capacity = 1;
            requiredLayers = Math.Clamp(requiredLayers, 1, maxLayers);
            while (capacity < requiredLayers && capacity < maxLayers)
                capacity <<= 1;

            return Math.Min(capacity, maxLayers);
        }

        private static ShadowAtlasPageDescriptor CreateDescriptor(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding, int pageIndex, uint pageSize)
        {
            ShadowMapFormatDescriptor format = ShadowMapResourceFactory.GetPreferredFormat(encoding);
            long rasterDepthBytes = checked((long)pageSize * pageSize * 4L);
            long colorBytes = atlasKind == EShadowAtlasKind.Directional && encoding == EShadowMapEncoding.Depth
                ? 0L
                : checked((long)pageSize * pageSize * format.BytesPerTexel);
            long bytes = checked(colorBytes + rasterDepthBytes);
            return new ShadowAtlasPageDescriptor(atlasKind, encoding, pageIndex, pageSize, format.InternalFormat, format.PixelFormat, format.PixelType, bytes);
        }
    }
}
