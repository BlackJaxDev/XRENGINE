using System.Collections.Concurrent;
using System.Diagnostics;

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
            }

            /// <summary>
            /// Enqueues a buffer upload to be processed during the frame budget.
            /// </summary>
            public void EnqueueUpload(GLDataBuffer buffer, byte[] data, uint dataLength)
            {
                buffer._hasPendingUpload = true;
                _pendingBuffers.TryAdd(buffer, 0);
                _pendingUploads.Enqueue(new PendingUpload
                {
                    Buffer = buffer,
                    Data = data,
                    DataLength = dataLength,
                    Offset = 0,
                    StorageAllocated = false
                });
            }

            /// <summary>
            /// Processes pending uploads within the frame budget.
            /// Call this once per frame from the render thread.
            /// </summary>
            public void ProcessUploads()
            {
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
                double budgetMs = FrameBudgetMs;

                while (!_renderer._oomDetectedThisFrame && sw.Elapsed.TotalMilliseconds < budgetMs && _pendingUploads.TryDequeue(out var upload))
                {
                    ExecuteUpload(upload);
                }

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
                FrameBudgetMs = boostedMs;
                _budgetBoosted = true;
            }

            private void ExecuteUpload(PendingUpload upload)
            {
                var buffer = upload.Buffer;
                var data = upload.Data;
                var dataLength = upload.DataLength;

                // Ensure buffer is generated
                if (!buffer.IsGenerated)
                    buffer.Generate();

                uint targetBufferId = buffer.BindingId;
                if (targetBufferId == GLObjectBase.InvalidBindingId)
                {
                    buffer._hasPendingUpload = false;
                    _pendingBuffers.TryRemove(buffer, out _);
                    Debug.OpenGLWarning("GLUploadQueue: Failed to generate buffer for upload.");
                    return;
                }

                if (!Engine.Rendering.Stats.CanAllocateVram(dataLength, buffer.AllocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
                {
                    buffer._hasPendingUpload = false;
                    _pendingBuffers.TryRemove(buffer, out _);
                    Debug.OpenGLWarning($"[VRAM Budget] Skipping queued buffer upload for '{buffer.GetDescribingName()}' ({dataLength} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes.");
                    return;
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

                offset += chunkLength;
                if (offset < dataLength)
                {
                    _pendingUploads.Enqueue(upload with
                    {
                        Offset = offset,
                        StorageAllocated = true
                    });
                    return;
                }

                buffer.SetLastPushedLength(dataLength);
                buffer._hasPendingUpload = false;
                _pendingBuffers.TryRemove(buffer, out _);
            }

            /// <summary>
            /// Forces all pending uploads to complete immediately.
            /// Use sparingly, as this defeats the purpose of frame budgeting.
            /// </summary>
            public void FlushAll()
            {
                while (_pendingUploads.TryDequeue(out var upload))
                {
                    ExecuteUpload(upload);
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
