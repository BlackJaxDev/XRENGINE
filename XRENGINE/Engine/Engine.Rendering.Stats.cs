using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Contains rendering statistics tracked per frame.
            /// </summary>
            public static class Stats
            {
                private static int _drawCalls;
                private static int _trianglesRendered;
                private static int _multiDrawCalls;
                private static int _lastFrameDrawCalls;
                private static int _lastFrameTrianglesRendered;
                private static int _lastFrameMultiDrawCalls;

                // VRAM tracking fields
                private static long _allocatedVRAMBytes;
                private static long _allocatedBufferBytes;
                private static long _allocatedTextureBytes;
                private static long _allocatedRenderBufferBytes;

                // FBO bandwidth tracking fields (per-frame)
                private static long _fboBandwidthBytes;
                private static int _fboBindCount;
                private static long _lastFrameFBOBandwidthBytes;
                private static int _lastFrameFBOBindCount;

                /// <summary>
                /// The number of draw calls in the last completed frame.
                /// </summary>
                public static int DrawCalls => _lastFrameDrawCalls;

                /// <summary>
                /// The number of triangles rendered in the last completed frame.
                /// </summary>
                public static int TrianglesRendered => _lastFrameTrianglesRendered;

                /// <summary>
                /// The number of multi-draw indirect calls in the last completed frame.
                /// </summary>
                public static int MultiDrawCalls => _lastFrameMultiDrawCalls;

                /// <summary>
                /// Total currently allocated GPU VRAM in bytes.
                /// </summary>
                public static long AllocatedVRAMBytes => Interlocked.Read(ref _allocatedVRAMBytes);

                /// <summary>
                /// Currently allocated GPU buffer memory in bytes.
                /// </summary>
                public static long AllocatedBufferBytes => Interlocked.Read(ref _allocatedBufferBytes);

                /// <summary>
                /// Currently allocated GPU texture memory in bytes.
                /// </summary>
                public static long AllocatedTextureBytes => Interlocked.Read(ref _allocatedTextureBytes);

                /// <summary>
                /// Currently allocated GPU render buffer memory in bytes.
                /// </summary>
                public static long AllocatedRenderBufferBytes => Interlocked.Read(ref _allocatedRenderBufferBytes);

                /// <summary>
                /// Total currently allocated GPU VRAM in megabytes.
                /// </summary>
                public static double AllocatedVRAMMB => AllocatedVRAMBytes / (1024.0 * 1024.0);

                /// <summary>
                /// Total FBO render bandwidth in bytes for the last completed frame.
                /// This represents the total size of all render targets written to during rendering.
                /// </summary>
                public static long FBOBandwidthBytes => _lastFrameFBOBandwidthBytes;

                /// <summary>
                /// Total FBO render bandwidth in megabytes for the last completed frame.
                /// </summary>
                public static double FBOBandwidthMB => _lastFrameFBOBandwidthBytes / (1024.0 * 1024.0);

                /// <summary>
                /// Number of times FBOs were bound for writing in the last completed frame.
                /// </summary>
                public static int FBOBindCount => _lastFrameFBOBindCount;

                /// <summary>
                /// Call this at the start of each frame to reset the counters.
                /// </summary>
                public static void BeginFrame()
                {
                    // Notify GPU dispatch logger of new frame for logging context
                    GpuDispatchLogger.BeginFrame();
                    
                    _lastFrameDrawCalls = _drawCalls;
                    _lastFrameTrianglesRendered = _trianglesRendered;
                    _lastFrameMultiDrawCalls = _multiDrawCalls;
                    _lastFrameFBOBandwidthBytes = _fboBandwidthBytes;
                    _lastFrameFBOBindCount = _fboBindCount;

                    _drawCalls = 0;
                    _trianglesRendered = 0;
                    _multiDrawCalls = 0;
                    _fboBandwidthBytes = 0;
                    _fboBindCount = 0;
                }

                /// <summary>
                /// Increment the draw call counter.
                /// </summary>
                public static void IncrementDrawCalls()
                {
                    Interlocked.Increment(ref _drawCalls);
                }

                /// <summary>
                /// Increment the draw call counter by a specific amount.
                /// </summary>
                public static void IncrementDrawCalls(int count)
                {
                    Interlocked.Add(ref _drawCalls, count);
                }

                /// <summary>
                /// Add to the triangles rendered counter.
                /// </summary>
                public static void AddTrianglesRendered(int count)
                {
                    Interlocked.Add(ref _trianglesRendered, count);
                }

                /// <summary>
                /// Increment the multi-draw indirect call counter.
                /// </summary>
                public static void IncrementMultiDrawCalls()
                {
                    Interlocked.Increment(ref _multiDrawCalls);
                }

                /// <summary>
                /// Increment the multi-draw indirect call counter by a specific amount.
                /// </summary>
                public static void IncrementMultiDrawCalls(int count)
                {
                    Interlocked.Add(ref _multiDrawCalls, count);
                }

                /// <summary>
                /// Record a GPU buffer memory allocation.
                /// </summary>
                /// <param name="bytes">The number of bytes allocated.</param>
                public static void AddBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedBufferBytes, bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                }

                /// <summary>
                /// Record a GPU buffer memory deallocation.
                /// </summary>
                /// <param name="bytes">The number of bytes deallocated.</param>
                public static void RemoveBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedBufferBytes, -bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                }

                /// <summary>
                /// Record a GPU texture memory allocation.
                /// </summary>
                /// <param name="bytes">The number of bytes allocated.</param>
                public static void AddTextureAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedTextureBytes, bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                }

                /// <summary>
                /// Record a GPU texture memory deallocation.
                /// </summary>
                /// <param name="bytes">The number of bytes deallocated.</param>
                public static void RemoveTextureAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedTextureBytes, -bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                }

                /// <summary>
                /// Record a GPU render buffer memory allocation.
                /// </summary>
                /// <param name="bytes">The number of bytes allocated.</param>
                public static void AddRenderBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedRenderBufferBytes, bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                }

                /// <summary>
                /// Record a GPU render buffer memory deallocation.
                /// </summary>
                /// <param name="bytes">The number of bytes deallocated.</param>
                public static void RemoveRenderBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedRenderBufferBytes, -bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                }

                /// <summary>
                /// Record FBO render bandwidth when an FBO is bound for writing.
                /// The bandwidth is calculated as the total size of all render target attachments.
                /// </summary>
                /// <param name="bytes">The total size of all render target attachments in bytes.</param>
                public static void AddFBOBandwidth(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _fboBandwidthBytes, bytes);
                    Interlocked.Increment(ref _fboBindCount);
                }

                /// <summary>
                /// Gets the bytes per pixel for a given sized internal format.
                /// </summary>
                public static int GetBytesPerPixel(ESizedInternalFormat format)
                {
                    return format switch
                    {
                        // 1-byte formats
                        ESizedInternalFormat.R8 => 1,
                        ESizedInternalFormat.R8Snorm => 1,
                        ESizedInternalFormat.R8i => 1,
                        ESizedInternalFormat.R8ui => 1,
                        ESizedInternalFormat.StencilIndex8 => 1,

                        // 2-byte formats
                        ESizedInternalFormat.R16 => 2,
                        ESizedInternalFormat.R16Snorm => 2,
                        ESizedInternalFormat.R16f => 2,
                        ESizedInternalFormat.R16i => 2,
                        ESizedInternalFormat.R16ui => 2,
                        ESizedInternalFormat.Rg8 => 2,
                        ESizedInternalFormat.Rg8Snorm => 2,
                        ESizedInternalFormat.Rg8i => 2,
                        ESizedInternalFormat.Rg8ui => 2,
                        ESizedInternalFormat.DepthComponent16 => 2,

                        // 3-byte formats
                        ESizedInternalFormat.Rgb8 => 3,
                        ESizedInternalFormat.Rgb8Snorm => 3,
                        ESizedInternalFormat.Srgb8 => 3,
                        ESizedInternalFormat.Rgb8i => 3,
                        ESizedInternalFormat.Rgb8ui => 3,
                        ESizedInternalFormat.DepthComponent24 => 3,

                        // 4-byte formats
                        ESizedInternalFormat.R32f => 4,
                        ESizedInternalFormat.R32i => 4,
                        ESizedInternalFormat.R32ui => 4,
                        ESizedInternalFormat.Rg16 => 4,
                        ESizedInternalFormat.Rg16Snorm => 4,
                        ESizedInternalFormat.Rg16f => 4,
                        ESizedInternalFormat.Rg16i => 4,
                        ESizedInternalFormat.Rg16ui => 4,
                        ESizedInternalFormat.Rgba8 => 4,
                        ESizedInternalFormat.Rgba8Snorm => 4,
                        ESizedInternalFormat.Srgb8Alpha8 => 4,
                        ESizedInternalFormat.Rgba8i => 4,
                        ESizedInternalFormat.Rgba8ui => 4,
                        ESizedInternalFormat.Rgb10A2 => 4,
                        ESizedInternalFormat.R11fG11fB10f => 4,
                        ESizedInternalFormat.Rgb9E5 => 4,
                        ESizedInternalFormat.DepthComponent32f => 4,
                        ESizedInternalFormat.Depth24Stencil8 => 4,

                        // 5-byte formats
                        ESizedInternalFormat.Depth32fStencil8 => 5,

                        // 6-byte formats
                        ESizedInternalFormat.Rgb16f => 6,
                        ESizedInternalFormat.Rgb16Snorm => 6,
                        ESizedInternalFormat.Rgb16i => 6,
                        ESizedInternalFormat.Rgb16ui => 6,

                        // 8-byte formats
                        ESizedInternalFormat.Rg32f => 8,
                        ESizedInternalFormat.Rg32i => 8,
                        ESizedInternalFormat.Rg32ui => 8,
                        ESizedInternalFormat.Rgba16 => 8,
                        ESizedInternalFormat.Rgba16f => 8,
                        ESizedInternalFormat.Rgba16i => 8,
                        ESizedInternalFormat.Rgba16ui => 8,

                        // 12-byte formats
                        ESizedInternalFormat.Rgb32f => 12,
                        ESizedInternalFormat.Rgb32i => 12,
                        ESizedInternalFormat.Rgb32ui => 12,

                        // 16-byte formats
                        ESizedInternalFormat.Rgba32f => 16,
                        ESizedInternalFormat.Rgba32i => 16,
                        ESizedInternalFormat.Rgba32ui => 16,

                        // Default fallback (estimate 4 bytes for unknown formats)
                        _ => 4
                    };
                }

                /// <summary>
                /// Gets the bytes per pixel for a given render buffer storage format.
                /// </summary>
                public static int GetBytesPerPixel(ERenderBufferStorage format)
                {
                    return format switch
                    {
                        // 1-byte formats
                        ERenderBufferStorage.R8 => 1,
                        ERenderBufferStorage.R8i => 1,
                        ERenderBufferStorage.R8ui => 1,
                        ERenderBufferStorage.StencilIndex1 => 1,
                        ERenderBufferStorage.StencilIndex4 => 1,
                        ERenderBufferStorage.StencilIndex8 => 1,

                        // 2-byte formats
                        ERenderBufferStorage.R16 => 2,
                        ERenderBufferStorage.R16f => 2,
                        ERenderBufferStorage.R16i => 2,
                        ERenderBufferStorage.R16ui => 2,
                        ERenderBufferStorage.DepthComponent16 => 2,
                        ERenderBufferStorage.StencilIndex16 => 2,

                        // 3-byte formats
                        ERenderBufferStorage.Rgb8 => 3,
                        ERenderBufferStorage.Srgb8 => 3,
                        ERenderBufferStorage.Rgb8i => 3,
                        ERenderBufferStorage.Rgb8ui => 3,
                        ERenderBufferStorage.DepthComponent24 => 3,

                        // 4-byte formats
                        ERenderBufferStorage.R32f => 4,
                        ERenderBufferStorage.R32i => 4,
                        ERenderBufferStorage.R32ui => 4,
                        ERenderBufferStorage.Rgba8 => 4,
                        ERenderBufferStorage.Srgb8Alpha8 => 4,
                        ERenderBufferStorage.Rgba8i => 4,
                        ERenderBufferStorage.Rgba8ui => 4,
                        ERenderBufferStorage.Rgb10A2 => 4,
                        ERenderBufferStorage.Rgb10A2ui => 4,
                        ERenderBufferStorage.R11fG11fB10f => 4,
                        ERenderBufferStorage.Rgb9E5 => 4,
                        ERenderBufferStorage.DepthComponent32 => 4,
                        ERenderBufferStorage.DepthComponent32f => 4,
                        ERenderBufferStorage.Depth24Stencil8 => 4,
                        ERenderBufferStorage.DepthComponent => 4,
                        ERenderBufferStorage.DepthStencil => 4,

                        // 5-byte formats
                        ERenderBufferStorage.Depth32fStencil8 => 5,

                        // 6-byte formats
                        ERenderBufferStorage.Rgb16 => 6,
                        ERenderBufferStorage.Rgb16f => 6,
                        ERenderBufferStorage.Rgb16i => 6,
                        ERenderBufferStorage.Rgb16ui => 6,

                        // 8-byte formats
                        ERenderBufferStorage.Rgba16 => 8,
                        ERenderBufferStorage.Rgba16f => 8,
                        ERenderBufferStorage.Rgba16i => 8,
                        ERenderBufferStorage.Rgba16ui => 8,

                        // 12-byte formats
                        ERenderBufferStorage.Rgb32f => 12,
                        ERenderBufferStorage.Rgb32i => 12,
                        ERenderBufferStorage.Rgb32ui => 12,

                        // 16-byte formats
                        ERenderBufferStorage.Rgba32f => 16,
                        ERenderBufferStorage.Rgba32i => 16,
                        ERenderBufferStorage.Rgba32ui => 16,

                        // Default fallback (estimate 4 bytes for unknown formats)
                        _ => 4
                    };
                }
            }
        }
    }
}
