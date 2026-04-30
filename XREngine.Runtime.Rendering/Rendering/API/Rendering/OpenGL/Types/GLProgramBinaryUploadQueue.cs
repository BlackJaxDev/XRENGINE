using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Threading;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// Processes <c>glProgramBinary</c> calls on a shared background GL context to avoid
        /// stalling the main render thread. Uses GLFW context sharing so program objects
        /// loaded on the background thread are immediately usable by the primary context
        /// after synchronization.
        /// <para/>
        /// In-flight uploads are capped at <see cref="MaxInFlight"/> to prevent the shared
        /// context from exhausting GPU memory while the main thread is also allocating.
        /// </summary>
        public sealed class GLProgramBinaryUploadQueue(OpenGLRenderer.GLSharedContext sharedContext)
        {
            private readonly GLSharedContext _sharedContext = sharedContext;
            private readonly ConcurrentDictionary<uint, UploadResult> _completed = new();
            private int _inFlight;

            /// <summary>
            /// Maximum number of program binary uploads that can be queued but not yet
            /// consumed by the main thread. Prevents unbounded VRAM allocation from the
            /// shared context competing with main-thread buffer/mesh uploads.
            /// </summary>
            public const int MaxInFlight = 8;

            public bool IsAvailable => _sharedContext.IsRunning;

            /// <summary>
            /// Returns <c>true</c> if the in-flight count is below <see cref="MaxInFlight"/>
            /// and a new upload can be enqueued without risking VRAM exhaustion.
            /// </summary>
            public bool CanEnqueue => Volatile.Read(ref _inFlight) < MaxInFlight;

            /// <summary>Number of uploads queued or completed but not yet consumed.</summary>
            public int InFlightCount => Volatile.Read(ref _inFlight);

            public enum UploadStatus : byte { Success, Failed }

            public readonly record struct UploadResult(UploadStatus Status, GLEnum Format, ulong Hash);

            /// <summary>
            /// Queues a program binary upload on the shared context thread.
            /// The GL program object must already be created (via <c>glCreateProgram</c>) on the main thread.
            /// After the upload completes, poll <see cref="TryGetResult"/> to retrieve the outcome.
            /// <para/>
            /// Check <see cref="CanEnqueue"/> before calling to respect the in-flight limit.
            /// </summary>
            public void EnqueueUpload(uint programId, byte[] binary, GLEnum format, uint length, ulong hash)
            {
                Interlocked.Increment(ref _inFlight);
                _sharedContext.Enqueue(gl =>
                {
                    fixed (byte* ptr = binary)
                        gl.ProgramBinary(programId, format, ptr, length);

                    var error = gl.GetError();

                    // glFinish ensures the binary is fully uploaded and validated before
                    // signaling completion. This is the recommended synchronization for
                    // shared-context resource uploads.
                    gl.Finish();

                    _completed[programId] = new UploadResult(
                        error == GLEnum.NoError ? UploadStatus.Success : UploadStatus.Failed,
                        format,
                        hash);
                });
            }

            /// <summary>
            /// Checks whether an async binary upload has completed for the given program.
            /// Returns <c>true</c> if a result is available (success or failure).
            /// The result is consumed (removed) on retrieval, freeing an in-flight slot.
            /// </summary>
            public bool TryGetResult(uint programId, out UploadResult result)
            {
                if (_completed.TryRemove(programId, out result))
                {
                    Interlocked.Decrement(ref _inFlight);
                    return true;
                }
                return false;
            }
        }
    }
}
