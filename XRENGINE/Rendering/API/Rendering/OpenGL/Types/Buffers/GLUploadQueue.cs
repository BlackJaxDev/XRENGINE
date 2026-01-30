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

            /// <summary>
            /// Whether frame-budgeted uploads are enabled.
            /// When disabled, uploads happen immediately.
            /// </summary>
            public bool Enabled { get; set; } = true;

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
            }

            /// <summary>
            /// Enqueues a buffer upload to be processed during the frame budget.
            /// </summary>
            public void EnqueueUpload(GLDataBuffer buffer, byte[] data, uint dataLength)
            {
                _pendingBuffers.TryAdd(buffer, 0);
                _pendingUploads.Enqueue(new PendingUpload
                {
                    Buffer = buffer,
                    Data = data,
                    DataLength = dataLength
                });
            }

            /// <summary>
            /// Processes pending uploads within the frame budget.
            /// Call this once per frame from the render thread.
            /// </summary>
            public void ProcessUploads()
            {
                if (_pendingUploads.IsEmpty)
                    return;

                var sw = Stopwatch.StartNew();
                double budgetMs = FrameBudgetMs;

                while (sw.Elapsed.TotalMilliseconds < budgetMs && _pendingUploads.TryDequeue(out var upload))
                {
                    ExecuteUpload(upload);
                }
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
                    _pendingBuffers.TryRemove(buffer, out _);
                    Debug.LogWarning("GLUploadQueue: Failed to generate buffer for upload.");
                    return;
                }

                // Perform the upload
                fixed (byte* src = data)
                {
                    _renderer.Api.NamedBufferData(targetBufferId, dataLength, src, GLDataBuffer.ToGLEnum(buffer.Data.Usage));
                }

                buffer.SetLastPushedLength(dataLength);
                buffer.TrackAllocation(dataLength);

                // Remove from pending set after successful upload
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
