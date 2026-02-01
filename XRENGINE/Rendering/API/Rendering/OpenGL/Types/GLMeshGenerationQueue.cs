using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Manages frame-budgeted mesh generation to prevent FPS stalls when many meshes load at once.
    /// Spreads mesh initialization (VAO/VBO setup, shader compilation) across multiple frames.
    /// </summary>
    public unsafe partial class OpenGLRenderer
    {
        public sealed class GLMeshGenerationQueue
        {
            private readonly OpenGLRenderer _renderer;
            private readonly ConcurrentQueue<GLMeshRenderer> _pendingGeneration = new();
            private readonly ConcurrentDictionary<GLMeshRenderer, byte> _pendingSet = new();

            /// <summary>
            /// Maximum milliseconds to spend on mesh generation per frame.
            /// </summary>
            public double FrameBudgetMs { get; set; } = 4.0;

            /// <summary>
            /// Whether frame-budgeted generation is enabled.
            /// When disabled, generation happens immediately.
            /// Disabled by default as it can cause rendering issues - enable only for debugging load spikes.
            /// </summary>
            public bool Enabled { get; set; } = false;

            /// <summary>
            /// Number of meshes pending generation.
            /// </summary>
            public int PendingCount => _pendingGeneration.Count;

            /// <summary>
            /// Checks if a mesh renderer has pending generation.
            /// </summary>
            public bool HasPendingGeneration(GLMeshRenderer renderer)
                => _pendingSet.ContainsKey(renderer);

            public GLMeshGenerationQueue(OpenGLRenderer renderer)
            {
                _renderer = renderer;
            }

            /// <summary>
            /// Enqueues a mesh renderer for deferred generation.
            /// Returns true if the mesh was enqueued, false if it's already pending or already generated.
            /// </summary>
            public bool EnqueueGeneration(GLMeshRenderer renderer)
            {
                if (renderer.IsGenerated || _pendingSet.ContainsKey(renderer))
                    return false;

                if (_pendingSet.TryAdd(renderer, 0))
                {
                    _pendingGeneration.Enqueue(renderer);
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Processes pending mesh generations within the frame budget.
            /// Call this once per frame from the render thread.
            /// </summary>
            public void ProcessGeneration()
            {
                if (_pendingGeneration.IsEmpty)
                    return;

                var sw = Stopwatch.StartNew();
                double budgetMs = FrameBudgetMs;
                int generated = 0;

                while (sw.Elapsed.TotalMilliseconds < budgetMs && _pendingGeneration.TryDequeue(out var renderer))
                {
                    _pendingSet.TryRemove(renderer, out _);
                    
                    // Skip if already generated (might have been generated through another path)
                    if (renderer.IsGenerated)
                        continue;

                    // Skip if destroyed or data is null
                    if (renderer.Data is null)
                        continue;

                    try
                    {
                        renderer.Generate();
                        generated++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"GLMeshGenerationQueue: Failed to generate mesh renderer");
                    }
                }

                if (generated > 0)
                    Debug.Out($"[GLMeshGenerationQueue] Generated {generated} mesh(es) in {sw.Elapsed.TotalMilliseconds:F2}ms, {_pendingGeneration.Count} remaining");
            }

            /// <summary>
            /// Forces all pending generations to complete immediately.
            /// </summary>
            public void FlushAll()
            {
                while (_pendingGeneration.TryDequeue(out var renderer))
                {
                    _pendingSet.TryRemove(renderer, out _);
                    if (!renderer.IsGenerated && renderer.Data is not null)
                    {
                        try
                        {
                            renderer.Generate();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, $"GLMeshGenerationQueue: Failed to generate mesh renderer during flush");
                        }
                    }
                }
            }
        }

        private GLMeshGenerationQueue? _meshGenerationQueue;

        /// <summary>
        /// Gets the frame-budgeted mesh generation queue for this renderer.
        /// </summary>
        public GLMeshGenerationQueue MeshGenerationQueue => _meshGenerationQueue ??= new GLMeshGenerationQueue(this);
    }
}
