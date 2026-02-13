using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Silk.NET.Core.Native;
using Silk.NET.DirectStorage;

namespace XREngine.Core.Files;

/// <summary>
/// High-performance file I/O service with DirectStorage hardware acceleration.
/// <para>
/// <b>Single reads</b>: <see cref="ReadAllBytes"/>, <see cref="ReadRange"/> — read a single file
/// through DirectStorage with automatic fallback to <see cref="RandomAccess"/>.
/// </para>
/// <para>
/// <b>Batched reads</b>: <see cref="ReadBatch"/> — collects multiple file reads and submits them
/// in a single DirectStorage <c>Submit()</c> call, maximizing NVMe queue depth and throughput.
/// </para>
/// <para>
/// <b>Staging-pointer reads</b>: <see cref="TryReadInto"/> — reads file data directly into a
/// caller-provided unmanaged pointer (e.g., Vulkan mapped staging buffer), eliminating intermediate
/// managed allocations.
/// </para>
/// <para>
/// <b>GPU destinations</b>: DirectStorage's <c>DestinationBuffer</c> and <c>DestinationTextureRegion</c>
/// require <c>ID3D12Resource*</c> and are D3D12-only. Since this engine uses Vulkan, GPU-direct DMA
/// is not available. Instead, use <see cref="TryReadInto"/> to read directly into mapped Vulkan
/// staging buffers, which eliminates the managed-array intermediate copy.
/// </para>
/// </summary>
public static class DirectStorageIO
{
    private const int DefaultRequestTimeoutMilliseconds = 30000;
    private static readonly object QueueLock = new();
    private readonly record struct RuntimeState(bool Enabled, string Reason);

    private sealed class DirectStorageQueueContext(DStorage api, ComPtr<IDStorageFactory> factory, ComPtr<IDStorageQueue1> queue) : IDisposable
    {
        public DStorage Api { get; } = api;
        public ComPtr<IDStorageFactory> Factory { get; } = factory;
        public ComPtr<IDStorageQueue1> Queue { get; } = queue;

        public void Dispose()
        {
            Queue.Dispose();
            Factory.Dispose();
            Api.Dispose();
        }
    }

