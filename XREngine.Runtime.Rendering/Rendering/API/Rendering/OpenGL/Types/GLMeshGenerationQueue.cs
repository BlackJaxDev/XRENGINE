using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Manages frame-budgeted mesh generation to prevent FPS stalls when many meshes load at once.
    /// Spreads mesh initialization (VAO/VBO setup, shader compilation) across multiple frames.
    /// High-priority meshes (render-pipeline resources and editor-interactive overlays) are
    /// processed first so visible tools are not hidden behind large scene warmup backlogs.
    /// </summary>
    public unsafe partial class OpenGLRenderer
    {
        public sealed class GLMeshGenerationQueue
        {
            private readonly OpenGLRenderer _renderer;

            /// <summary>High-priority meshes: drained before normal scene meshes.</summary>
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
            private const double SlowRendererPrepareLogThresholdMs = 50.0;

            /// <summary>
            /// If <see cref="GLMeshRenderer.Generate"/> alone consumes more than this
            /// many milliseconds, the renderer is requeued for the next frame instead
            /// of running <see cref="GLMeshRenderer.TryPrepareForRendering"/> in the
            /// same frame. Bisects expensive first-use preparation across two frames
            /// so the render thread does not sit on a 50-70ms compound stall.
            /// </summary>
            private const double GenerateOverrunDeferThresholdMs = 8.0;

            /// <summary>
            /// Per-frame budget passed into <see cref="GLMeshRenderer.TryPrepareForRendering(double)"/>.
            /// If the accumulated <c>EnsureProgramsMatchRenderSettings</c> +
            /// <c>EnsureProgramsMatchMaterialShaderState</c> +
            /// <c>GenProgramsAndBuffers</c> stages consume more than this many
            /// milliseconds, <c>TryPrepareForRendering</c> bails out before the
            /// expensive <c>GetPrograms</c>/<c>BindBuffers</c> stages and the
            /// queue requeues the renderer for the next frame.
            /// Bounds compound first-use stalls when material-readiness gating
            /// has not yet landed (e.g. non-uber materials).
            /// </summary>
            public double TryPrepareOverrunDeferThresholdMs { get; set; } = 8.0;

            /// <summary>
            /// Maximum number of generation attempts per renderer before giving up.
            /// The renderer is retried from scratch when its mesh data genuinely changes.
            /// </summary>
            public int MaxRetries { get; set; } = 3;

            /// <summary>
            /// Maximum milliseconds to spend on mesh generation per frame.
            /// </summary>
            public double FrameBudgetMs { get; set; } = 3.0;

            /// <summary>
            /// Hard cap for normal scene renderer preparation in one render frame.
            /// The time budget is checked between renderers, so a count cap prevents
            /// a large backlog from chaining several individually-expensive first-use preparations.
            /// </summary>
            /// <remarks>
            /// First-use Sponza-class submesh preparation can run 50-70ms per renderer
            /// of synchronous OpenGL driver work (immutable buffer allocation + VAO
            /// setup) that cannot be sliced inside a single ProcessRenderer call.
            /// Capping at 1 ensures a frame contains at most one such overrun rather
            /// than compounding two of them into a 100ms+ render-thread stall.
            /// </remarks>
            public int MaxNormalRenderersPerFrame { get; set; } = 1;

            /// <summary>
            /// Hard cap for render-pipeline-priority renderer preparation when priority generation is throttled.
            /// </summary>
            public int MaxThrottledPriorityRenderersPerFrame { get; set; } = 4;

            /// <summary>
            /// Always-on hard cap for render-pipeline-priority renderer preparation per frame.
            /// Prevents the priority lane from iterating an unbounded backlog of
            /// material-not-ready renderers every frame (C-MESH-1).
            /// </summary>
            public int MaxPriorityRenderersPerFrame { get; set; } = 32;

            /// <summary>
            /// Maximum number of "material-not-ready" deferrals that may be visited
            /// per frame across the priority + normal lanes combined. Each visit
            /// re-requests uber-variant preparation and re-enqueues the renderer,
            /// which is cheap individually but accumulates render-thread time when
            /// the same hundred-plus renderers cycle every frame (C-MESH-1).
            /// </summary>
            public int MaxMaterialNotReadyDeferralsPerFrame { get; set; } = 16;

            private double _savedBudgetMs;
            private int _savedMaxNormalRenderersPerFrame;
            private int _savedMaxThrottledPriorityRenderersPerFrame;
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
            public void BoostBudgetUntilDrained(
                double boostedMs,
                int? maxNormalRenderersPerFrame = null,
                int? maxThrottledPriorityRenderersPerFrame = null)
            {
                if (_budgetBoosted)
                    return;

                _savedBudgetMs = FrameBudgetMs;
                _savedMaxNormalRenderersPerFrame = MaxNormalRenderersPerFrame;
                _savedMaxThrottledPriorityRenderersPerFrame = MaxThrottledPriorityRenderersPerFrame;
                FrameBudgetMs = boostedMs;
                if (maxNormalRenderersPerFrame.HasValue)
                    MaxNormalRenderersPerFrame = Math.Max(1, maxNormalRenderersPerFrame.Value);
                if (maxThrottledPriorityRenderersPerFrame.HasValue)
                    MaxThrottledPriorityRenderersPerFrame = Math.Max(1, maxThrottledPriorityRenderersPerFrame.Value);
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
            /// Pipeline/editor-priority meshes go into a fast lane that is drained before normal scene meshes.
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

                if (UsesPriorityGenerationQueue(renderer))
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
            /// High-priority meshes are drained first within the priority lane cap.
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
                int materialNotReadyDeferralsThisFrame = 0;
                int priorityCap = throttlePriorityGeneration
                    ? MaxThrottledPriorityRenderersPerFrame
                    : MaxPriorityRenderersPerFrame;

                // Priority lane: drains render-pipeline quads, but with an unconditional
                // time + count cap so a steady-state backlog of material-not-ready
                // renderers cannot iterate uncapped each frame (C-MESH-1).
                // During startup budget boost, the cap drops further to spread
                // cold-start mesh generation across more frames.
                while (!_renderer._oomDetectedThisFrame
                    && sw.Elapsed.TotalMilliseconds < budgetMs
                    && priorityProcessed < priorityCap
                    && _priorityQueue.TryDequeue(out var priorityRenderer))
                {
                    _pendingSet.TryRemove(priorityRenderer, out _);

                    if (!IsMaterialReadyForGeneration(priorityRenderer))
                    {
                        if (materialNotReadyDeferralsThisFrame >= MaxMaterialNotReadyDeferralsPerFrame)
                        {
                            // Re-enqueue without iterating further this frame; preserves
                            // ordering and lets the queue drain over subsequent frames.
                            deferredPriority.Add(priorityRenderer);
                            break;
                        }
                        materialNotReadyDeferralsThisFrame++;
                        RequestMaterialPrepIfNeeded(priorityRenderer);
                        deferredPriority.Add(priorityRenderer);
                        continue;
                    }

                    priorityProcessed++;
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
                    _pendingSet.TryRemove(renderer, out _);

                    // Defer renderers whose material's uber-variant preparation has not
                    // completed without consuming a per-frame slot. The synchronous
                    // shader-source generation otherwise runs inside GLMeshRenderer.Generate()
                    // on the render thread (50-70ms per first-use Sponza-class material).
                    if (!IsMaterialReadyForGeneration(renderer))
                    {
                        if (materialNotReadyDeferralsThisFrame >= MaxMaterialNotReadyDeferralsPerFrame)
                        {
                            deferredNormal.Add(renderer);
                            break;
                        }
                        materialNotReadyDeferralsThisFrame++;
                        RequestMaterialPrepIfNeeded(renderer);
                        deferredNormal.Add(renderer);
                        continue;
                    }

                    normalProcessed++;
                    QueueProcessResult result = ProcessRenderer(renderer);
                    CountProcessResult(renderer, result, deferredNormal, ref generated, ref failed);
                }

                RequeueDeferred(deferredPriority);
                RequeueDeferred(deferredNormal);

                if (_budgetBoosted && PendingCount == 0)
                {
                    FrameBudgetMs = _savedBudgetMs;
                    MaxNormalRenderersPerFrame = _savedMaxNormalRenderersPerFrame;
                    MaxThrottledPriorityRenderersPerFrame = _savedMaxThrottledPriorityRenderersPerFrame;
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

                    if (UsesPriorityGenerationQueue(renderer))
                        _priorityQueue.Enqueue(renderer);
                    else
                        _normalQueue.Enqueue(renderer);
                }
            }

            private static bool UsesPriorityGenerationQueue(GLMeshRenderer renderer)
                => renderer.MeshRenderer.GenerationPriority != EMeshGenerationPriority.Normal;

            private QueueProcessResult ProcessRenderer(GLMeshRenderer renderer)
            {
                if (renderer.IsPreparedForRendering)
                {
                    _failureCount.TryRemove(renderer, out _);
                    return QueueProcessResult.NoWork;
                }

                if (renderer.Data is null)
                    return QueueProcessResult.NoWork;

                if (renderer.Mesh is null)
                {
                    _failureCount.TryRemove(renderer, out _);
                    return QueueProcessResult.NoWork;
                }

                long processStart = Stopwatch.GetTimestamp();
                double generateMs = 0.0;
                double prepareMs = 0.0;

                try
                {
                    if (!renderer.IsGenerated)
                    {
                        long generateStart = Stopwatch.GetTimestamp();
                        renderer.Generate();
                        generateMs = ElapsedMilliseconds(generateStart);
                    }

                    if (!renderer.IsGenerated)
                    {
                        double totalMs = ElapsedMilliseconds(processStart);
                        LogSlowRendererPreparation(renderer, QueueProcessResult.Failed, totalMs, generateMs, prepareMs);
                        int failures = _failureCount.AddOrUpdate(renderer, 1, (_, c) => c + 1);
                        if (failures >= MaxRetries)
                            Debug.OpenGLWarning($"[GLMeshGenerationQueue] Giving up on mesh renderer after {failures} failed generation attempts: {renderer.GetDescribingName()}");

                        return QueueProcessResult.Failed;
                    }

                    // If Generate() alone consumed most of a frame budget, defer
                    // TryPrepareForRendering to the next frame so we do not compound
                    // a 50ms+ Generate with a 50ms+ TryPrepare in a single render
                    // frame. The Pending result requeues the renderer.
                    if (generateMs > GenerateOverrunDeferThresholdMs)
                    {
                        LogSlowRendererPreparation(renderer, QueueProcessResult.Pending, ElapsedMilliseconds(processStart), generateMs, prepareMs);
                        return QueueProcessResult.Pending;
                    }

                    long prepareStart = Stopwatch.GetTimestamp();
                    bool prepared = renderer.TryPrepareForRendering(TryPrepareOverrunDeferThresholdMs);
                    prepareMs = ElapsedMilliseconds(prepareStart);
                    if (prepared)
                    {
                        LogSlowRendererPreparation(renderer, QueueProcessResult.Completed, ElapsedMilliseconds(processStart), generateMs, prepareMs);
                        _failureCount.TryRemove(renderer, out _);
                        return QueueProcessResult.Completed;
                    }

                    if (string.Equals(renderer.LastPrepareResult, "DeferredOverrun", StringComparison.Ordinal))
                    {
                        Debug.OpenGL(
                            $"[GLMeshGenerationQueue] TryPrepare deferred: deferOverrunMs={renderer.LastDeferOverrunMs:F2}, " +
                            $"budgetMs={TryPrepareOverrunDeferThresholdMs:F2}, generateMs={generateMs:F2}, " +
                            $"renderer='{renderer.GetDescribingName()}', material='{renderer.MeshRenderer.Material?.Name ?? "<null>"}'.");
                        return QueueProcessResult.Pending;
                    }

                    LogSlowRendererPreparation(renderer, QueueProcessResult.Pending, ElapsedMilliseconds(processStart), generateMs, prepareMs);
                    return QueueProcessResult.Pending;
                }
                catch (Exception ex)
                {
                    LogSlowRendererPreparation(renderer, QueueProcessResult.Pending, ElapsedMilliseconds(processStart), generateMs, prepareMs);
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

            private static void LogSlowRendererPreparation(
                GLMeshRenderer renderer,
                QueueProcessResult result,
                double totalMs,
                double generateMs,
                double prepareMs)
            {
                if (totalMs < SlowRendererPrepareLogThresholdMs &&
                    generateMs < SlowRendererPrepareLogThresholdMs &&
                    prepareMs < SlowRendererPrepareLogThresholdMs)
                {
                    return;
                }

                Debug.OpenGLWarning(
                    $"[GLMeshGenerationQueue] Slow renderer prep: result={result}, totalMs={totalMs:F2}, " +
                    $"generateMs={generateMs:F2}, tryPrepareMs={prepareMs:F2}, pending={renderer.Renderer.MeshGenerationQueue.PendingCount}, " +
                    $"priority={renderer.MeshRenderer.GenerationPriority}, generated={renderer.IsGenerated}, prepared={renderer.IsPreparedForRendering}, " +
                    $"lastPrepare={renderer.LastPrepareResult}, lastPrepareDetail='{renderer.LastPrepareDetail}', " +
                    $"shadowPass={RuntimeEngine.Rendering.State.IsShadowPass}, directionalAtlasGrouped={RuntimeEngine.Rendering.State.IsDirectionalCascadeAtlasGroupedShadowPass}, " +
                    $"pointAtlasGrouped={RuntimeEngine.Rendering.State.IsPointLightAtlasGroupedShadowPass}, renderer='{renderer.GetDescribingName()}', " +
                    $"mesh='{renderer.Mesh?.Name ?? "<null>"}', material='{renderer.MeshRenderer.Material?.Name ?? "<null>"}'.");
            }

            private static double ElapsedMilliseconds(long startTimestamp)
                => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

            /// <summary>
            /// Returns true if the renderer's material has either no uber-variant
            /// requirement or a prepared variant already active. False means
            /// preparation is in flight (or hasn't been requested yet) and the
            /// renderer should be deferred to a later frame so the synchronous
            /// shader-source generation does not run on the render thread.
            /// </summary>
            private static bool IsMaterialReadyForGeneration(GLMeshRenderer renderer)
            {
                XRMaterial? material = renderer.MeshRenderer.Material;
                return material is null || material.IsUberVariantReadyForRendering();
            }

            /// <summary>
            /// Asks the material to start an asynchronous uber-variant build if
            /// one is not already in flight or complete. No-op for non-uber materials.
            /// </summary>
            private static void RequestMaterialPrepIfNeeded(GLMeshRenderer renderer)
                => renderer.MeshRenderer.Material?.RequestUberVariantPreparationIfNeeded();
        }

        private GLMeshGenerationQueue? _meshGenerationQueue;

        /// <summary>
        /// Gets the frame-budgeted mesh generation queue for this renderer.
        /// </summary>
        public GLMeshGenerationQueue MeshGenerationQueue => _meshGenerationQueue ??= new GLMeshGenerationQueue(this);
    }
}
