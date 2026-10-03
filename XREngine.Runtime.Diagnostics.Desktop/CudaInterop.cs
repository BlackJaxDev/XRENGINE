using System.Runtime.InteropServices;

namespace XREngine.Data
{
    /// <summary>
    /// Low-level CUDA runtime P/Invoke interop with RAII helpers.
    /// All CUDA calls are centralized here so GPU consumers
    /// (nvCOMP, future CUDA kernels, etc.) share one clean surface.
    /// </summary>
    public static class CudaInterop
    {
        private const string CudaRtLib = "cudart64_12"; // CUDA 12.x runtime

        // ──────────────────── Constants ──────────────────────────────────

        private const int cudaSuccess = 0;
        private const int cudaMemcpyHostToDevice = 1;
        private const int cudaMemcpyDeviceToHost = 2;

        // ──────────────────── P/Invoke imports ───────────────────────────

        // Error handling
        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint cudaGetErrorString(int error);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint cudaGetErrorName(int error);

        // Device management
        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int cudaGetDeviceCount(int* count);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int cudaGetDevice(int* device);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaSetDevice(int device);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaDeviceSynchronize();

        // Memory
        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaMalloc(out nint devPtr, nuint size);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaFree(nint devPtr);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaMemcpy(nint dst, nint src, nuint count, int kind);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaMemcpyAsync(nint dst, nint src, nuint count, int kind, nint stream);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaMemset(nint devPtr, int value, nuint count);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaMemsetAsync(nint devPtr, int value, nuint count, nint stream);

        // Streams
        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaStreamCreate(out nint stream);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaStreamSynchronize(nint stream);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaStreamDestroy(nint stream);

        // Events
        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaEventCreate(out nint @event);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaEventRecord(nint @event, nint stream);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaEventSynchronize(nint @event);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int cudaEventElapsedTime(float* ms, nint start, nint end);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cudaEventDestroy(nint @event);

        // ──────────────────── Error handling ─────────────────────────────

        /// <summary>
        /// Returns the human-readable error string for a CUDA error code,
        /// e.g. <c>"out of memory"</c>.
        /// </summary>
        public static string GetErrorString(int error)
        {
            nint ptr = cudaGetErrorString(error);
            return Marshal.PtrToStringAnsi(ptr) ?? $"unknown ({error})";
        }

        /// <summary>
        /// Returns the symbolic error name for a CUDA error code,
        /// e.g. <c>"cudaErrorMemoryAllocation"</c>.
        /// </summary>
        public static string GetErrorName(int error)
        {
            nint ptr = cudaGetErrorName(error);
            return Marshal.PtrToStringAnsi(ptr) ?? $"UNKNOWN_{error}";
        }

        public static void Check(int err, string context)
        {
            if (err != cudaSuccess)
                throw new InvalidOperationException(
                    $"CUDA {GetErrorName(err)} ({err}): {GetErrorString(err)} — during {context}.");
        }

        // ──────────────────── Library loading ────────────────────────────

        public static bool TryLoad(out nint cudartHandle)
            => NativeLibrary.TryLoad(CudaRtLib, out cudartHandle);

        // ──────────────────── Device management ──────────────────────────

        /// <summary>Returns the number of CUDA-capable devices. Throws on CUDA error.</summary>
        public static unsafe int GetDeviceCount()
        {
            int count = 0;
            Check(cudaGetDeviceCount(&count), "cudaGetDeviceCount");
            return count;
        }

        /// <summary>
        /// Non-throwing probe: returns <c>true</c> and writes device count if
        /// <c>cudaGetDeviceCount</c> succeeds with at least one device.
        /// </summary>
        public static unsafe bool TryGetDeviceCount(out int count)
        {
            count = 0;
            fixed (int* p = &count)
                return cudaGetDeviceCount(p) == cudaSuccess && count > 0;
        }

        /// <summary>Returns the ordinal of the currently active CUDA device.</summary>
        public static unsafe int GetDevice()
        {
            int device = 0;
            Check(cudaGetDevice(&device), "cudaGetDevice");
            return device;
        }

        /// <summary>Sets the active CUDA device for the calling thread.</summary>
        public static void SetDevice(int device)
            => Check(cudaSetDevice(device), "cudaSetDevice");

        /// <summary>Blocks until the device has completed all preceding tasks.</summary>
        public static void DeviceSynchronize()
            => Check(cudaDeviceSynchronize(), "cudaDeviceSynchronize");

