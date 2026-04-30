using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Manages frame-budgeted mesh generation to prevent FPS stalls when many meshes load at once.
    /// Spreads mesh initialization (VAO/VBO setup, shader compilation) across multiple frames.
    /// Render-pipeline meshes (FBO quads, cube maps) are processed first without a budget cap
    /// so that post-process / lighting passes warm instantly.
    /// </summary>
    public unsafe partial class OpenGLRenderer
    {
        public sealed class GLMeshGenerationQueue
        {
            private readonly OpenGLRenderer _renderer;

            /// <summary>Render-pipeline meshes: drained every frame with no budget limit.</summary>
            private readonly ConcurrentQueue<GLMeshRenderer> _priorityQueue = new();

            /// <summary>Normal scene meshes: drained within the per-frame budget.</summary>
            private readonly ConcurrentQueue<GLMeshRenderer> _normalQueue = new();

            /// <summary>Dedup set – value is unused, presence means "enqueued".</summary>
            private readonly ConcurrentDictionary<GLMeshRenderer, byte> _pendingSet = new();
            private readonly List<GLMeshRenderer> _deferredPriorityScratch = [];
            private readonly List<GLMeshRenderer> _deferredNormalScratch = [];

            /// <summary>
            /// Tracks how many times a renderer has been processed without becoming IsGenerated.
            /// Prevents infinite re-enqueue when Generate() silently fails.
            /// </summary>
            private readonly ConcurrentDictionary<GLMeshRenderer, int> _failureCount = new();
            private enum QueueProcessResult
            {
                NoWork,
                Completed,
                Pending,
                Failed,
            }

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
            public double FrameBudgetMs { get; set; } = 10.0;

            /// <summary>
            /// Hard cap for normal scene renderer preparation in one render frame.
            /// The time budget is checked between renderers, so a count cap prevents
            /// a large backlog from chaining several individually-expensive first-use preparations.
            /// </summary>
            public int MaxNormalRenderersPerFrame { get; set; } = 2;

            /// <summary>
            /// Hard cap for render-pipeline-priority renderer preparation when priority generation is throttled.
            /// </summary>
            public int MaxThrottledPriorityRenderersPerFrame { get; set; } = 4;

            private double _savedBudgetMs;
            private volatile bool _budgetBoosted;

            /// <summary>
            /// During startup budget boosts, even render-pipeline meshes should defer out of the draw path
            /// and respect the temporary frame budget instead of forcing inline generation.
            /// </summary>
            public bool ThrottlePriorityGeneration => _budgetBoosted;

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
            public int PendingCount => _priorityQueue.Count + _normalQueue.Count;

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
            /// Pipeline-priority meshes go into a fast lane that is drained every frame without a budget cap.
            /// Returns true if the mesh was newly enqueued.
            /// </summary>
            public bool EnqueueGeneration(GLMeshRenderer renderer)
            {
                if (renderer.IsPreparedForRendering || _pendingSet.ContainsKey(renderer))
                    return false;

                // Don't re-enqueue renderers that have repeatedly failed to generate.
                if (_failureCount.TryGetValue(renderer, out int count) && count >= MaxRetries)
                    return false;

                if (!_pendingSet.TryAdd(renderer, 0))
                    return false;

                if (renderer.MeshRenderer.GenerationPriority == EMeshGenerationPriority.RenderPipeline)
                    _priorityQueue.Enqueue(renderer);
                else
                    _normalQueue.Enqueue(renderer);

                return true;
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
            /// Render-pipeline meshes are drained first without any budget limit.
            /// Normal scene meshes are then processed within the remaining frame budget.
            /// Call this once per frame from the render thread.
            /// </summary>
            public void ProcessGeneration()
            {
                if (!Enabled)
                    return;

                if (_priorityQueue.IsEmpty && _normalQueue.IsEmpty)
                    return;

                // Bail if the GPU is in OOM recovery — generating more meshes would worsen VRAM pressure.
                if (_renderer._oomDetectedThisFrame)
                    return;

                var sw = Stopwatch.StartNew();
                double budgetMs = FrameBudgetMs;
                int generated = 0;
                int failed = 0;
                _deferredPriorityScratch.Clear();
                _deferredNormalScratch.Clear();
                List<GLMeshRenderer> deferredPriority = _deferredPriorityScratch;
                List<GLMeshRenderer> deferredNormal = _deferredNormalScratch;

                bool throttlePriorityGeneration = ThrottlePriorityGeneration;
                int priorityProcessed = 0;
                int normalProcessed = 0;

                // Priority lane: normally drains immediately so render-pipeline quads are ready the same frame.
                // During startup budget boosts, throttle this lane too so cold-start mesh generation is spread
                // across frames instead of stalling a single frame for hundreds of milliseconds.
                while (!_renderer._oomDetectedThisFrame
                    && (!throttlePriorityGeneration || sw.Elapsed.TotalMilliseconds < budgetMs)
                    && (!throttlePriorityGeneration || priorityProcessed < MaxThrottledPriorityRenderersPerFrame)
                    && _priorityQueue.TryDequeue(out var priorityRenderer))
                {
                    priorityProcessed++;
                    _pendingSet.TryRemove(priorityRenderer, out _);
                    QueueProcessResult result = ProcessRenderer(priorityRenderer);
                    CountProcessResult(priorityRenderer, result, deferredPriority, ref generated, ref failed);

                    if (_renderer._oomDetectedThisFrame)
                        break;
                }

                // Normal lane: process scene meshes within the frame budget.
                while (!_renderer._oomDetectedThisFrame
                    && sw.Elapsed.TotalMilliseconds < budgetMs
                    && normalProcessed < MaxNormalRenderersPerFrame
                    && _normalQueue.TryDequeue(out var renderer))
                {
                    normalProcessed++;
                    _pendingSet.TryRemove(renderer, out _);
                    QueueProcessResult result = ProcessRenderer(renderer);
                    CountProcessResult(renderer, result, deferredNormal, ref generated, ref failed);
                }

                RequeueDeferred(deferredPriority);
                RequeueDeferred(deferredNormal);

                if (_budgetBoosted && PendingCount == 0)
                {
                    FrameBudgetMs = _savedBudgetMs;
                    _budgetBoosted = false;
                }

                if (generated > 0 || failed > 0)
                {
                    _totalGenerated += generated;
                    _totalFailed += failed;
                    int remaining = PendingCount;
                    double elapsed = _logTimer.Elapsed.TotalMilliseconds;
                    bool queueJustDrained = remaining == 0 && _lastLoggedRemaining > 0;

                    // Log when: the queue just drained, failures occur, remaining changes significantly,
                    // or the periodic interval elapses while a backlog is still active.
                    bool shouldLog = queueJustDrained
                        || failed > 0
                        || Math.Abs(remaining - _lastLoggedRemaining) >= RemainingChangeThreshold
                        || (remaining > 0 && elapsed - _timeSinceLastLogMs >= LogIntervalMs);

                    if (shouldLog)
                    {
                        Debug.OpenGL($"[GLMeshGenerationQueue] Prepared {generated} mesh(es){(failed > 0 ? $", {failed} failed" : "")} in {sw.Elapsed.TotalMilliseconds:F2}ms, {remaining} remaining (total: {_totalGenerated} ok, {_totalFailed} failed)");
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
                while (_priorityQueue.TryDequeue(out var renderer))
                {
                    _pendingSet.TryRemove(renderer, out _);
                    if (!renderer.IsPreparedForRendering && renderer.Data is not null)
                    {
                        try
                        {
                            if (renderer.TryPrepareForRendering())
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

                while (_normalQueue.TryDequeue(out var renderer))
                {
                    _pendingSet.TryRemove(renderer, out _);
                    if (!renderer.IsPreparedForRendering && renderer.Data is not null)
                    {
                        try
                        {
                            if (renderer.TryPrepareForRendering())
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

            private void CountProcessResult(
                GLMeshRenderer renderer,
                QueueProcessResult result,
                List<GLMeshRenderer> deferred,
                ref int generated,
                ref int failed)
            {
                switch (result)
                {
                    case QueueProcessResult.Completed:
                        generated++;
                        break;
                    case QueueProcessResult.Pending:
                        deferred.Add(renderer);
                        break;
                    case QueueProcessResult.Failed:
                        failed++;
                        break;
                }
            }

            private void RequeueDeferred(List<GLMeshRenderer> deferred)
            {
                for (int i = 0; i < deferred.Count; i++)
                {
                    GLMeshRenderer renderer = deferred[i];
                    if (renderer.IsPreparedForRendering)
                        continue;

                    if (_failureCount.TryGetValue(renderer, out int failures) && failures >= MaxRetries)
                        continue;

                    if (!_pendingSet.TryAdd(renderer, 0))
                        continue;

                    if (renderer.MeshRenderer.GenerationPriority == EMeshGenerationPriority.RenderPipeline)
                        _priorityQueue.Enqueue(renderer);
                    else
                        _normalQueue.Enqueue(renderer);
                }
            }

            private QueueProcessResult ProcessRenderer(GLMeshRenderer renderer)
            {
                if (renderer.IsPreparedForRendering)
                {
                    _failureCount.TryRemove(renderer, out _);
                    return QueueProcessResult.NoWork;
                }

                if (renderer.Data is null)
                    return QueueProcessResult.NoWork;

                try
                {
                    if (!renderer.IsGenerated)
                        renderer.Generate();

                    if (!renderer.IsGenerated)
                    {
                        int failures = _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                        if (failures >= MaxRetries)
                            Debug.OpenGLWarning($"[GLMeshGenerationQueue] Giving up on mesh renderer after {failures} failed generation attempts: {renderer.GetDescribingName()}");

                        return QueueProcessResult.Failed;
                    }

                    if (renderer.TryPrepareForRendering())
                    {
                        _failureCount.TryRemove(renderer, out _);
                        return QueueProcessResult.Completed;
                    }

                    return QueueProcessResult.Pending;
                }
                catch (Exception ex)
                {
                    int failures = _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                    Debug.OpenGLException(ex, $"GLMeshGenerationQueue: Failed to prepare mesh renderer (attempt {failures}/{MaxRetries})");
                    if (failures >= MaxRetries)
                    {
                        Debug.OpenGLWarning($"[GLMeshGenerationQueue] Giving up on mesh renderer after {failures} failed preparation attempts: {renderer.GetDescribingName()}");
                        return QueueProcessResult.Failed;
                    }

                    return QueueProcessResult.Pending;
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
