using System.Threading;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Coordinates rendering statistics tracked by feature-specific modules.
            /// </summary>
            public static partial class Stats
            {
                /// <summary>
                /// When false, disables all per-frame statistics tracking to reduce overhead.
                /// VRAM tracking remains enabled as it's not per-frame.
                /// </summary>
                public static bool EnableTracking { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif

                /// <summary>
                /// Call this at the start of each frame to publish the previous frame's counters.
                /// </summary>
                public static void BeginFrame()
                {
                    bool gpuPipelineProfilingEnabled = EnableTracking && Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
                    RenderPipelineGpuProfiler.Instance.BeginFrame(State.RenderFrameId, gpuPipelineProfilingEnabled);

                    GpuDispatchLogger.BeginFrame();
                    XREngine.Rendering.Occlusion.OcclusionTelemetry.BeginFrame();

                    Frame.SnapshotAndReset();
                    GpuFallback.SnapshotAndReset();
                    GpuTransparency.SnapshotAndReset();
                    GpuMeshlets.SnapshotAndReset();
                    GpuReadback.SnapshotAndReset();
                    RtxIo.SnapshotAndReset();
                    Vulkan.SnapshotAndReset();
                    Vr.SnapshotAndReset();
                    Vram.SnapshotAndReset();

#if !XRE_PUBLISHED
                    Engine.ProfileCapture.RecordRenderStatsSnapshot();
#endif

                    // Render-matrix, skinned-bounds, and octree stats are swapped separately during SwapBuffers.
                }

                public static class Frame
                {
                    private static int _drawCalls;
                    private static int _trianglesRendered;
                    private static int _multiDrawCalls;
                    private static int _lastFrameDrawCalls;
                    private static int _lastFrameTrianglesRendered;
                    private static int _lastFrameMultiDrawCalls;

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

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameDrawCalls = Interlocked.Exchange(ref _drawCalls, 0);
                        _lastFrameTrianglesRendered = Interlocked.Exchange(ref _trianglesRendered, 0);
                        _lastFrameMultiDrawCalls = Interlocked.Exchange(ref _multiDrawCalls, 0);
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
                }
            }
        }
    }
}