        // ──────────────────── Memory ─────────────────────────────────────

        public static nint Malloc(nuint size, string context)
        {
            Check(cudaMalloc(out nint ptr, size), context);
            return ptr;
        }

        public static void Free(nint ptr)
            => Check(cudaFree(ptr), "cudaFree");

        // Synchronous copies (raw pointers)

        public static void CopyHostToDevice(nint destination, nint source, nuint count)
            => Check(cudaMemcpy(destination, source, count, cudaMemcpyHostToDevice), "cudaMemcpy H→D");

        public static void CopyDeviceToHost(nint destination, nint source, nuint count)
            => Check(cudaMemcpy(destination, source, count, cudaMemcpyDeviceToHost), "cudaMemcpy D→H");

        // Span-friendly synchronous copies

        /// <summary>Uploads a host span to a device pointer (synchronous).</summary>
        public static unsafe void Upload(ReadOnlySpan<byte> source, nint deviceDst)
        {
            fixed (byte* p = source)
                CopyHostToDevice(deviceDst, (nint)p, (nuint)source.Length);
        }

        /// <summary>Downloads from a device pointer into a host span (synchronous).</summary>
        public static unsafe void Download(nint deviceSrc, Span<byte> destination)
        {
            fixed (byte* p = destination)
                CopyDeviceToHost((nint)p, deviceSrc, (nuint)destination.Length);
        }

        // Asynchronous copies (raw pointers, stream-ordered)

        /// <summary>Enqueues a host→device copy on <paramref name="stream"/>.</summary>
        public static void CopyHostToDeviceAsync(nint destination, nint source, nuint count, nint stream)
            => Check(cudaMemcpyAsync(destination, source, count, cudaMemcpyHostToDevice, stream), "cudaMemcpyAsync H→D");

        /// <summary>Enqueues a device→host copy on <paramref name="stream"/>.</summary>
        public static void CopyDeviceToHostAsync(nint destination, nint source, nuint count, nint stream)
            => Check(cudaMemcpyAsync(destination, source, count, cudaMemcpyDeviceToHost, stream), "cudaMemcpyAsync D→H");

        // Span-friendly async copies

        /// <summary>
        /// Enqueues upload of a host span to device on <paramref name="stream"/>.
        /// Caller must ensure <paramref name="source"/> stays pinned until the stream synchronizes.
        /// </summary>
        public static unsafe void UploadAsync(ReadOnlySpan<byte> source, nint deviceDst, nint stream)
        {
            fixed (byte* p = source)
                CopyHostToDeviceAsync(deviceDst, (nint)p, (nuint)source.Length, stream);
        }

        /// <summary>
        /// Enqueues download from device into a host span on <paramref name="stream"/>.
        /// Caller must ensure <paramref name="destination"/> stays pinned until the stream synchronizes.
        /// </summary>
        public static unsafe void DownloadAsync(nint deviceSrc, Span<byte> destination, nint stream)
        {
            fixed (byte* p = destination)
                CopyDeviceToHostAsync((nint)p, deviceSrc, (nuint)destination.Length, stream);
        }

        // Memset

        /// <summary>Fills device memory with a byte value (synchronous).</summary>
        public static void Memset(nint devicePtr, byte value, nuint count)
            => Check(cudaMemset(devicePtr, value, count), "cudaMemset");

        /// <summary>Enqueues a device memset on <paramref name="stream"/>.</summary>
        public static void MemsetAsync(nint devicePtr, byte value, nuint count, nint stream)
            => Check(cudaMemsetAsync(devicePtr, value, count, stream), "cudaMemsetAsync");

        // ──────────────────── Streams ────────────────────────────────────

        public static nint CreateStream()
        {
            Check(cudaStreamCreate(out nint stream), "cudaStreamCreate");
            return stream;
        }

        public static void SynchronizeStream(nint stream)
            => Check(cudaStreamSynchronize(stream), "cudaStreamSynchronize");

        public static void DestroyStream(nint stream)
            => Check(cudaStreamDestroy(stream), "cudaStreamDestroy");

        // ──────────────────── Events / Timing ────────────────────────────

        public static nint CreateEvent()
        {
            Check(cudaEventCreate(out nint ev), "cudaEventCreate");
            return ev;
        }

        public static void RecordEvent(nint @event, nint stream)
            => Check(cudaEventRecord(@event, stream), "cudaEventRecord");

