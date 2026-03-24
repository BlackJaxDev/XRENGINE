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
            /// Tracks how many times a renderer has been processed without becoming IsGenerated.
            /// Prevents infinite re-enqueue when Generate() silently fails.
            /// </summary>
            private readonly ConcurrentDictionary<GLMeshRenderer, int> _failureCount = new();

            // Log rate-limiting state
            private long _totalGenerated;
            private long _totalFailed;
            private int _lastLoggedRemaining;
            private double _timeSinceLastLogMs;
            private readonly Stopwatch _logTimer = Stopwatch.StartNew();
            private const double LogIntervalMs = 2000.0; // Log at most every 2 seconds during steady state
            private const int RemainingChangeThreshold = 20; // Log immediately if remaining changes by this much

            /// <summary>
            /// Maximum number of generation attempts per renderer before giving up.
            /// The renderer is retried from scratch when its mesh data genuinely changes.
            /// </summary>
            public int MaxRetries { get; set; } = 3;

            /// <summary>
            /// Maximum milliseconds to spend on mesh generation per frame.
            /// </summary>
            public double FrameBudgetMs { get; set; } = 4.0;

            private double _savedBudgetMs;
            private volatile bool _budgetBoosted;

            /// <summary>
            /// Temporarily increases <see cref="FrameBudgetMs"/> to drain a large backlog
            /// faster (e.g. after bulk-spawning many mesh-bearing objects).
            /// The original budget is automatically restored when the pending queue empties.
            /// Safe to call from any thread.
            /// </summary>
            public void BoostBudgetUntilDrained(double boostedMs)
            {
                if (_budgetBoosted)
                    return;

                _savedBudgetMs = FrameBudgetMs;
                FrameBudgetMs = boostedMs;
                _budgetBoosted = true;
            }

            /// <summary>
            /// Whether frame-budgeted generation is enabled.
            /// When disabled, generation happens immediately from the draw path except during shadow passes,
            /// where cold-start generation is always deferred to avoid multiplying startup stalls.
            /// </summary>
            public bool Enabled { get; set; } = true;

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
            /// Returns true if the mesh was enqueued, false if it's already pending, already generated,
            /// or has exceeded its retry limit.
            /// </summary>
            public bool EnqueueGeneration(GLMeshRenderer renderer)
            {
                if (renderer.IsGenerated || _pendingSet.ContainsKey(renderer))
                    return false;

                // Don't re-enqueue renderers that have repeatedly failed to generate.
                if (_failureCount.TryGetValue(renderer, out int count) && count >= MaxRetries)
                    return false;

                if (_pendingSet.TryAdd(renderer, 0))
                {
                    _pendingGeneration.Enqueue(renderer);
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Resets the failure counter for a renderer, allowing it to be re-enqueued.
            /// Call this when mesh data genuinely changes (e.g., from DataChanged).
            /// </summary>
            public void ResetRetries(GLMeshRenderer renderer)
            {
                _failureCount.TryRemove(renderer, out _);
            }

            /// <summary>
            /// Processes pending mesh generations within the frame budget.
            /// Call this once per frame from the render thread.
            /// </summary>
            public void ProcessGeneration()
            {
                if (!Enabled)
                    return;

                if (_pendingGeneration.IsEmpty)
                    return;

                var sw = Stopwatch.StartNew();
                double budgetMs = FrameBudgetMs;
                int generated = 0;
                int failed = 0;

                while (sw.Elapsed.TotalMilliseconds < budgetMs && _pendingGeneration.TryDequeue(out var renderer))
                {
                    _pendingSet.TryRemove(renderer, out _);
                    
                    // Skip if already generated (might have been generated through another path)
                    if (renderer.IsGenerated)
                    {
                        _failureCount.TryRemove(renderer, out _);
                        continue;
                    }

                    // Skip if destroyed or data is null
                    if (renderer.Data is null)
                        continue;

                    try
                    {
                        renderer.Generate();

                        if (renderer.IsGenerated)
                        {
                            generated++;
                            _failureCount.TryRemove(renderer, out _);
                        }
                        else
                        {
                            // Generate() returned without exception but the object is still not valid.
                            int failures = _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                            failed++;
                            if (failures >= MaxRetries)
                                Debug.OpenGLWarning($"[GLMeshGenerationQueue] Giving up on mesh renderer after {failures} failed generation attempts: {renderer.GetDescribingName()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        int failures = _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                        failed++;
                        Debug.OpenGLException(ex, $"GLMeshGenerationQueue: Failed to generate mesh renderer (attempt {failures}/{MaxRetries})");
                    }
                }

                if (_budgetBoosted && _pendingGeneration.IsEmpty)
                {
                    FrameBudgetMs = _savedBudgetMs;
                    _budgetBoosted = false;
                }

                if (generated > 0 || failed > 0)
                {
                    _totalGenerated += generated;
                    _totalFailed += failed;
                    int remaining = _pendingGeneration.Count;
                    double elapsed = _logTimer.Elapsed.TotalMilliseconds;

                    // Log when: queue empties, failures occur, remaining changes significantly, or periodic interval
                    bool shouldLog = remaining == 0
                        || failed > 0
                        || Math.Abs(remaining - _lastLoggedRemaining) >= RemainingChangeThreshold
                        || elapsed - _timeSinceLastLogMs >= LogIntervalMs;

                    if (shouldLog)
                    {
                        Debug.OpenGL($"[GLMeshGenerationQueue] Generated {generated} mesh(es){(failed > 0 ? $", {failed} failed" : "")} in {sw.Elapsed.TotalMilliseconds:F2}ms, {remaining} remaining (total: {_totalGenerated} ok, {_totalFailed} failed)");
                        _lastLoggedRemaining = remaining;
                        _timeSinceLastLogMs = elapsed;
                    }
                }
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
                            if (renderer.IsGenerated)
                                _failureCount.TryRemove(renderer, out _);
                            else
                                _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                        }
                        catch (Exception ex)
                        {
                            _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                            Debug.OpenGLException(ex, $"GLMeshGenerationQueue: Failed to generate mesh renderer during flush");
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
