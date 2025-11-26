using System.Threading;

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
                /// Call this at the start of each frame to reset the counters.
                /// </summary>
                public static void BeginFrame()
                {
                    _lastFrameDrawCalls = _drawCalls;
                    _lastFrameTrianglesRendered = _trianglesRendered;
                    _lastFrameMultiDrawCalls = _multiDrawCalls;
                    _drawCalls = 0;
                    _trianglesRendered = 0;
                    _multiDrawCalls = 0;
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
            }
        }
    }
}
