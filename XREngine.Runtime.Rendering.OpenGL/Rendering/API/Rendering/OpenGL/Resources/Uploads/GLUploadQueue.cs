using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Manages frame-budgeted GPU uploads to prevent FPS stalls when many meshes load at once.
    /// Spreads buffer uploads across multiple frames based on a time budget.
    /// </summary>
    public unsafe partial class OpenGLRenderer
    {
        public sealed class GLUploadQueue
        {
            private readonly OpenGLRenderer _renderer;
            private readonly ConcurrentQueue<PendingUpload> _pendingUploads = new();
            private readonly ConcurrentDictionary<GLDataBuffer, byte> _pendingBuffers = new();
            
            /// <summary>
            /// Maximum milliseconds to spend on uploads per frame.
            /// </summary>
            public double FrameBudgetMs { get; set; } = 2.0;

            private const uint MaxUploadChunkBytes = 1024 * 1024;
            private const double MinimumFrameBudgetMs = 0.05;
            private const double DefaultHardFrameBudgetMs = 2.0;
            private const string HardBudgetEnvVar = XREngineEnvironmentVariables.UploadQueueHardBudgetMs;
            private const string ChunkLoggingEnvVar = XREngineEnvironmentVariables.UploadQueueChunkLogging;
            private static readonly bool EnableChunkLogging = IsEnabledEnvironmentVariable(ChunkLoggingEnvVar);

            /// <summary>
            /// Hard upper bound for upload work in a render frame. Boosted startup budgets
            /// are clamped to this value so warmup cannot consume an entire frame.
            /// </summary>
            public double HardFrameBudgetMs { get; set; } = ResolveInitialHardFrameBudgetMs();

            /// <summary>
            /// Whether frame-budgeted uploads are enabled.
            /// When disabled, uploads happen immediately.
            /// </summary>
            public bool Enabled { get; set; } = true;

            private double _savedBudgetMs;
            private bool _budgetBoosted;

            /// <summary>
            /// Number of pending uploads in the queue.
            /// </summary>
            public int PendingCount => _pendingUploads.Count;

            public int LastDequeuedItems { get; private set; }
            public int LastCompletedItems { get; private set; }
            public long LastUploadedBytes { get; private set; }
            public double LastElapsedMs { get; private set; }
            public double LastRequestedBudgetMs { get; private set; }
            public double LastEffectiveBudgetMs { get; private set; }
            public bool LastBudgetWasBoosted { get; private set; }
            public bool LastBudgetWasClamped { get; private set; }
            /// <summary>Wall-clock time of the slowest single chunk processed in the last frame.</summary>
            public double LastMaxChunkMs { get; private set; }
            /// <summary>Count of single-chunk uploads in the last frame whose wall time exceeded <see cref="HardFrameBudgetMs"/>.</summary>
            public int LastChunkOverrunCount { get; private set; }
            /// <summary>Count of dequeues skipped in the last frame because the predicted chunk cost would overshoot the budget.</summary>
            public int LastPredictiveSkipCount { get; private set; }

            // Rolling estimate of recent worst-case single-chunk cost, used to predict overruns
            // before dequeuing another upload. Decays toward the latest sample so a one-off slow
            // chunk cannot poison the predictor indefinitely.
            private double _recentMaxChunkMs;
            private const double RecentMaxChunkDecay = 0.85;

            /// <summary>
            /// Checks if a buffer has a pending upload (data not yet on GPU).
            /// </summary>
            public bool HasPendingUpload(GLDataBuffer buffer)
                => _pendingBuffers.ContainsKey(buffer);

            public GLUploadQueue(OpenGLRenderer renderer)
            {
                _renderer = renderer;
            }

            private readonly struct PendingUpload
            {
                public required GLDataBuffer Buffer { get; init; }
                public required byte[] Data { get; init; }
                public required uint DataLength { get; init; }
                public uint Offset { get; init; }
                public bool StorageAllocated { get; init; }
                public bool ReturnDataToPool { get; init; }
            }

            /// <summary>
            /// Enqueues a buffer upload to be processed during the frame budget.
            /// </summary>
            public void EnqueueUpload(GLDataBuffer buffer, byte[] data, uint dataLength, bool returnDataToPool = false)
            {
                buffer._hasPendingUpload = true;
                _pendingBuffers.TryAdd(buffer, 0);
                _pendingUploads.Enqueue(new PendingUpload
                {
                    Buffer = buffer,
                    Data = data,
                    DataLength = dataLength,
                    Offset = 0,
                    StorageAllocated = false,
                    ReturnDataToPool = returnDataToPool
                });
            }

            /// <summary>
            /// Processes pending uploads within the frame budget.
            /// Call this once per frame from the render thread.
            /// </summary>
            public void ProcessUploads()
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLUploadQueue.ProcessUploads");

                ResetLastProcessStats();

                if (_pendingUploads.IsEmpty)
                {
                    if (_budgetBoosted)
                    {
                        FrameBudgetMs = _savedBudgetMs;
                        _budgetBoosted = false;
                    }
                    return;
                }

                // Bail if the GPU is in OOM recovery — uploading more data would worsen VRAM pressure.
                if (_renderer._oomDetectedThisFrame)
                    return;

                var sw = Stopwatch.StartNew();
                double requestedBudgetMs = NormalizeBudget(FrameBudgetMs);
                double hardBudgetMs = NormalizeBudget(HardFrameBudgetMs);
                double budgetMs = Math.Min(requestedBudgetMs, hardBudgetMs);

                LastRequestedBudgetMs = requestedBudgetMs;
                LastEffectiveBudgetMs = budgetMs;
                LastBudgetWasBoosted = _budgetBoosted;
                LastBudgetWasClamped = requestedBudgetMs > budgetMs;

                while (!_renderer._oomDetectedThisFrame && sw.Elapsed.TotalMilliseconds < budgetMs)
                {
                    // Predictive skip: if recent chunks have been expensive and starting another
                    // would push us past the hard budget, stop now rather than overrun on a chunk
                    // boundary. This is what bounds the 53–114 ms single-chunk spikes the perf
                    // doc captured. Still process one chunk per non-empty frame so a slow
                    // previous sample cannot pin the queue forever.
                    double elapsedMs = sw.Elapsed.TotalMilliseconds;
                    if (LastDequeuedItems > 0 && _recentMaxChunkMs > 0.0 && elapsedMs + _recentMaxChunkMs > budgetMs)
                    {
                        LastPredictiveSkipCount++;
                        break;
                    }

                    if (!_pendingUploads.TryDequeue(out var upload))
                        break;

                    LastDequeuedItems++;
                    long chunkStart = Stopwatch.GetTimestamp();
                    UploadExecutionResult result = ExecuteUpload(upload);
                    double chunkMs = ElapsedMs(chunkStart);

                    LastUploadedBytes += result.UploadedBytes;
                    if (result.Completed)
                        LastCompletedItems++;
                    if (chunkMs > LastMaxChunkMs)
                        LastMaxChunkMs = chunkMs;
                    if (chunkMs > hardBudgetMs)
                        LastChunkOverrunCount++;
                    // Decayed rolling max: take the larger of (decayed previous, latest sample).
                    _recentMaxChunkMs = Math.Max(_recentMaxChunkMs * RecentMaxChunkDecay, chunkMs);

                    LogChunk(upload, result, chunkMs, sw.Elapsed.TotalMilliseconds, budgetMs);

                    // Budgeting is chunk-boundary based. If one GL upload overruns, stop
                    // immediately so boosted startup drains cannot cascade into more work.
                    if (sw.Elapsed.TotalMilliseconds >= budgetMs)
                        break;
                }

                LastElapsedMs = sw.Elapsed.TotalMilliseconds;
                LogFrameSummary();
                WarnIfChunkOverran(hardBudgetMs);

                if (_budgetBoosted && _pendingUploads.IsEmpty)
                {
                    FrameBudgetMs = _savedBudgetMs;
                    _budgetBoosted = false;
                }
            }

            /// <summary>
            /// Temporarily increases <see cref="FrameBudgetMs"/> to drain a large backlog
            /// faster (e.g. during startup when many buffers and shaders need uploading).
            /// The original budget is automatically restored when the pending queue empties.
            /// Safe to call from any thread.
            /// </summary>
            public void BoostBudgetUntilDrained(double boostedMs)
            {
                if (_budgetBoosted)
                    return;

                _savedBudgetMs = FrameBudgetMs;
                FrameBudgetMs = NormalizeBudget(boostedMs);
                _budgetBoosted = true;
            }

            private UploadExecutionResult ExecuteUpload(PendingUpload upload)
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLUploadQueue.ExecuteUpload");

                var buffer = upload.Buffer;
                var data = upload.Data;
                var dataLength = upload.DataLength;

                if (buffer.Data.IsDestroyed)
                {
                    buffer.FailQueuedUpload();
                    _pendingBuffers.TryRemove(buffer, out _);
                    ReturnUploadData(upload);
                    return UploadExecutionResult.Failure;
                }

                // Ensure buffer is generated
                if (!buffer.IsGenerated)
                    buffer.Generate();

                uint targetBufferId = buffer.BindingId;
                if (targetBufferId == GLObjectBase.InvalidBindingId)
                {
                    buffer.FailQueuedUpload();
                    _pendingBuffers.TryRemove(buffer, out _);
                    ReturnUploadData(upload);
                    Debug.OpenGLWarning("GLUploadQueue: Failed to generate buffer for upload.");
                    return UploadExecutionResult.Failure;
                }

                if (!RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(dataLength, buffer.AllocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
                {
                    buffer.FailQueuedUpload();
                    _pendingBuffers.TryRemove(buffer, out _);
                    ReturnUploadData(upload);
                    Debug.OpenGLWarning($"[VRAM Budget] Skipping queued buffer upload for '{buffer.GetDescribingName()}' ({dataLength} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes.");
                    return UploadExecutionResult.Failure;
                }

                if (!upload.StorageAllocated)
                {
                    // Allocate once without copying client memory, then stream sub-ranges over later frames.
                    if (buffer.ImmutableStorageSet)
                        buffer.RecreateBufferAsMutable();

                    _renderer.Api.NamedBufferData(buffer.BindingId, dataLength, (void*)null, GLDataBuffer.ToGLEnum(buffer.Data.Usage));
                    buffer.TrackAllocation(dataLength);
                }

                uint offset = upload.Offset;
                uint remaining = dataLength - offset;
                uint chunkLength = Math.Min(MaxUploadChunkBytes, remaining);
                int offsetBytes = checked((int)offset);
                fixed (byte* src = data)
                {
                    _renderer.Api.NamedBufferSubData(buffer.BindingId, offsetBytes, chunkLength, src + offsetBytes);
                }
                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes, chunkLength);

                offset += chunkLength;
                if (offset < dataLength)
                {
                    _pendingUploads.Enqueue(upload with
                    {
                        Offset = offset,
                        StorageAllocated = true
                    });
                    return new UploadExecutionResult(chunkLength, completed: false, requeued: true, failed: false);
                }

                buffer.SetLastPushedLength(dataLength);
                _pendingBuffers.TryRemove(buffer, out _);
                ReturnUploadData(upload);
                buffer.CompleteQueuedUpload(dataLength);
                return new UploadExecutionResult(chunkLength, completed: true, requeued: false, failed: false);
            }

            private static void ReturnUploadData(PendingUpload upload)
            {
                if (upload.ReturnDataToPool)
                    ArrayPool<byte>.Shared.Return(upload.Data);
            }

            private void ResetLastProcessStats()
            {
                LastDequeuedItems = 0;
                LastCompletedItems = 0;
                LastUploadedBytes = 0;
                LastElapsedMs = 0.0;
                LastRequestedBudgetMs = NormalizeBudget(FrameBudgetMs);
                LastEffectiveBudgetMs = Math.Min(LastRequestedBudgetMs, NormalizeBudget(HardFrameBudgetMs));
                LastBudgetWasBoosted = _budgetBoosted;
                LastBudgetWasClamped = LastRequestedBudgetMs > LastEffectiveBudgetMs;
                LastMaxChunkMs = 0.0;
                LastChunkOverrunCount = 0;
                LastPredictiveSkipCount = 0;
            }

            private static double NormalizeBudget(double budgetMs)
            {
                if (double.IsNaN(budgetMs) || double.IsInfinity(budgetMs))
                    return DefaultHardFrameBudgetMs;

                return Math.Max(MinimumFrameBudgetMs, budgetMs);
            }

            private static double ResolveInitialHardFrameBudgetMs()
            {
                string? raw = Environment.GetEnvironmentVariable(HardBudgetEnvVar);
                return !string.IsNullOrWhiteSpace(raw) &&
                    double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? NormalizeBudget(parsed)
                    : DefaultHardFrameBudgetMs;
            }

            private static bool IsEnabledEnvironmentVariable(string name)
            {
                string? raw = Environment.GetEnvironmentVariable(name);
                return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }

            private static double ElapsedMs(long startTicks)
                => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

            private static void LogChunk(
                PendingUpload upload,
                UploadExecutionResult result,
                double chunkMs,
                double frameElapsedMs,
                double budgetMs)
            {
                if (!EnableChunkLogging)
                    return;

                Debug.OpenGL(
                    "[GLUploadQueue] chunk buffer='{0}' bytes={1} completed={2} requeued={3} failed={4} chunkMs={5:F3} frameMs={6:F3}/{7:F3}",
                    upload.Buffer.GetDescribingName(),
                    result.UploadedBytes,
                    result.Completed,
                    result.Requeued,
                    result.Failed,
                    chunkMs,
                    frameElapsedMs,
                    budgetMs);
            }

            private void LogFrameSummary()
            {
                if (!EnableChunkLogging || LastDequeuedItems == 0)
                    return;

                Debug.OpenGL(
                    "[GLUploadQueue] frame dequeued={0} completed={1} bytes={2} elapsedMs={3:F3} budgetMs={4:F3} requestedMs={5:F3} boosted={6} clamped={7} maxChunkMs={8:F3} overruns={9} predictiveSkips={10} pending={11}",
                    LastDequeuedItems,
                    LastCompletedItems,
                    LastUploadedBytes,
                    LastElapsedMs,
                    LastEffectiveBudgetMs,
                    LastRequestedBudgetMs,
                    LastBudgetWasBoosted,
                    LastBudgetWasClamped,
                    LastMaxChunkMs,
                    LastChunkOverrunCount,
                    LastPredictiveSkipCount,
                    PendingCount);
            }

            private void WarnIfChunkOverran(double hardBudgetMs)
            {
                if (LastChunkOverrunCount == 0)
                    return;

                // Always warn (regardless of chunk-logging env var) when a single GL upload
                // breached the hard frame budget. Surfacing the spike is the whole point of A2.
                Debug.OpenGLWarning(
                    $"[GLUploadQueue] single-chunk upload exceeded hard budget: overruns={LastChunkOverrunCount} maxChunkMs={LastMaxChunkMs:F3} hardBudgetMs={hardBudgetMs:F3} dequeued={LastDequeuedItems} bytes={LastUploadedBytes} pending={PendingCount}");
            }

            private readonly struct UploadExecutionResult
            {
                public static UploadExecutionResult Failure { get; } = new(0, completed: true, requeued: false, failed: true);

                public UploadExecutionResult(uint uploadedBytes, bool completed, bool requeued, bool failed)
                {
                    UploadedBytes = uploadedBytes;
                    Completed = completed;
                    Requeued = requeued;
                    Failed = failed;
                }

                public uint UploadedBytes { get; }
                public bool Completed { get; }
                public bool Requeued { get; }
                public bool Failed { get; }
            }

            /// <summary>
            /// Forces all pending uploads to complete immediately.
            /// Use sparingly, as this defeats the purpose of frame budgeting.
            /// </summary>
            public void FlushAll()
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLUploadQueue.FlushAll");

                while (_pendingUploads.TryDequeue(out var upload))
                {
                    ExecuteUpload(upload);
                }
            }

            /// <summary>
            /// Forces one buffer's queued upload to complete without draining unrelated uploads.
            /// </summary>
            public void FlushBuffer(GLDataBuffer buffer)
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLUploadQueue.FlushBuffer");

                if (!_pendingBuffers.ContainsKey(buffer))
                    return;

                while (_pendingBuffers.ContainsKey(buffer))
                {
                    int scanCount = _pendingUploads.Count;
                    if (scanCount == 0)
                        return;

                    bool executedTargetUpload = false;
                    for (int i = 0; i < scanCount; ++i)
                    {
                        if (!_pendingUploads.TryDequeue(out var upload))
                            break;

                        if (ReferenceEquals(upload.Buffer, buffer))
                        {
                            ExecuteUpload(upload);
                            executedTargetUpload = true;
                        }
                        else
                        {
                            _pendingUploads.Enqueue(upload);
                        }
                    }

                    if (!executedTargetUpload)
                        return;
                }
            }
        }

        private GLUploadQueue? _uploadQueue;

        /// <summary>
        /// Gets the frame-budgeted upload queue for this renderer.
        /// </summary>
        public GLUploadQueue UploadQueue => _uploadQueue ??= new GLUploadQueue(this);
    }
}