        public static void SynchronizeEvent(nint @event)
            => Check(cudaEventSynchronize(@event), "cudaEventSynchronize");

        /// <summary>
        /// Returns elapsed time in milliseconds between two recorded events.
        /// Both events must have completed (<see cref="SynchronizeEvent"/> or stream sync).
        /// </summary>
        public static unsafe float ElapsedMilliseconds(nint start, nint end)
        {
            float ms = 0;
            Check(cudaEventElapsedTime(&ms, start, end), "cudaEventElapsedTime");
            return ms;
        }

        public static void DestroyEvent(nint @event)
            => Check(cudaEventDestroy(@event), "cudaEventDestroy");

        // ──────────────────── RAII wrappers ─────────────────────────────

        /// <summary>
        /// Stack-only RAII wrapper around a CUDA device allocation.
        /// Use with <c>using</c> to guarantee <c>cudaFree</c> on all exit paths.
        /// <code>
        /// using var buf = CudaInterop.DeviceBuffer.Allocate(size);
        /// CudaInterop.Upload(hostData, buf.Ptr);
        /// </code>
        /// </summary>
        public ref struct DeviceBuffer
        {
            /// <summary>Device-side pointer. <c>0</c> after disposal.</summary>
            public nint Ptr { get; private set; }

            /// <summary>Size in bytes that was allocated.</summary>
            public nuint Size { get; }

            private DeviceBuffer(nint ptr, nuint size)
            {
                Ptr = ptr;
                Size = size;
            }

            /// <summary>Allocate <paramref name="size"/> bytes of device memory.</summary>
            public static DeviceBuffer Allocate(nuint size, string context = "cudaMalloc")
                => new(Malloc(size, context), size);

            public void Dispose()
            {
                if (Ptr != 0)
                {
                    Free(Ptr);
                    Ptr = 0;
                }
            }
        }

        /// <summary>
        /// Stack-only RAII wrapper around a CUDA stream.
        /// Use with <c>using</c> to guarantee <c>cudaStreamDestroy</c> on all exit paths.
        /// <code>
        /// using var stream = CudaInterop.CudaStream.Create();
        /// CudaInterop.UploadAsync(data, devicePtr, stream.Handle);
        /// stream.Synchronize();
        /// </code>
        /// </summary>
        public ref struct CudaStream
        {
            /// <summary>Native stream handle. <c>0</c> after disposal.</summary>
            public nint Handle { get; private set; }

            private CudaStream(nint handle) => Handle = handle;

            public static CudaStream Create()
                => new(CreateStream());

            /// <summary>Block until all operations on this stream complete.</summary>
            public readonly void Synchronize()
                => SynchronizeStream(Handle);

            /// <summary>Record a timing event on this stream.</summary>
            public readonly void RecordEvent(nint @event)
                => CudaInterop.RecordEvent(@event, Handle);

            public void Dispose()
            {
                if (Handle != 0)
                {
                    DestroyStream(Handle);
                    Handle = 0;
                }
            }
        }

        /// <summary>
        /// Stack-only RAII wrapper around a CUDA event for GPU timing.
        /// <code>
        /// using var start = CudaInterop.CudaEvent.Create();
        /// using var end   = CudaInterop.CudaEvent.Create();
        /// start.Record(stream.Handle);
        /// /* … GPU work … */
        /// end.Record(stream.Handle);
        /// stream.Synchronize();
        /// float ms = start.ElapsedMs(end);
        /// </code>
        /// </summary>
        public ref struct CudaEvent
        {
            /// <summary>Native event handle. <c>0</c> after disposal.</summary>
            public nint Handle { get; private set; }

            private CudaEvent(nint handle) => Handle = handle;

            public static CudaEvent Create()
                => new(CreateEvent());

            public readonly void Record(nint stream)
                => CudaInterop.RecordEvent(Handle, stream);

            public readonly void Synchronize()
                => SynchronizeEvent(Handle);

            /// <summary>
            /// Returns elapsed milliseconds between this event and <paramref name="later"/>.
            /// Both events must have completed.
            /// </summary>
            public readonly float ElapsedMs(CudaEvent later)
                => ElapsedMilliseconds(Handle, later.Handle);

            public void Dispose()
            {
                if (Handle != 0)
                {
                    DestroyEvent(Handle);
                    Handle = 0;
                }
            }
        }
    }
}
