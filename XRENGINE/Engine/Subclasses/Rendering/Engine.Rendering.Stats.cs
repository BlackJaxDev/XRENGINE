using System.Collections.Generic;
using System;
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
                private static int _gpuCpuFallbackEvents;
                private static int _gpuCpuFallbackRecoveredCommands;
                private static int _lastFrameGpuCpuFallbackEvents;
                private static int _lastFrameGpuCpuFallbackRecoveredCommands;

                // GPU->CPU readback / mapping counters (per-frame)
                private static int _gpuMappedBuffers;
                private static long _gpuReadbackBytes;
                private static int _lastFrameGpuMappedBuffers;
                private static long _lastFrameGpuReadbackBytes;
                private static int _vrLeftEyeDraws;
                private static int _vrRightEyeDraws;
                private static int _lastFrameVrLeftEyeDraws;
                private static int _lastFrameVrRightEyeDraws;
                private static int _vrLeftEyeVisible;
                private static int _vrRightEyeVisible;
                private static int _lastFrameVrLeftEyeVisible;
                private static int _lastFrameVrRightEyeVisible;
                private static long _vrLeftWorkerBuildTimeTicks;
                private static long _vrRightWorkerBuildTimeTicks;
                private static long _lastFrameVrLeftWorkerBuildTimeTicks;
                private static long _lastFrameVrRightWorkerBuildTimeTicks;
                private static long _vrRenderSubmitTimeTicks;
                private static long _lastFrameVrRenderSubmitTimeTicks;

                // Render-matrix stats use a separate swap cycle aligned with SwapBuffers phase.
                // Current = being written now, Display = last completed swap, Ready = waiting to become Display.
                private static int _renderMatrixAppliedCurrent;
                private static int _renderMatrixSetCallsCurrent;
                private static int _renderMatrixListenerInvocationsCurrent;
                private static int _renderMatrixAppliedDisplay;
                private static int _renderMatrixSetCallsDisplay;
                private static int _renderMatrixListenerInvocationsDisplay;
                private static readonly object _renderMatrixStatsLock = new();
                private static Dictionary<string, int> _renderMatrixListenerCountsCurrent = new(StringComparer.Ordinal);
                private static Dictionary<string, int> _renderMatrixListenerCountsDisplay = new(StringComparer.Ordinal);
                private static bool _renderMatrixStatsReady;
                private static int _renderMatrixStatsDirty;

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
                /// Number of GPU->CPU culling fallback events in the last completed frame.
                /// </summary>
                public static int GpuCpuFallbackEvents => _lastFrameGpuCpuFallbackEvents;

                /// <summary>
                /// Number of commands recovered by GPU->CPU fallback in the last completed frame.
                /// </summary>
                public static int GpuCpuFallbackRecoveredCommands => _lastFrameGpuCpuFallbackRecoveredCommands;

                /// <summary>
                /// Number of GPU buffers mapped for CPU access in the last completed frame.
                /// </summary>
                public static int GpuMappedBuffers => _lastFrameGpuMappedBuffers;

                /// <summary>
                /// Total bytes read back from GPU buffers in the last completed frame.
                /// </summary>
                public static long GpuReadbackBytes => _lastFrameGpuReadbackBytes;
                public static int VrLeftEyeDraws => _lastFrameVrLeftEyeDraws;
                public static int VrRightEyeDraws => _lastFrameVrRightEyeDraws;
                public static int VrLeftEyeVisible => _lastFrameVrLeftEyeVisible;
                public static int VrRightEyeVisible => _lastFrameVrRightEyeVisible;
                public static double VrLeftWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrLeftWorkerBuildTimeTicks).TotalMilliseconds;
                public static double VrRightWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrRightWorkerBuildTimeTicks).TotalMilliseconds;
                public static double VrRenderSubmitTimeMs => TimeSpan.FromTicks(_lastFrameVrRenderSubmitTimeTicks).TotalMilliseconds;

                /// <summary>
                /// Enables collection of render-matrix statistics.
                /// </summary>
                public static bool EnableRenderMatrixStats { get; set; }

                /// <summary>
                /// Enables detailed render-matrix listener tracking (per listener type).
                /// </summary>
                public static bool EnableRenderMatrixListenerTracking { get; set; }

                /// <summary>
                /// When false, disables all per-frame statistics tracking to reduce overhead.
                /// VRAM tracking remains enabled as it's not per-frame.
                /// </summary>
                public static bool EnableTracking { get; set; } = true;

                /// <summary>
                /// Whether render-matrix stats have been populated at least once.
                /// </summary>
                public static bool RenderMatrixStatsReady => _renderMatrixStatsReady;

                /// <summary>
                /// Number of render-matrix updates applied in the last completed frame.
                /// </summary>
                public static int RenderMatrixApplied => _renderMatrixAppliedDisplay;

                /// <summary>
                /// Number of SetRenderMatrix calls in the last completed frame.
                /// </summary>
                public static int RenderMatrixSetCalls => _renderMatrixSetCallsDisplay;

                /// <summary>
                /// Total number of render-matrix listener invocations in the last completed frame.
                /// </summary>
                public static int RenderMatrixListenerInvocations => _renderMatrixListenerInvocationsDisplay;

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
                    _lastFrameGpuCpuFallbackEvents = _gpuCpuFallbackEvents;
                    _lastFrameGpuCpuFallbackRecoveredCommands = _gpuCpuFallbackRecoveredCommands;
                    _lastFrameGpuMappedBuffers = _gpuMappedBuffers;
                    _lastFrameGpuReadbackBytes = _gpuReadbackBytes;
                    _lastFrameVrLeftEyeDraws = _vrLeftEyeDraws;
                    _lastFrameVrRightEyeDraws = _vrRightEyeDraws;
                    _lastFrameVrLeftEyeVisible = _vrLeftEyeVisible;
                    _lastFrameVrRightEyeVisible = _vrRightEyeVisible;
                    _lastFrameVrLeftWorkerBuildTimeTicks = _vrLeftWorkerBuildTimeTicks;
                    _lastFrameVrRightWorkerBuildTimeTicks = _vrRightWorkerBuildTimeTicks;
                    _lastFrameVrRenderSubmitTimeTicks = _vrRenderSubmitTimeTicks;
                    _lastFrameFBOBandwidthBytes = _fboBandwidthBytes;
                    _lastFrameFBOBindCount = _fboBindCount;

                    _drawCalls = 0;
                    _trianglesRendered = 0;
                    _multiDrawCalls = 0;
                    _gpuCpuFallbackEvents = 0;
                    _gpuCpuFallbackRecoveredCommands = 0;
                    _gpuMappedBuffers = 0;
                    _gpuReadbackBytes = 0;
                    _vrLeftEyeDraws = 0;
                    _vrRightEyeDraws = 0;
                    _vrLeftEyeVisible = 0;
                    _vrRightEyeVisible = 0;
                    _vrLeftWorkerBuildTimeTicks = 0;
                    _vrRightWorkerBuildTimeTicks = 0;
                    _vrRenderSubmitTimeTicks = 0;
                    _fboBandwidthBytes = 0;
                    _fboBindCount = 0;
                    // Note: render-matrix stats are swapped separately via SwapRenderMatrixStats()
                }

                /// <summary>
                /// Records that a GPU buffer was mapped for CPU access.
                /// </summary>
                public static void RecordGpuBufferMapped(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _gpuMappedBuffers, count);
                }

                /// <summary>
                /// Records the number of bytes read back from GPU buffers.
                /// </summary>
                public static void RecordGpuReadbackBytes(long bytes)
                {
                    if (!EnableTracking || bytes <= 0)
                        return;

                    Interlocked.Add(ref _gpuReadbackBytes, bytes);
                }

                public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftEyeDraws, (int)Math.Min(leftDraws, int.MaxValue));
                    Interlocked.Exchange(ref _vrRightEyeDraws, (int)Math.Min(rightDraws, int.MaxValue));
                }

                public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftEyeVisible, (int)Math.Min(leftVisible, int.MaxValue));
                    Interlocked.Exchange(ref _vrRightEyeVisible, (int)Math.Min(rightVisible, int.MaxValue));
                }

                public static void RecordVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftWorkerBuildTimeTicks, leftBuildTime.Ticks);
                    Interlocked.Exchange(ref _vrRightWorkerBuildTimeTicks, rightBuildTime.Ticks);
                }

                public static void RecordVrRenderSubmitTime(TimeSpan submitTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrRenderSubmitTimeTicks, submitTime.Ticks);
                }

                /// <summary>
                /// Swaps render-matrix stats from current to display buffer. Call from SwapBuffers phase.
                /// </summary>
                public static void SwapRenderMatrixStats()
                {
                    if (!EnableRenderMatrixStats)
                        return;

                    if (Interlocked.Exchange(ref _renderMatrixStatsDirty, 0) == 0)
                        return;

                    // Atomically copy current values to display and reset current.
                    _renderMatrixAppliedDisplay = Interlocked.Exchange(ref _renderMatrixAppliedCurrent, 0);
                    _renderMatrixSetCallsDisplay = Interlocked.Exchange(ref _renderMatrixSetCallsCurrent, 0);
                    _renderMatrixListenerInvocationsDisplay = Interlocked.Exchange(ref _renderMatrixListenerInvocationsCurrent, 0);

                    lock (_renderMatrixStatsLock)
                    {
                        var temp = _renderMatrixListenerCountsDisplay;
                        _renderMatrixListenerCountsDisplay = _renderMatrixListenerCountsCurrent;
                        _renderMatrixListenerCountsCurrent = temp;
                        _renderMatrixListenerCountsCurrent.Clear();
                    }

                    _renderMatrixStatsReady = true;
                }

                /// <summary>
                /// Record the number of render-matrix updates applied during swap buffers.
                /// </summary>
                public static void RecordRenderMatrixApplied(int count)
                {
                    if (!EnableRenderMatrixStats || count <= 0)
                        return;

                    Interlocked.Add(ref _renderMatrixAppliedCurrent, count);
                    Interlocked.Exchange(ref _renderMatrixStatsDirty, 1);
                }

                /// <summary>
                /// Record a render-matrix change event and (optionally) its listeners.
                /// </summary>
                public static void RecordRenderMatrixChange(Delegate? listeners)
                {
                    if (!EnableRenderMatrixStats)
                        return;

                    Interlocked.Increment(ref _renderMatrixSetCallsCurrent);
                    Interlocked.Exchange(ref _renderMatrixStatsDirty, 1);

                    if (!EnableRenderMatrixListenerTracking || listeners is null)
                        return;

                    var invocationList = listeners.GetInvocationList();
                    Interlocked.Add(ref _renderMatrixListenerInvocationsCurrent, invocationList.Length);

                    lock (_renderMatrixStatsLock)
                    {
                        foreach (var handler in invocationList)
                        {
                            var key = handler.Target?.GetType().Name ?? handler.Method.DeclaringType?.Name ?? "Static";
                            if (_renderMatrixListenerCountsCurrent.TryGetValue(key, out int current))
                                _renderMatrixListenerCountsCurrent[key] = current + 1;
                            else
                                _renderMatrixListenerCountsCurrent[key] = 1;
                        }
                    }
                }

                /// <summary>
                /// Returns the last-frame snapshot of render-matrix listener counts per listener type.
                /// </summary>
                public static KeyValuePair<string, int>[] GetRenderMatrixListenerSnapshot()
                {
                    lock (_renderMatrixStatsLock)
                    {
                        if (_renderMatrixListenerCountsDisplay.Count == 0)
                            return [];

                        var copy = new KeyValuePair<string, int>[_renderMatrixListenerCountsDisplay.Count];
                        int index = 0;
                        foreach (var pair in _renderMatrixListenerCountsDisplay)
                            copy[index++] = pair;
                        return copy;
                    }
                }

                // Octree stats
                private static int _octreeAddCommandsCurrent;
                private static int _octreeMoveCommandsCurrent;
                private static int _octreeRemoveCommandsCurrent;
                private static int _octreeSkippedMovesCurrent;
                private static int _octreeAddCommandsDisplay;
                private static int _octreeMoveCommandsDisplay;
                private static int _octreeRemoveCommandsDisplay;
                private static int _octreeSkippedMovesDisplay;
                private static int _octreeStatsDirty;
                private static bool _octreeStatsReady;

                public static bool EnableOctreeStats { get; set; }
                public static bool OctreeStatsReady => _octreeStatsReady;
                public static int OctreeAddCount => _octreeAddCommandsDisplay;
                public static int OctreeMoveCount => _octreeMoveCommandsDisplay;
                public static int OctreeRemoveCount => _octreeRemoveCommandsDisplay;
                public static int OctreeSkippedMoveCount => _octreeSkippedMovesDisplay;

                public static void RecordOctreeAdd()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeAddCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeMove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeMoveCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeRemove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeRemoveCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeSkippedMove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeSkippedMovesCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void SwapOctreeStats()
                {
                    if (!EnableOctreeStats) return;
                    if (Interlocked.Exchange(ref _octreeStatsDirty, 0) == 0) return;

                    _octreeAddCommandsDisplay = Interlocked.Exchange(ref _octreeAddCommandsCurrent, 0);
                    _octreeMoveCommandsDisplay = Interlocked.Exchange(ref _octreeMoveCommandsCurrent, 0);
                    _octreeRemoveCommandsDisplay = Interlocked.Exchange(ref _octreeRemoveCommandsCurrent, 0);
                    _octreeSkippedMovesDisplay = Interlocked.Exchange(ref _octreeSkippedMovesCurrent, 0);
                    _octreeStatsReady = true;
                }

                /// <summary>
                /// Increment the draw call counter.
                /// </summary>
                public static void IncrementDrawCalls()
                {
                    if (!EnableTracking) return;
                    Interlocked.Increment(ref _drawCalls);
                }

                /// <summary>
                /// Increment the draw call counter by a specific amount.
                /// </summary>
                public static void IncrementDrawCalls(int count)
                {
                    if (!EnableTracking) return;
                    Interlocked.Add(ref _drawCalls, count);
                }

                /// <summary>
                /// Add to the triangles rendered counter.
                /// </summary>
                public static void AddTrianglesRendered(int count)
                {
                    if (!EnableTracking) return;
                    Interlocked.Add(ref _trianglesRendered, count);
                }

                /// <summary>
                /// Increment the multi-draw indirect call counter.
                /// </summary>
                public static void IncrementMultiDrawCalls()
                {
                    if (!EnableTracking) return;
                    Interlocked.Increment(ref _multiDrawCalls);
                }

                /// <summary>
                /// Increment the multi-draw indirect call counter by a specific amount.
                /// </summary>
                public static void IncrementMultiDrawCalls(int count)
                {
                    if (!EnableTracking) return;
                    Interlocked.Add(ref _multiDrawCalls, count);
                }

                /// <summary>
                /// Records usage of GPU->CPU fallback recovery during culling.
                /// </summary>
                public static void RecordGpuCpuFallback(int eventCount, int recoveredCommands)
                {
                    if (!EnableTracking || eventCount <= 0)
                        return;

                    Interlocked.Add(ref _gpuCpuFallbackEvents, eventCount);
                    if (recoveredCommands > 0)
                        Interlocked.Add(ref _gpuCpuFallbackRecoveredCommands, recoveredCommands);
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