    private static readonly Lazy<RuntimeState> Runtime = new(DetectRuntime, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<DirectStorageQueueContext?> QueueContext = new(CreateQueueContext, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsEnabled => Runtime.Value.Enabled;

    public static string Status => Runtime.Value.Reason;

    // ────────────────────────────────────────────────────────────────
    //  Single-read API
    // ────────────────────────────────────────────────────────────────

    public static byte[] ReadAllBytes(string filePath)
    {
        long length = new FileInfo(filePath).Length;
        if (length == 0)
            return [];

        if (length > int.MaxValue)
            throw new IOException($"File '{filePath}' exceeds the maximum supported size for in-memory loading.");

        return ReadRange(filePath, 0, (int)length);
    }

    public static async Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        long length = new FileInfo(filePath).Length;
        if (length == 0)
            return [];

        if (length > int.MaxValue)
            throw new IOException($"File '{filePath}' exceeds the maximum supported size for in-memory loading.");

        return await ReadRangeAsync(filePath, 0, (int)length, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] ReadRange(string filePath, long offset, int length)
    {
        ValidateReadRequest(filePath, offset, length);
        if (length == 0)
            return [];

        if (TryReadRangeViaDirectStorage(filePath, offset, length, out byte[]? directStorageBytes))
            return directStorageBytes!;

        return ReadRangeFallback(filePath, offset, length);
    }

    public static async Task<byte[]> ReadRangeAsync(string filePath, long offset, int length, CancellationToken cancellationToken = default)
    {
        ValidateReadRequest(filePath, offset, length);
        if (length == 0)
            return [];

        if (TryReadRangeViaDirectStorage(filePath, offset, length, out byte[]? directStorageBytes, cancellationToken))
            return directStorageBytes!;

        return await ReadRangeFallbackAsync(filePath, offset, length, cancellationToken).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────
    //  Pointer-based read API (for staging buffer fills)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads file data directly into a caller-provided unmanaged buffer.
    /// Ideal for filling Vulkan mapped staging buffers without intermediate managed allocations.
    /// The caller must ensure <paramref name="destination"/> remains valid and the memory region
    /// of <paramref name="length"/> bytes is writable until this method returns.
    /// </summary>
    /// <returns><c>true</c> if DirectStorage was used; <c>false</c> if the fallback read was used.</returns>
    public static unsafe bool TryReadInto(string filePath, long offset, int length, void* destination, CancellationToken cancellationToken = default)
    {
        ValidateReadRequest(filePath, offset, length);
        if (length == 0)
            return true;

        if (destination == null)
            throw new ArgumentNullException(nameof(destination));

        if (TryReadIntoViaDirectStorage(filePath, offset, length, destination, cancellationToken))
            return true;

        ReadIntoFallback(filePath, offset, length, destination);
        return false;
    }

    /// <summary>
    /// Reads an entire file directly into a caller-provided unmanaged buffer.
    /// </summary>
    public static unsafe bool TryReadFileInto(string filePath, void* destination, int destinationSize, CancellationToken cancellationToken = default)
    {
        long fileLength = new FileInfo(filePath).Length;
        if (fileLength == 0)
            return true;

        if (fileLength > destinationSize)
            throw new IOException($"File '{filePath}' ({fileLength} bytes) exceeds destination buffer size ({destinationSize} bytes).");

        return TryReadInto(filePath, 0, (int)fileLength, destination, cancellationToken);
    }

    // ────────────────────────────────────────────────────────────────
    //  Batch read API
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Collects multiple file read requests and submits them to DirectStorage in a single
    /// <c>Submit()</c> call. This maximizes NVMe queue depth, allowing the storage controller
    /// to reorder and coalesce I/O operations for peak throughput.
    /// <para>
    /// Usage pattern:
    /// <code>
    /// using var batch = new DirectStorageIO.ReadBatch();
    /// int idx0 = batch.AddFile("texture1.png");
    /// int idx1 = batch.AddFile("texture2.png");
    /// int idx2 = batch.AddFile("mesh.bin");
    /// batch.Execute();
    /// byte[] tex1Data = batch.GetResult(idx0);
    /// byte[] tex2Data = batch.GetResult(idx1);
    /// byte[] meshData = batch.GetResult(idx2);
    /// </code>
    /// </para>
    /// </summary>
    public sealed class ReadBatch : IDisposable
    {
        private readonly record struct ReadRequest(string FilePath, long Offset, int Length);

        private readonly List<ReadRequest> _requests = [];
        private byte[]?[]? _results;
        private bool _submitted;
        private bool _disposed;

        /// <summary>
        /// Gets the number of reads queued in this batch.
        /// </summary>
        public int Count => _requests.Count;

        /// <summary>
        /// Adds a full-file read to the batch.
        /// </summary>
        /// <returns>An index to retrieve the result after <see cref="Execute"/>.</returns>
        public int AddFile(string filePath)
        {
            ThrowIfSubmitted();

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            long length = new FileInfo(filePath).Length;
            if (length == 0)
            {
                int emptyIndex = _requests.Count;
                _requests.Add(new ReadRequest(filePath, 0, 0));
                return emptyIndex;
            }

            if (length > int.MaxValue)
                throw new IOException($"File '{filePath}' exceeds the maximum supported size for batch loading.");

            int index = _requests.Count;
            _requests.Add(new ReadRequest(filePath, 0, (int)length));
            return index;
        }

        /// <summary>
        /// Adds a ranged file read to the batch.
        /// </summary>
        public int AddRange(string filePath, long offset, int length)
        {
            ThrowIfSubmitted();
            ValidateReadRequest(filePath, offset, length);
            int index = _requests.Count;
            _requests.Add(new ReadRequest(filePath, offset, length));
            return index;
        }

        /// <summary>
        /// Submits all queued reads and blocks until all complete.
        /// </summary>
        /// <returns><c>true</c> if DirectStorage was used; <c>false</c> if fallback I/O was used.</returns>
        public bool Execute(CancellationToken cancellationToken = default)
        {
            ThrowIfSubmitted();
            _submitted = true;
            _results = new byte[_requests.Count][];

            // Pre-fill zero-length requests.
            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i].Length == 0)
                    _results[i] = [];
            }

            if (_requests.Count == 0)
                return true;

            if (TryExecuteViaDirectStorage(cancellationToken))
                return true;

            ExecuteFallback(cancellationToken);
            return false;
        }

        /// <summary>
        /// Submits all queued reads asynchronously. The DirectStorage portion is synchronous
        /// (hardware-queued), but fallback I/O uses async file reads.
        /// </summary>
        public async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfSubmitted();
            _submitted = true;
            _results = new byte[_requests.Count][];

            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i].Length == 0)
                    _results[i] = [];
            }

            if (_requests.Count == 0)
                return true;

            if (TryExecuteViaDirectStorage(cancellationToken))
                return true;

            await ExecuteFallbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        /// <summary>
        /// Gets the result for a previously added request.
        /// Must be called after <see cref="Execute"/> or <see cref="ExecuteAsync"/>.
        /// </summary>
        public byte[] GetResult(int index)
        {
            if (!_submitted)
                throw new InvalidOperationException("Batch has not been submitted. Call Execute() first.");

            if (_results is null || (uint)index >= (uint)_results.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _results[index] ?? throw new InvalidOperationException(
                $"Result at index {index} is null. This may indicate a DirectStorage failure that was silently handled by fallback.");
        }

        /// <summary>
        /// Tries to get the result for a given index. Returns false if the result is unavailable.
        /// </summary>
        public bool TryGetResult(int index, out byte[]? data)
        {
            data = null;
            if (!_submitted || _results is null || (uint)index >= (uint)_results.Length)
                return false;

            data = _results[index];
            return data is not null;
        }

        private unsafe bool TryExecuteViaDirectStorage(CancellationToken ct)
        {
            if (!IsEnabled)
                return false;

            var context = QueueContext.Value;
            if (context is null)
                return false;

            // Count non-empty requests.
            int liveCount = 0;
            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i].Length > 0)
                    liveCount++;
            }

            if (liveCount == 0)
                return true;

            var handles = new GCHandle[_requests.Count];
            var files = new ComPtr<IDStorageFile>[_requests.Count];

            lock (QueueLock)
            {
                try
                {
                    // Phase 1: Allocate buffers, pin them, and open DirectStorage file handles.
                    for (int i = 0; i < _requests.Count; i++)
                    {
                        var req = _requests[i];
                        if (req.Length == 0)
                            continue;

                        _results![i] = GC.AllocateUninitializedArray<byte>(req.Length);
                        handles[i] = GCHandle.Alloc(_results[i], GCHandleType.Pinned);
                        files[i] = context.Factory.OpenFile<IDStorageFile>(req.FilePath);
                        if (files[i].Handle == null)
                            return false; // Can't open file — fall back entirely.
                    }

                    // Phase 2: Enqueue all requests into the DirectStorage queue.
                    for (int i = 0; i < _requests.Count; i++)
                    {
                        var req = _requests[i];
                        if (req.Length == 0)
                            continue;

                        byte* outputPtr = (byte*)handles[i].AddrOfPinnedObject();

                        Request dsRequest = default;
                        dsRequest.Options = new RequestOptions
                        {
                            SourceType = RequestSourceType.File,
                            DestinationType = RequestDestinationType.Memory,
                            CompressionFormat = CompressionFormat.CompressionFormatNone,
                        };
                        dsRequest.Source = new Source
                        {
                            File = new SourceFile
                            {
                                Source = files[i].Handle,
                                Offset = (ulong)req.Offset,
                                Size = (uint)req.Length,
                            }
                        };
                        dsRequest.Destination = new Destination
                        {
                            Memory = new DestinationMemory
                            {
                                Buffer = outputPtr,
                                Size = (uint)req.Length,
                            }
                        };
                        dsRequest.UncompressedSize = (uint)req.Length;

                        context.Queue.EnqueueRequest(dsRequest);
                    }

                    // Phase 3: Single submit for the entire batch + wait for completion.
                    using var completedEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
                    void* eventHandle = (void*)completedEvent.SafeWaitHandle.DangerousGetHandle();
                    context.Queue.EnqueueSetEvent(eventHandle);
                    context.Queue.Submit();

                    int timeoutMs = ResolveTimeoutMilliseconds();
                    int signalIndex;
                    if (ct.CanBeCanceled)
                    {
                        signalIndex = WaitHandle.WaitAny([completedEvent, ct.WaitHandle], timeoutMs);
                        if (signalIndex == 1)
                            ct.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        bool signaled = completedEvent.WaitOne(timeoutMs);
                        signalIndex = signaled ? 0 : WaitHandle.WaitTimeout;
                    }

                    if (signalIndex == WaitHandle.WaitTimeout)
                        return false;

                    // Phase 4: Verify the batch completed without errors.
                    ErrorRecord errorRecord = default;
                    context.Queue.RetrieveErrorRecord(ref errorRecord);
                    if (errorRecord.FailureCount > 0)
                        return false;

                    return true;
                }
                finally
                {
                    for (int i = 0; i < files.Length; i++)
                        files[i].Dispose();

                    for (int i = 0; i < handles.Length; i++)
                    {
                        if (handles[i].IsAllocated)
                            handles[i].Free();
                    }
                }
            }
        }

        private void ExecuteFallback(CancellationToken ct)
        {
            for (int i = 0; i < _requests.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var req = _requests[i];
                if (req.Length == 0)
                    continue;

                _results![i] = ReadRangeFallback(req.FilePath, req.Offset, req.Length);
            }
        }

        private async Task ExecuteFallbackAsync(CancellationToken ct)
        {
            // Run fallback reads concurrently for maximum throughput.
            var tasks = new Task[_requests.Count];
            for (int i = 0; i < _requests.Count; i++)
            {
                var req = _requests[i];
                if (req.Length == 0)
                {
                    tasks[i] = Task.CompletedTask;
                    continue;
                }

                int capturedIndex = i;
                tasks[i] = Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    _results![capturedIndex] = ReadRangeFallback(req.FilePath, req.Offset, req.Length);
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private void ThrowIfSubmitted()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_submitted)
                throw new InvalidOperationException("Batch has already been submitted.");
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Internal — fallback I/O
    // ────────────────────────────────────────────────────────────────

    internal static byte[] ReadRangeFallback(string filePath, long offset, int length)
    {
        byte[] data = GC.AllocateUninitializedArray<byte>(length);
        using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = RandomAccess.Read(handle, data.AsSpan(totalRead), offset + totalRead);
            if (read <= 0)
                throw new EndOfStreamException($"Unexpected EOF while reading '{filePath}' at offset {offset + totalRead}.");

            totalRead += read;
        }

        return data;
    }

    internal static async Task<byte[]> ReadRangeFallbackAsync(string filePath, long offset, int length, CancellationToken cancellationToken)
    {
        byte[] data = GC.AllocateUninitializedArray<byte>(length);
        using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous | FileOptions.RandomAccess);
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await RandomAccess.ReadAsync(handle, data.AsMemory(totalRead), offset + totalRead, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException($"Unexpected EOF while reading '{filePath}' at offset {offset + totalRead}.");

            totalRead += read;
        }

        return data;
    }

    private static unsafe void ReadIntoFallback(string filePath, long offset, int length, void* destination)
    {
        using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        int totalRead = 0;
        while (totalRead < length)
        {
            var span = new Span<byte>((byte*)destination + totalRead, length - totalRead);
            int read = RandomAccess.Read(handle, span, offset + totalRead);
            if (read <= 0)
                throw new EndOfStreamException($"Unexpected EOF while reading '{filePath}' at offset {offset + totalRead}.");

            totalRead += read;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Internal — DirectStorage single-request path
    // ────────────────────────────────────────────────────────────────

    private static unsafe bool TryReadRangeViaDirectStorage(string filePath, long offset, int length, out byte[]? data, CancellationToken cancellationToken = default)
    {
        data = null;
        if (!IsEnabled)
            return false;

        var context = QueueContext.Value;
        if (context is null)
            return false;

        if (offset < 0)
            return false;

        if (length > uint.MaxValue)
            return false;

        lock (QueueLock)
        {
            using ComPtr<IDStorageFile> file = context.Factory.OpenFile<IDStorageFile>(filePath);
            if (file.Handle == null)
                return false;

            byte[] output = GC.AllocateUninitializedArray<byte>(length);
            using var completedEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

            // Pin the output buffer for the entire duration of the async DirectStorage operation.
            // Using GCHandle instead of 'fixed' because the buffer must remain pinned across
            // the Submit() call and the subsequent WaitOne() — DirectStorage writes asynchronously.
            var pinHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
            try
            {
                byte* outputPtr = (byte*)pinHandle.AddrOfPinnedObject();

                Request request = default;
                request.Options = new RequestOptions
                {
                    SourceType = RequestSourceType.File,
                    DestinationType = RequestDestinationType.Memory,
                    CompressionFormat = CompressionFormat.CompressionFormatNone,
                };
                request.Source = new Source
                {
                    File = new SourceFile
                    {
                        Source = file.Handle,
                        Offset = (ulong)offset,
                        Size = (uint)length,
                    }
                };
                request.Destination = new Destination
                {
                    Memory = new DestinationMemory
                    {
                        Buffer = outputPtr,
                        Size = (uint)length,
                    }
                };
                request.UncompressedSize = (uint)length;

                context.Queue.EnqueueRequest(request);

                void* completedHandle = (void*)completedEvent.SafeWaitHandle.DangerousGetHandle();
                context.Queue.EnqueueSetEvent(completedHandle);
                context.Queue.Submit();

                // Wait for completion while the buffer is still pinned.
                int timeoutMs = ResolveTimeoutMilliseconds();
                int signalIndex;
                if (cancellationToken.CanBeCanceled)
                {
                    signalIndex = WaitHandle.WaitAny([completedEvent, cancellationToken.WaitHandle], timeoutMs);
                    if (signalIndex == 1)
                        cancellationToken.ThrowIfCancellationRequested();
                }
                else
                {
                    bool signaled = completedEvent.WaitOne(timeoutMs);
                    signalIndex = signaled ? 0 : WaitHandle.WaitTimeout;
                }

                if (signalIndex == WaitHandle.WaitTimeout)
                    return false;

                // Verify the request actually succeeded — DirectStorage can signal
                // completion even on failure, so check for queued errors.
                ErrorRecord errorRecord = default;
                context.Queue.RetrieveErrorRecord(ref errorRecord);
                if (errorRecord.FailureCount > 0)
                    return false;

                data = output;
                return true;
            }
            finally
            {
                pinHandle.Free();
            }
        }
    }

    private static unsafe bool TryReadIntoViaDirectStorage(string filePath, long offset, int length, void* destination, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return false;

        var context = QueueContext.Value;
        if (context is null)
            return false;

        if (offset < 0 || length > uint.MaxValue)
            return false;

        lock (QueueLock)
        {
            using ComPtr<IDStorageFile> file = context.Factory.OpenFile<IDStorageFile>(filePath);
            if (file.Handle == null)
                return false;

            using var completedEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

            Request request = default;
            request.Options = new RequestOptions
            {
                SourceType = RequestSourceType.File,
                DestinationType = RequestDestinationType.Memory,
                CompressionFormat = CompressionFormat.CompressionFormatNone,
            };
            request.Source = new Source
            {
                File = new SourceFile
                {
                    Source = file.Handle,
                    Offset = (ulong)offset,
                    Size = (uint)length,
                }
            };
            request.Destination = new Destination
            {
                Memory = new DestinationMemory
                {
                    Buffer = destination,
                    Size = (uint)length,
                }
            };
            request.UncompressedSize = (uint)length;

            context.Queue.EnqueueRequest(request);

            void* eventHandle = (void*)completedEvent.SafeWaitHandle.DangerousGetHandle();
            context.Queue.EnqueueSetEvent(eventHandle);
            context.Queue.Submit();

            int timeoutMs = ResolveTimeoutMilliseconds();
            int signalIndex;
            if (cancellationToken.CanBeCanceled)
            {
                signalIndex = WaitHandle.WaitAny([completedEvent, cancellationToken.WaitHandle], timeoutMs);
                if (signalIndex == 1)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                bool signaled = completedEvent.WaitOne(timeoutMs);
                signalIndex = signaled ? 0 : WaitHandle.WaitTimeout;
            }

            if (signalIndex == WaitHandle.WaitTimeout)
                return false;

            ErrorRecord errorRecord = default;
            context.Queue.RetrieveErrorRecord(ref errorRecord);
            return errorRecord.FailureCount == 0;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Internal — configuration and runtime detection
    // ────────────────────────────────────────────────────────────────

    private static int ResolveTimeoutMilliseconds()
    {
        string? envValue = Environment.GetEnvironmentVariable("XRE_DIRECTSTORAGE_TIMEOUT_MS");
        if (int.TryParse(envValue, out int configured) && configured > 0)
            return configured;

        return DefaultRequestTimeoutMilliseconds;
    }

    private static void ValidateReadRequest(string filePath, long offset, int length)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
    }

    private static RuntimeState DetectRuntime()
    {
        string? overrideValue = Environment.GetEnvironmentVariable("XRE_DIRECTSTORAGE_ENABLED");
        if (!string.IsNullOrWhiteSpace(overrideValue) &&
            (string.Equals(overrideValue, "0", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(overrideValue, "false", StringComparison.OrdinalIgnoreCase)))
        {
            return new RuntimeState(false, "disabled-by-env");
        }

        if (!OperatingSystem.IsWindows())
            return new RuntimeState(false, "unsupported-platform");

        if (NativeLibrary.TryLoad("dstoragecore.dll", out nint handleCore))
        {
            NativeLibrary.Free(handleCore);
            return new RuntimeState(true, "dstoragecore");
        }

        if (NativeLibrary.TryLoad("dstorage.dll", out nint handleLegacy))
        {
            NativeLibrary.Free(handleLegacy);
            return new RuntimeState(true, "dstorage");
        }

        return new RuntimeState(false, "runtime-missing");
    }

    private static unsafe DirectStorageQueueContext? CreateQueueContext()
    {
        if (!Runtime.Value.Enabled)
            return null;

        try
        {
            DStorage api = DStorage.GetApi();
            ComPtr<IDStorageFactory> factory = api.GetFactory<IDStorageFactory>();
            if (factory.Handle == null)
            {
                api.Dispose();
                return null;
            }

            QueueDesc queueDesc = default;
            queueDesc.SourceType = RequestSourceType.File;
            queueDesc.Capacity = (ushort)Math.Clamp(Environment.ProcessorCount * 8, DStorage.MinQueueCapacity, DStorage.MaxQueueCapacity);
            queueDesc.Priority = Priority.Normal;
            queueDesc.Device = null;
            queueDesc.Name = null;

            ComPtr<IDStorageQueue1> queue = factory.CreateQueue<IDStorageQueue1>(queueDesc);
            if (queue.Handle == null)
            {
                factory.Dispose();
                api.Dispose();
                return null;
            }

            return new DirectStorageQueueContext(api, factory, queue);
        }
        catch
        {
            return null;
        }
    }
}
