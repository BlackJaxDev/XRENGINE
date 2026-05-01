using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    /// <summary>
    /// Tracks the currently allocated GPU memory size for this texture in bytes.
    /// Sparse logical storage only updates this with committed page bytes.
    /// </summary>
    private long _allocatedVRAMBytes = 0;
    private uint _allocatedLevels = 0;
    private uint _allocatedWidth = 0;
    private uint _allocatedHeight = 0;
    private ESizedInternalFormat _allocatedInternalFormat;
    private volatile bool _pendingImmutableStorageRecreate;
    private uint _externalMemoryObject;

    private EPixelInternalFormat? EnsureStorageAllocated()
    {
        EPixelInternalFormat? internalFormatForce = null;
        if (!Data.Resizable && !StorageSet)
        {
            uint width = Math.Max(1u, Data.Width);
            uint height = Math.Max(1u, Data.Height);
            int naturalSmallestMipIndex = Data.SmallestMipmapLevel;
            int mipmapCount = Math.Max(0, Mipmaps?.Length ?? 0);
            int requestedLevels = Math.Max(1, naturalSmallestMipIndex + 1);
            bool sparseLogicalAllocation = Data.SparseTextureStreamingEnabled
                && Data.SparseTextureStreamingLogicalWidth > 0
                && Data.SparseTextureStreamingLogicalHeight > 0
                && Data.SparseTextureStreamingLogicalMipCount > 0;
            if (sparseLogicalAllocation)
            {
                width = Math.Max(1u, Data.SparseTextureStreamingLogicalWidth);
                height = Math.Max(1u, Data.SparseTextureStreamingLogicalHeight);
            }
            else if (Data.SparseTextureStreamingEnabled)
            {
                Debug.OpenGLWarning(
                    $"[GLTexture2D] Clearing incomplete sparse state before storage allocation for '{GetDescribingName()}': " +
                    $"residentDims={width}x{height} residentBase={Data.SparseTextureStreamingResidentBaseMipLevel} " +
                    $"logicalDims={Data.SparseTextureStreamingLogicalWidth}x{Data.SparseTextureStreamingLogicalHeight} " +
                    $"logicalMipCount={Data.SparseTextureStreamingLogicalMipCount}.");
                Data.ClearSparseTextureStreamingState();
            }

            // CRITICAL: `Data.SmallestMipmapLevel` is clamped by `Data.SmallestAllowedMipmapLevel`.
            // The progressive streaming coroutine pins `SmallestAllowedMipmapLevel = lockMipLevel`
            // while seeding, and that clamp occurs BEFORE the first PushMipLevel call that triggers
            // this allocation — so without the floor below, immutable storage would be sized for
            // only `lockMipLevel + 1` levels instead of the full resident mip chain. That forces
            // higher-mip uploads to silently fall outside allocated storage (driver may later
            // return content from adjacent texture memory when those levels are sampled).
            // Use the full Mipmaps count as a floor so storage always covers every resident mip.
            int requestedLevelsBeforeMipFloor = requestedLevels;
            if (mipmapCount > requestedLevels)
                requestedLevels = mipmapCount;
            if (sparseLogicalAllocation)
            {
                int residentBaseMipLevel = SparseTextureResidentBaseMipLevelOrZero;
                int residentMipCount = Math.Max(0, Mipmaps?.Length ?? 0);
                int logicalMipCount = Math.Max(0, Data.SparseTextureStreamingLogicalMipCount);

                // Sparse uploads target logical mip indices (resident base + local mip index).
                // Storage must include the full addressed level range, not just resident mip count.
                int requiredSparseLevels = logicalMipCount > 0
                    ? logicalMipCount
                    : residentBaseMipLevel + residentMipCount;
                requestedLevels = Math.Max(requestedLevels, Math.Max(1, requiredSparseLevels));
            }

            if (requestedLevels != requestedLevelsBeforeMipFloor)
            {
                Debug.OpenGL(
                    $"[GLTexture2D] Storage level count raised by mipmap-chain floor for '{GetDescribingName()}': " +
                    $"naturalSmallestMip={naturalSmallestMipIndex} mipmapCount={mipmapCount} " +
                    $"SmallestAllowedMipmapLevel={Data.SmallestAllowedMipmapLevel} LargestMipmapLevel={Data.LargestMipmapLevel} " +
                    $"StreamingLockMipLevel={Data.StreamingLockMipLevel} " +
                    $"requestedLevelsBefore={requestedLevelsBeforeMipFloor} requestedLevelsAfter={requestedLevels} dims={width}x{height} " +
                    $"sparseEnabled={Data.SparseTextureStreamingEnabled}.");
            }

            uint levels = (uint)requestedLevels;
            uint legalLevels = GetLegalMipLevelCount(width, height);
            if (levels > legalLevels && !Data.UsesOpenGlExternalMemoryImport)
            {
                Debug.OpenGLWarning(
                    $"[GLTexture2D] Clamping storage levels for '{GetDescribingName()}' from {levels} to {legalLevels}: " +
                    $"dims={width}x{height} mipmapCount={mipmapCount} sparseEnabled={Data.SparseTextureStreamingEnabled} " +
                    $"logicalDims={Data.SparseTextureStreamingLogicalWidth}x{Data.SparseTextureStreamingLogicalHeight} " +
                    $"logicalMipCount={Data.SparseTextureStreamingLogicalMipCount} residentBase={Data.SparseTextureStreamingResidentBaseMipLevel}.");
                levels = legalLevels;
            }
            if (Data.UsesOpenGlExternalMemoryImport)
            {
                uint importedLevels = Math.Max(1u, Data.OpenGlExternalMemoryImportMipLevels);
/*
                if (levels != importedLevels)
                {
                    Debug.OpenGL(
                        $"[GLTexture2D] Clamping external-memory import levels for '{GetDescribingName()}' from {levels} to {importedLevels}. " +
                        $"ImportLabel={Data.OpenGlExternalMemoryLabel ?? Data.Name ?? BindingId.ToString()} dims={width}x{height}.");
                }
*/
                levels = importedLevels;
            }

            long requestedBytes = CalculateTextureVRAMSize(width, height, levels, Data.SizedInternalFormat, Data.MultiSample ? Data.MultiSampleCount : 1u);
            if (!Engine.Rendering.Stats.CanAllocateVram(requestedBytes, _allocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
            {
                Debug.OpenGLWarning($"[VRAM Budget] Skipping 2D texture allocation for '{Data.Name ?? BindingId.ToString()}' ({requestedBytes} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes.");
                return null;
            }

            if (_allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }

            if (_externalMemoryObject != 0)
            {
                Renderer.DeleteMemoryObject(_externalMemoryObject);
                _externalMemoryObject = 0;
            }

            if (Data.UsesOpenGlExternalMemoryImport)
            {
                unsafe
                {
                    _externalMemoryObject = Renderer.CreateImportedMemoryObject(
                        Data.OpenGlExternalMemoryImportSize,
                        (void*)Data.OpenGlExternalMemoryImportHandle);
                }

                if (_externalMemoryObject == 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to import external memory for texture '{Data.OpenGlExternalMemoryLabel ?? Data.Name ?? BindingId.ToString()}'.");
                }

                Silk.NET.OpenGLES.SizedInternalFormat sizedInternalFormat = (Silk.NET.OpenGLES.SizedInternalFormat)(uint)ToGLEnum(Data.SizedInternalFormat);
                if (Data.MultiSample)
                {
                    Renderer.EXTMemoryObject?.TextureStorageMem2DMultisample(
                        BindingId,
                        Data.MultiSampleCount,
                        sizedInternalFormat,
                        width,
                        height,
                        Data.FixedSampleLocations,
                        _externalMemoryObject,
                        0);
                }
                else
                {
                    Renderer.EXTMemoryObject?.TextureStorageMem2D(
                        BindingId,
                        levels,
                        sizedInternalFormat,
                        width,
                        height,
                        _externalMemoryObject,
                        0);
                }
            }
            else if (Data.MultiSample)
            {
                Api.TextureStorage2DMultisample(BindingId, Data.MultiSampleCount, ToGLEnum(Data.SizedInternalFormat), width, height, Data.FixedSampleLocations);
            }
            else
            {
                Api.TextureStorage2D(BindingId, levels, ToGLEnum(Data.SizedInternalFormat), width, height);
            }

            internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
            StorageSet = true;
            _allocatedLevels = levels;
            _allocatedWidth = width;
            _allocatedHeight = height;
            _allocatedInternalFormat = Data.SizedInternalFormat;

            _allocatedVRAMBytes = CalculateTextureVRAMSize(width, height, levels, Data.SizedInternalFormat, Data.MultiSample ? Data.MultiSampleCount : 1u);
            Engine.Rendering.Stats.AddTextureAllocation(_allocatedVRAMBytes);
/*
            Debug.OpenGL(
                $"[GLTexture2D] Storage allocated for '{GetDescribingName()}': binding={BindingId} dims={width}x{height} levels={levels} " +
                $"format={Data.SizedInternalFormat} mipmapCount={mipmapCount} " +
                $"SmallestAllowedMipmapLevel={Data.SmallestAllowedMipmapLevel} LargestMipmapLevel={Data.LargestMipmapLevel} " +
                $"StreamingLockMipLevel={Data.StreamingLockMipLevel} sparseEnabled={Data.SparseTextureStreamingEnabled}.");
*/
        }

        return internalFormatForce;
    }

    private bool IsMipLevelInAllocatedRange(int glLevel)
        => glLevel >= 0 && glLevel < _allocatedLevels;

    private static uint GetLegalMipLevelCount(uint width, uint height)
    {
        uint largestDimension = Math.Max(1u, Math.Max(width, height));
        uint levels = 1u;
        while (largestDimension > 1u)
        {
            largestDimension >>= 1;
            levels++;
        }

        return levels;
    }

    /// <summary>
    /// Calculates the approximate VRAM size for a 2D texture including all mipmap levels.
    /// </summary>
    internal static long CalculateTextureVRAMSize(uint width, uint height, uint mipLevels, ESizedInternalFormat format, uint sampleCount)
    {
        long totalSize = 0;
        uint bytesPerPixel = GetBytesPerPixel(format);

        for (uint mip = 0; mip < mipLevels; mip++)
        {
            uint mipWidth = Math.Max(1u, width >> (int)mip);
            uint mipHeight = Math.Max(1u, height >> (int)mip);
            totalSize += mipWidth * mipHeight * bytesPerPixel * sampleCount;
        }

        return totalSize;
    }

    /// <summary>
    /// Returns the bytes per pixel for a given sized internal format.
    /// </summary>
    internal static uint GetBytesPerPixel(ESizedInternalFormat format)
    {
        return format switch
        {
            ESizedInternalFormat.R8 => 1,
            ESizedInternalFormat.R8Snorm => 1,
            ESizedInternalFormat.R16 => 2,
            ESizedInternalFormat.R16Snorm => 2,
            ESizedInternalFormat.Rg8 => 2,
            ESizedInternalFormat.Rg8Snorm => 2,
            ESizedInternalFormat.Rg16 => 4,
            ESizedInternalFormat.Rg16Snorm => 4,
            ESizedInternalFormat.Rgb8 => 3,
            ESizedInternalFormat.Rgb8Snorm => 3,
            ESizedInternalFormat.Rgb16Snorm => 6,
            ESizedInternalFormat.Rgba8 => 4,
            ESizedInternalFormat.Rgba8Snorm => 4,
            ESizedInternalFormat.Rgba16 => 8,
            ESizedInternalFormat.Srgb8 => 3,
            ESizedInternalFormat.Srgb8Alpha8 => 4,
            ESizedInternalFormat.R16f => 2,
            ESizedInternalFormat.Rg16f => 4,
            ESizedInternalFormat.Rgb16f => 6,
            ESizedInternalFormat.Rgba16f => 8,
            ESizedInternalFormat.R32f => 4,
            ESizedInternalFormat.Rg32f => 8,
            ESizedInternalFormat.Rgb32f => 12,
            ESizedInternalFormat.Rgba32f => 16,
            ESizedInternalFormat.R11fG11fB10f => 4,
            ESizedInternalFormat.Rgb9E5 => 4,
            ESizedInternalFormat.R8i => 1,
            ESizedInternalFormat.R8ui => 1,
            ESizedInternalFormat.R16i => 2,
            ESizedInternalFormat.R16ui => 2,
            ESizedInternalFormat.R32i => 4,
            ESizedInternalFormat.R32ui => 4,
            ESizedInternalFormat.Rg8i => 2,
            ESizedInternalFormat.Rg8ui => 2,
            ESizedInternalFormat.Rg16i => 4,
            ESizedInternalFormat.Rg16ui => 4,
            ESizedInternalFormat.Rg32i => 8,
            ESizedInternalFormat.Rg32ui => 8,
            ESizedInternalFormat.Rgb8i => 3,
            ESizedInternalFormat.Rgb8ui => 3,
            ESizedInternalFormat.Rgb16i => 6,
            ESizedInternalFormat.Rgb16ui => 6,
            ESizedInternalFormat.Rgb32i => 12,
            ESizedInternalFormat.Rgb32ui => 12,
            ESizedInternalFormat.Rgba8i => 4,
            ESizedInternalFormat.Rgba8ui => 4,
            ESizedInternalFormat.Rgba16i => 8,
            ESizedInternalFormat.Rgba16ui => 8,
            ESizedInternalFormat.Rgba32i => 16,
            ESizedInternalFormat.Rgba32ui => 16,
            ESizedInternalFormat.DepthComponent16 => 2,
            ESizedInternalFormat.DepthComponent24 => 3,
            ESizedInternalFormat.DepthComponent32f => 4,
            ESizedInternalFormat.Depth24Stencil8 => 4,
            ESizedInternalFormat.Depth32fStencil8 => 5,
            ESizedInternalFormat.StencilIndex8 => 1,
            _ => 4, // Default assumption for unknown/other formats.
        };
    }
}
