using XREngine.Extensions;
using Silk.NET.OpenGL;
using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using System.Buffers;
using System.Linq;
using System.Collections.Concurrent;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLDataBuffer(OpenGLRenderer renderer, XRDataBuffer buffer) : GLObject<XRDataBuffer>(renderer, buffer), IApiDataBuffer
        {
            private static readonly ConcurrentDictionary<string, byte> _missingInterleavedLogs = new();

            /// <summary>
            /// Tracks the currently allocated GPU memory size for this buffer in bytes.
            /// </summary>
            private long _allocatedVRAMBytes = 0;
            internal long AllocatedVRAMBytes => _allocatedVRAMBytes;

            /// <summary>
            /// Cache of program binding ID -> SSBO resource index to avoid expensive glGetProgramResourceIndex calls every frame.
            /// </summary>
            private readonly Dictionary<uint, uint> _ssboResourceIndexCache = [];

            protected override void UnlinkData()
            {
                Data.PushDataRequested -= PushData;
                Data.PushSubDataRequested -= PushSubData;
                Data.FlushRequested -= Flush;
                Data.FlushRangeRequested -= FlushRange;
                Data.SetBlockNameRequested -= SetUniformBlockName;
                Data.SetBlockIndexRequested -= SetBlockIndex;
                Data.BindRequested -= Bind;
                Data.UnbindRequested -= Unbind;
                Data.MapBufferDataRequested -= MapBufferData;
                Data.UnmapBufferDataRequested -= UnmapBufferData;
                Data.BindSSBORequested -= BindSSBO;
            }
            private static bool IsGpuBufferLoggingEnabled()
                => RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;

            protected override void LinkData()
            {
                Data.PushDataRequested += PushData;
                Data.PushSubDataRequested += PushSubData;
                Data.FlushRequested += Flush;
                Data.FlushRangeRequested += FlushRange;
                Data.SetBlockNameRequested += SetUniformBlockName;
                Data.SetBlockIndexRequested += SetBlockIndex;
                Data.BindRequested += Bind;
                Data.UnbindRequested += Unbind;
                Data.MapBufferDataRequested += MapBufferData;
                Data.UnmapBufferDataRequested += UnmapBufferData;
                Data.BindSSBORequested += BindSSBO;
            }

            public override EGLObjectType Type => EGLObjectType.Buffer;

            protected internal override void PostGenerated()
            {
                var rend = Renderer.ActiveMeshRenderer;
                // Attribute binding must be driven by GLMeshRenderer.BindBuffers(), which knows the
                // owning VAO/program pair. Async mesh generation can happen while an unrelated renderer
                // is active, and eagerly binding here pollutes that VAO and emits bogus missing-attribute logs.
                if (rend is null && Data.Target == EBufferTarget.ArrayBuffer && IsGpuBufferLoggingEnabled())
                {
                    // Suppress noisy warning; atlas / generic buffers can legitimately be created before a renderer binds them.
                    Debug.OpenGL($"{GetDescribingName()} generated (no active mesh renderer yet) – delaying attribute binding.");
                }

                if (Data.Resizable && !ShouldUseImmutableStorage())
                {
                    // Use dynamic mutable allocation path.
                    LogFirstUseAudit("Resizable");
                    PushData();
                }
                else if (_hasPendingUpload)
                {
                    // GLUploadQueue generated the object and will allocate/upload it in budgeted chunks.
                    LogFirstUseAudit("PendingQueued");
                }
                else if (Data.Length > AsyncUploadThreshold
                    && Renderer.UploadQueue.Enabled
                    && Data.TryGetAddress(out var sourceAddress)
                    && sourceAddress != VoidPtr.Zero)
                {
                    LogFirstUseAudit("Queued");
                    PushDataQueued();
                }
                else
                {
                    LogFirstUseAudit("ImmutableSync");
                    AllocateImmutable();
                    _lastPushedLength = Data.Length;
                }
            }

            // Phase D audit: log buffer size and routing decision on first
            // PostGenerated. One line per buffer; emit to log_opengl.txt so
            // a cold-start grep can produce a route-by-size histogram and
            // identify any meaningful number of <AsyncUploadThreshold
            // buffers slipping into the synchronous ImmutableSync route.
            private void LogFirstUseAudit(string route)
            {
                Debug.OpenGL(
                    $"[BufferUploadAudit] route={route} " +
                    $"sizeBytes={Data.Length} " +
                    $"thresholdBytes={AsyncUploadThreshold} " +
                    $"target={Data.Target} " +
                    $"attribute='{Data.AttributeName ?? string.Empty}' " +
                    $"resizable={Data.Resizable} " +
                    $"immutableStorage={ShouldUseImmutableStorage()} " +
                    $"queueEnabled={Renderer.UploadQueue.Enabled} " +
                    $"name='{GetDescribingName()}'.");
            }

            private string RangeFlagsString() => Data.RangeFlags.ToString();
            private string StorageFlagsString() => Data.StorageFlags.ToString();
            private string BufferNameOrTarget() => string.IsNullOrWhiteSpace(Data.AttributeName) ? Data.Target.ToString() : Data.AttributeName;

            public void BindToRenderer(GLRenderProgram vertexProgram, GLMeshRenderer? arrayBufferLink, bool pushDataNow = true)
            {
                // Profiler instrumentation removed from this hot path - called per buffer per mesh
                try
                {
                    BindV2(vertexProgram, arrayBufferLink);
                }
                catch (Exception e)
                {
                    Debug.OpenGLException(e, "Error binding buffer.");
                }
            }

            public bool TryGetAttributeLocation(GLRenderProgram vertexProgram, out uint layoutLocation)
            {
                layoutLocation = GetAttributeLocation(vertexProgram, Data.AttributeName ?? string.Empty);
                return layoutLocation != uint.MaxValue;
            }

            private void BindV2(GLRenderProgram vertexProgram, GLMeshRenderer? arrayBufferLink)
            {
                // Profiler instrumentation removed from this hot path
                if (vertexProgram is null)
                {
                    Debug.OpenGLWarning("[GLDataBuffer] Cannot bind buffer without an active GLRenderProgram.");
                    return;
                }

                switch (Data.Target)
                {
                    case EBufferTarget.ArrayBuffer:
                        {
                            // Profiler instrumentation removed from this hot path
                            uint vaoId = arrayBufferLink?.BindingId ?? 0;
                            if (vaoId == 0)
                            {
                                Debug.OpenGLWarning($"Failed to bind buffer {GetDescribingName()} to mesh renderer.");
                                return;
                            }

                            uint bindingIndex = uint.MaxValue;
                            if (Data.BindingIndexOverride.HasValue)
                                bindingIndex = Data.BindingIndexOverride.Value;
                            else if (!TryGetAttributeLocation(vertexProgram, out bindingIndex))
                            {
                                string programName = vertexProgram.Data?.Name ?? "<unnamed>";
                                Debug.OpenGL($"[GLDataBuffer] Attribute '{Data.AttributeName}' missing in program '{programName}' while binding buffer '{GetDescribingName()}'.");
                                return;
                            }

                            if (Data.InterleavedAttributes.Length > 0)
                            {
                                // Handle interleaved vertex attributes
                                foreach (var (attribIndexOverride, name, offset, componentType, componentCount, integral) in Data.InterleavedAttributes)
                                {
                                    uint attribIndex = attribIndexOverride ?? GetAttributeLocation(vertexProgram, name);
                                    if (attribIndex != uint.MaxValue)
                                    {
                                        Api.EnableVertexArrayAttrib(vaoId, attribIndex);
                                        Api.VertexArrayBindingDivisor(vaoId, attribIndex, Data.InstanceDivisor);
                                        Api.VertexArrayAttribBinding(vaoId, attribIndex, bindingIndex); // Use same binding point
                                        if (integral)
                                            Api.VertexArrayAttribIFormat(vaoId, attribIndex, (int)componentCount, GLEnum.Byte + (int)componentType, offset);
                                        else
                                            Api.VertexArrayAttribFormat(vaoId, attribIndex, (int)componentCount, GLEnum.Byte + (int)componentType, Data.Normalize, offset);
                                    }
                                    else
                                    {
                                        string programName = vertexProgram.Data?.Name ?? "<unnamed>";
                                        string shaderNames = vertexProgram.Data?.Shaders is { Count: > 0 }
                                            ? string.Join(", ", vertexProgram.Data.Shaders.Select(s => s?.Name ?? s?.FilePath ?? "<unnamed>"))
                                            : "<no shaders>";
                                        string key = $"{vertexProgram.BindingId}:{programName}:{name}:{GetDescribingName()}";
                                        if (_missingInterleavedLogs.TryAdd(key, 0))
                                            Debug.OpenGL($"[GLDataBuffer] Interleaved attribute '{name}' missing in program '{programName}' (id {vertexProgram.BindingId}) for buffer '{GetDescribingName()}'. Shaders: [{shaderNames}]");
                                    }
                                }
                                // Bind the interleaved buffer once
                                Api.VertexArrayVertexBuffer(vaoId, bindingIndex, BindingId, 0, Data.ElementSize);
                            }
                            else
                            {
                                int componentType = (int)Data.ComponentType;
                                uint componentCount = Data.ComponentCount;
                                bool integral = Data.Integral;

                                // Original non-interleaved path
                                Api.EnableVertexArrayAttrib(vaoId, bindingIndex);
                                Api.VertexArrayBindingDivisor(vaoId, bindingIndex, Data.InstanceDivisor);
                                Api.VertexArrayAttribBinding(vaoId, bindingIndex, bindingIndex);
                                if (integral)
                                    Api.VertexArrayAttribIFormat(vaoId, bindingIndex, (int)componentCount, GLEnum.Byte + componentType, 0);
                                else
                                    Api.VertexArrayAttribFormat(vaoId, bindingIndex, (int)componentCount, GLEnum.Byte + componentType, Data.Normalize, 0);
                                Api.VertexArrayVertexBuffer(vaoId, bindingIndex, BindingId, 0, Data.ElementSize);
                            }
                        }
                        break;
                    case EBufferTarget.ShaderStorageBuffer:
                    case EBufferTarget.UniformBuffer:
                        {
                            // Profiler instrumentation removed from this hot path
                            uint bindingIndex = uint.MaxValue;
                            if (Data.BindingIndexOverride.HasValue)
                                bindingIndex = Data.BindingIndexOverride.Value;
                            else if (!TryGetAttributeLocation(vertexProgram, out bindingIndex))
                            {
                                return;
                            }

                            Bind();
                            Api.BindBufferBase(ToGLEnum(Data.Target), bindingIndex, BindingId);
                            Unbind();
                            break;
                        }
                }
            }

            private uint GetAttributeLocation(GLRenderProgram vertexProgram, string attributeName)
            {
                uint index = 0u;

                if (string.IsNullOrWhiteSpace(attributeName))
                {
                    Debug.OpenGLWarning($"{GetDescribingName()} has no attribute name.");
                    return uint.MaxValue;
                }

                switch (Data.Target)
                {
                    case EBufferTarget.ArrayBuffer:
                        int location = vertexProgram.GetAttributeLocation(attributeName);
                        if (location >= 0)
                            index = (uint)location;
                        else
                            return uint.MaxValue;
                        break;
                    case EBufferTarget.ShaderStorageBuffer:
                        index = Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(vertexProgram.BindingId, GLEnum.ShaderStorageBlock, attributeName);
                        break;
                    case EBufferTarget.UniformBuffer:
                        index = Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(vertexProgram.BindingId, GLEnum.UniformBlock, attributeName);
                        break;
                    default:
                        return uint.MaxValue;
                }

                return index;
            }

            private uint _lastPushedLength = 0u;

            private bool ShouldUseImmutableStorage()
                => Data.StorageFlags != 0 ||
                   Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Persistent) ||
                   Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Coherent);

            private bool AllowsUpdatesWhileMapped()
                => Data.StorageFlags.HasFlag(EBufferMapStorageFlags.Persistent) ||
                   Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Persistent);

            private bool HasBlockingActiveMapping()
                => Data.ActivelyMapping.Contains(this) && !AllowsUpdatesWhileMapped();

            /// <summary>
            /// Threshold above which frame-budgeted uploads are used (64 KB).
            /// Smaller buffers use synchronous upload as the overhead isn't worth it.
            /// </summary>
            private const uint AsyncUploadThreshold = 64 * 1024;

            /// <summary>
            /// Allocates and pushes the buffer to the GPU.
            /// Uses frame-budgeted uploads for large buffers to prevent FPS stalls.
            /// </summary>
            public void PushData()
            {
                if (HasBlockingActiveMapping())
                    return;

                uint dataLength = Data.Length;

                // Use frame-budgeted path for large buffers (even when on render thread)
                // This spreads upload work across multiple frames to prevent stalls
                if (dataLength > AsyncUploadThreshold && Renderer.UploadQueue.Enabled)
                {
                    PushDataQueued();
                    return;
                }

                // Synchronous path for small buffers or when upload queue is disabled
                if (RuntimeEngine.InvokeOnMainThread(PushData, "GLDataBuffer.PushData"))
                    return;

                PushDataImmediate();
            }

            /// <summary>
            /// Queues the buffer data for frame-budgeted upload.
            /// Can be called from any thread.
            /// </summary>
            private void PushDataQueued()
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLDataBuffer.PushDataQueued");

                uint dataLength = Data.Length;
                if (dataLength == 0)
                    return;

                // Get source data address
                if (!Data.TryGetAddress(out var sourceAddress) || sourceAddress == VoidPtr.Zero)
                {
                    // Fallback to sync path if no data
                    if (RuntimeEngine.IsRenderThread)
                        PushDataImmediate();
                    else
                        RuntimeEngine.EnqueueMainThreadTask(PushDataImmediate, "GLDataBuffer.PushData.Fallback");
                    return;
                }

                if (!TryBeginQueuedUpload())
                    return;

                void* srcPtr = sourceAddress.Pointer;

                void EnqueueCopy()
                {
                    byte[]? dataCopy = null;
                    try
                    {
                        // Copy data to a managed snapshot; no GL calls are made off the render thread.
                        dataCopy = ArrayPool<byte>.Shared.Rent(checked((int)dataLength));
                        fixed (byte* dest = dataCopy)
                        {
                            System.Buffer.MemoryCopy(srcPtr, dest, dataLength, dataLength);
                        }

                        Renderer.UploadQueue.EnqueueUpload(this, dataCopy, dataLength, returnDataToPool: true);
                        CompleteQueuedCopy();
                        dataCopy = null;
                    }
                    catch (Exception ex)
                    {
                        if (dataCopy is not null)
                            ArrayPool<byte>.Shared.Return(dataCopy);

                        CancelQueuedUpload();
                        Debug.OpenGLWarning($"[GLDataBuffer] Failed to snapshot queued upload for '{GetDescribingName()}' ({dataLength} bytes): {ex.GetType().Name}: {ex.Message}. Falling back to immediate upload.");
                        RuntimeEngine.EnqueueMainThreadTask(PushDataImmediate, "GLDataBuffer.PushDataQueued.Fallback");
                    }
                }

                // The source pointer belongs to XRDataBuffer.ClientSideSource. That memory can be
                // resized or disposed by the owning buffer, so do not capture it for a later
                // thread-pool copy. Snapshot the client bytes immediately, then let the upload
                // queue budget only the GL work.
                EnqueueCopy();
            }

            /// <summary>
            /// Performs the actual synchronous GPU upload. Must be called on the render thread.
            /// </summary>
            private void PushDataImmediate()
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLDataBuffer.PushDataImmediate");

                bool shouldUseImmutableStorage = ShouldUseImmutableStorage();
                bool remapAfterUpload = Data.ActivelyMapping.Contains(this);

                if (!RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(Data.Length, _allocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
                {
                    Debug.OpenGLWarning($"[VRAM Budget] Skipping buffer allocation for '{GetDescribingName()}' ({Data.Length} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes.");
                    return;
                }

                void* addr = (Data.TryGetAddress(out var address) ? address : VoidPtr.Zero).Pointer;

                if (shouldUseImmutableStorage)
                {
                    if (_immutableStorageSet && Data.Length == _lastPushedLength)
                    {
                        if (!Data.StorageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage))
                        {
                            if (remapAfterUpload)
                                UnmapBufferData();

                            RecreateBuffer();
                            AllocateImmutable();

                            if (remapAfterUpload)
                                MapBufferData();
                        }
                        else
                        {
                            Api.NamedBufferSubData(BindingId, 0, Data.Length, addr);
                        }

                        _lastPushedLength = Data.Length;

                        if (Data.DisposeOnPush)
                            Data.Dispose();

                        return;
                    }

                    if (remapAfterUpload)
                        UnmapBufferData();

                    if (_immutableStorageSet || _lastPushedLength > 0)
                        RecreateBuffer();

                    AllocateImmutable();
                    _lastPushedLength = Data.Length;

                    if (remapAfterUpload)
                        MapBufferData();

                    if (Data.DisposeOnPush)
                        Data.Dispose();

                    return;
                }

                // Track VRAM deallocation of previous buffer if any
                if (_allocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                // If the GL buffer was allocated with glNamedBufferStorage (immutable),
                // glNamedBufferData is illegal. Delete and recreate as a mutable buffer.
                if (_immutableStorageSet)
                    RecreateBufferAsMutable();

                Api.NamedBufferData(BindingId, Data.Length, addr, ToGLEnum(Data.Usage));
                _lastPushedLength = Data.Length;

                // Track VRAM allocation
                _allocatedVRAMBytes = Data.Length;
                RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(_allocatedVRAMBytes);

                if (Data.DisposeOnPush)
                    Data.Dispose();
            }

            /// <summary>
            /// Sets the last pushed length. Used by the upload queue.
            /// </summary>
            internal void SetLastPushedLength(uint length)
            {
                _lastPushedLength = length;
            }

            private readonly object _queuedUploadSync = new();
            private bool _queuedCopyInFlight;
            private bool _queuedUploadDirty;

            private bool TryBeginQueuedUpload()
            {
                lock (_queuedUploadSync)
                {
                    if (_queuedCopyInFlight || _hasPendingUpload)
                    {
                        _queuedUploadDirty = true;
                        return false;
                    }

                    _queuedCopyInFlight = true;
                    _hasPendingUpload = true;
                    return true;
                }
            }

            private void CompleteQueuedCopy()
            {
                lock (_queuedUploadSync)
                    _queuedCopyInFlight = false;
            }

            private void CancelQueuedUpload()
            {
                lock (_queuedUploadSync)
                {
                    _queuedCopyInFlight = false;
                    _queuedUploadDirty = false;
                    _hasPendingUpload = false;
                }
            }

            internal void CompleteQueuedUpload(uint length)
            {
                bool uploadAgain;
                lock (_queuedUploadSync)
                {
                    _lastPushedLength = length;
                    _hasPendingUpload = false;
                    uploadAgain = _queuedUploadDirty;
                    _queuedUploadDirty = false;
                }

                if (Data.DisposeOnPush && !uploadAgain)
                    Data.Dispose();

                if (uploadAgain)
                    PushDataQueued();
            }

            internal void FailQueuedUpload()
            {
                lock (_queuedUploadSync)
                {
                    _hasPendingUpload = false;
                    _queuedCopyInFlight = false;
                    _queuedUploadDirty = false;
                }
            }

            /// <summary>
            /// Tracks the allocation in stats. Used by the upload queue.
            /// </summary>
            internal void TrackAllocation(long bytes)
            {
                if (_allocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                }
                _allocatedVRAMBytes = bytes;
                RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(_allocatedVRAMBytes);
            }

            /// <summary>
            /// Tracks whether this buffer has a pending upload in the queue.
            /// Set by GLUploadQueue when enqueued/completed.
            /// </summary>
            internal volatile bool _hasPendingUpload;

            /// <summary>
            /// Returns true if this buffer has data uploaded and is ready for rendering.
            /// Returns false if there's a pending upload in the queue.
            /// </summary>
            public bool IsReadyForRendering
                => _lastPushedLength >= Data.Length && !_hasPendingUpload;

            internal void EnsureStorageAllocatedForGpuCopy()
            {
                using var scope = RuntimeEngine.Profiler.Start("OpenGL.GLDataBuffer.EnsureStorageAllocatedForGpuCopy");

                if (HasBlockingActiveMapping())
                    return;

                if (RuntimeEngine.InvokeOnMainThread(EnsureStorageAllocatedForGpuCopy, "GLDataBuffer.EnsureStorageAllocatedForGpuCopy"))
                    return;

                if (!IsGenerated)
                    Generate();

                if (_hasPendingUpload)
                    Renderer.UploadQueue.FlushBuffer(this);

                if (_lastPushedLength < Data.Length)
                {
                    if (Data.Length > AsyncUploadThreshold &&
                        Renderer.UploadQueue.Enabled &&
                        Data.TryGetAddress(out var sourceAddress) &&
                        sourceAddress != VoidPtr.Zero)
                    {
                        PushDataQueued();
                        Renderer.UploadQueue.FlushBuffer(this);
                        return;
                    }

                    PushDataImmediate();
                }
            }

            public static GLEnum ToGLEnum(EBufferUsage usage) => usage switch
            {
                EBufferUsage.StaticDraw => GLEnum.StaticDraw,
                EBufferUsage.DynamicDraw => GLEnum.DynamicDraw,
                EBufferUsage.StreamDraw => GLEnum.StreamDraw,
                EBufferUsage.StaticRead => GLEnum.StaticRead,
                EBufferUsage.DynamicRead => GLEnum.DynamicRead,
                EBufferUsage.StreamRead => GLEnum.StreamRead,
                EBufferUsage.StaticCopy => GLEnum.StaticCopy,
                EBufferUsage.DynamicCopy => GLEnum.DynamicCopy,
                EBufferUsage.StreamCopy => GLEnum.StreamCopy,
                _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, null),
            };

            // ----- PushSubData breakdown (env-gated 1 Hz dump) -----
            // Enable with `XRE_PUSHSUBDATA_BREAKDOWN=1`. When enabled, aggregates per-buffer
            // (call count, bytes requested) and dumps a sorted snapshot to log_rendering.txt
            // approximately once per second. Used to attribute render-thread PushSubData
            // queue floods (see docs/work/design/rendering/render-submission-perf-debug-plan.md §5.4).
            private static readonly bool _pushSubDataBreakdownEnabled =
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRE_PUSHSUBDATA_BREAKDOWN"));

            // ----- PushSubData per-call trace (env-gated, AutoFlush) -----
            // Enable with `XRE_PUSHSUBDATA_TRACE=1`. Writes every PushSubData call and the
            // post-decision branch (PushData / NamedBufferSubData / clamp / immutable-fallback)
            // to Build/Logs/pushsubdata-trace.log with AutoFlush so entries survive a process
            // fail-fast such as the NVIDIA driver `0xc0000409` crash being investigated.
            // Diagnostic only - do not leave enabled in benchmark runs.
            private static readonly bool _pushSubDataTraceEnabled =
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRE_PUSHSUBDATA_TRACE"));
            private static readonly object _pushSubDataTraceLock = new();
            private static System.IO.StreamWriter? _pushSubDataTraceWriter;

            private static void TracePushSubData(string label, int offset, uint length, uint dataLength, uint lastPushed, bool hasPendingUpload, bool immutableStorageSet, bool isGenerated, string stage)
            {
                if (!_pushSubDataTraceEnabled)
                    return;
                try
                {
                    var w = _pushSubDataTraceWriter;
                    if (w is null)
                    {
                        lock (_pushSubDataTraceLock)
                        {
                            if (_pushSubDataTraceWriter is null)
                            {
                                string root = AppContext.BaseDirectory;
                                string logsDir = System.IO.Path.Combine(root, "Build", "Logs");
                                try { System.IO.Directory.CreateDirectory(logsDir); } catch { }
                                string path = System.IO.Path.Combine(logsDir, $"pushsubdata-trace_pid{Environment.ProcessId}.log");
                                var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite, 4096, System.IO.FileOptions.WriteThrough);
                                _pushSubDataTraceWriter = new System.IO.StreamWriter(fs) { AutoFlush = true };
                                _pushSubDataTraceWriter.WriteLine($"# PushSubData trace started at {DateTime.UtcNow:O}");
                            }
                            w = _pushSubDataTraceWriter;
                        }
                    }
                    lock (_pushSubDataTraceLock)
                    {
                        w.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} tid={Environment.CurrentManagedThreadId} stage={stage} label={label} off={offset} len={length} dataLen={dataLength} lastPushed={lastPushed} pendingUpload={hasPendingUpload} immutable={immutableStorageSet} generated={isGenerated}");
                    }
                }
                catch { }
            }
            private static readonly ConcurrentDictionary<string, long[]> _pushSubDataBreakdown = new();
            private static long _pushSubDataLastFlushMs = 0;
            private static int _pushSubDataFlushBusy = 0;

            // long[] layout per label:
            //   [0] calls, [1] totalBytes, [2] minBytes, [3] maxBytes
            // minBytes initialised to long.MaxValue via GetOrAdd factory.
            private const int BD_CALLS = 0;
            private const int BD_BYTES = 1;
            private const int BD_MIN = 2;
            private const int BD_MAX = 3;

            private static void RecordPushSubData(string label, uint length)
            {
                var entry = _pushSubDataBreakdown.GetOrAdd(label, static _ => new long[4] { 0, 0, long.MaxValue, 0 });
                Interlocked.Increment(ref entry[BD_CALLS]);
                Interlocked.Add(ref entry[BD_BYTES], (long)length);

                long len = (long)length;
                long oldMin;
                do
                {
                    oldMin = Interlocked.Read(ref entry[BD_MIN]);
                    if (len >= oldMin) break;
                } while (Interlocked.CompareExchange(ref entry[BD_MIN], len, oldMin) != oldMin);

                long oldMax;
                do
                {
                    oldMax = Interlocked.Read(ref entry[BD_MAX]);
                    if (len <= oldMax) break;
                } while (Interlocked.CompareExchange(ref entry[BD_MAX], len, oldMax) != oldMax);

                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref _pushSubDataLastFlushMs);
                if (now - last < 1000)
                    return;
                if (Interlocked.CompareExchange(ref _pushSubDataFlushBusy, 1, 0) != 0)
                    return;
                try
                {
                    if (Interlocked.Read(ref _pushSubDataLastFlushMs) != last)
                        return;
                    Interlocked.Exchange(ref _pushSubDataLastFlushMs, now);

                    var snap = _pushSubDataBreakdown.ToArray();
                    _pushSubDataBreakdown.Clear();
                    Array.Sort(snap, static (a, b) => b.Value[BD_BYTES].CompareTo(a.Value[BD_BYTES]));
                    var sb = new System.Text.StringBuilder(256 + snap.Length * 96);
                    sb.Append("[PushSubDataBreakdown] 1Hz dump entries=").Append(snap.Length).AppendLine();
                    long totalCalls = 0;
                    long totalBytes = 0;
                    foreach (var kv in snap)
                    {
                        long c = kv.Value[BD_CALLS];
                        long b = kv.Value[BD_BYTES];
                        long mn = kv.Value[BD_MIN] == long.MaxValue ? 0 : kv.Value[BD_MIN];
                        long mx = kv.Value[BD_MAX];
                        long avg = c > 0 ? b / c : 0;
                        totalCalls += c;
                        totalBytes += b;
                        sb.Append("  calls=").Append(c)
                          .Append(" bytes=").Append(b)
                          .Append(" min=").Append(mn)
                          .Append(" max=").Append(mx)
                          .Append(" avg=").Append(avg)
                          .Append(" label=").AppendLine(kv.Key);
                    }
                    sb.Append("  TOTAL calls=").Append(totalCalls).Append(" bytes=").Append(totalBytes);
                    Debug.OpenGL(sb.ToString());
                }
                finally
                {
                    Interlocked.Exchange(ref _pushSubDataFlushBusy, 0);
                }
            }

            private string PushSubDataLabel()
            {
                string attr = Data.AttributeName;
                if (string.IsNullOrEmpty(attr))
                    attr = "<unnamed-attr>";
                string name = Data.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    name = $"id={Data.GetCacheIndex()}";
                return $"{attr}|{name}|target={Data.Target}";
            }

            /// <summary>
            /// Pushes the entire buffer to the GPU. Assumes the buffer has already been allocated using PushData.
            /// </summary>
            public void PushSubData()
                => PushSubData(0, Data.Length);

            /// <summary>
            /// Pushes the a portion of the buffer to the GPU. Assumes the buffer has already been allocated using PushData.
            /// </summary>
            public void PushSubData(int offset, uint length)
            {
                if (HasBlockingActiveMapping())
                    return;

                if (RuntimeEngine.InvokeOnMainThread(() => PushSubData(offset, length), "GLDataBuffer.PushSubData"))
                    return;

                if (_pushSubDataBreakdownEnabled)
                    RecordPushSubData(PushSubDataLabel(), length);

                string traceLabel = _pushSubDataTraceEnabled ? PushSubDataLabel() : string.Empty;
                if (_pushSubDataTraceEnabled)
                    TracePushSubData(traceLabel, offset, length, Data.Length, _lastPushedLength, _hasPendingUpload, _immutableStorageSet, IsGenerated, "enter");

                if (!IsGenerated)
                    Generate();
                else
                {
                    uint lastPushed = _lastPushedLength;
                    uint dataLength = Data.Length;

                    // If the client-side buffer has grown beyond what we've allocated on the GPU,
                    // we need to reallocate the GPU buffer first.
                    if (dataLength > lastPushed)
                    {
                        // Reallocate GPU buffer to match client-side size, then do the sub-data update.
                        if (_pushSubDataTraceEnabled)
                            TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, _immutableStorageSet, true, "grow-PushData");
                        PushData();
                        lastPushed = _lastPushedLength;

                        // Large buffers may route the full upload through GLUploadQueue. In that case
                        // _lastPushedLength still reflects the old GPU allocation until the queued
                        // upload finishes, but the pending full upload already covers this subrange.
                        if (_hasPendingUpload)
                        {
                            if (_pushSubDataTraceEnabled)
                                TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, true, _immutableStorageSet, true, "grow-pending-return");
                            return;
                        }

                        // After a synchronous PushData, all data is already on the GPU, so we can
                        // return if the requested range is within the newly allocated buffer.
                        if (RequestedRangeFits(offset, length, lastPushed))
                        {
                            if (_pushSubDataTraceEnabled)
                                TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, false, _immutableStorageSet, true, "grow-fits-return");
                            return;
                        }
                    }

                    // If resizable buffer was grown and we never (re)allocated on the GPU, fall back to full PushData.
                    // Also do this if caller is pushing the whole buffer starting at 0.
                    if (offset == 0 && (lastPushed == 0 || length > lastPushed))
                    {
                        if (_pushSubDataTraceEnabled)
                            TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, _immutableStorageSet, true, "full-PushData-fallback");
                        PushData();
                        return;
                    }

                    if (!RequestedRangeFits(offset, length, lastPushed))
                    {
                        int clamped = (int)lastPushed - offset;
                        if (clamped <= 0)
                        {
                            if (_pushSubDataTraceEnabled)
                                TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, _immutableStorageSet, true, "offset-past-end-IGNORED");
                            Debug.OpenGLWarning($"PushSubData called with offset {offset} and length {length}, with an offset that exceeds the last fully-pushed length of {lastPushed}. Ignoring call.");
                            return;
                        }

                        if (_pushSubDataTraceEnabled)
                            TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, _immutableStorageSet, true, $"clamp len {length}->{clamped}");
                        length = (uint)clamped;
                    }

                    void* addr = (Data.Address + (uint)offset).Pointer;
                    if (_immutableStorageSet && !Data.StorageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage))
                    {
                        if (_pushSubDataTraceEnabled)
                            TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, true, true, "immutable-no-dynstore-PushData");
                        PushData();
                        return;
                    }

                    if (_pushSubDataTraceEnabled)
                        TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, _immutableStorageSet, true, "NamedBufferSubData");
                    Api.NamedBufferSubData(BindingId, offset, length, addr);
                    if (_pushSubDataTraceEnabled)
                        TracePushSubData(traceLabel, offset, length, dataLength, lastPushed, _hasPendingUpload, _immutableStorageSet, true, "NamedBufferSubData-done");
                }
            }

            private static bool RequestedRangeFits(int offset, uint length, uint bufferLength)
            {
                if (offset < 0)
                    return false;

                ulong end = (ulong)(uint)offset + length;
                return end <= bufferLength;
            }

            public void Flush()
            {
                if (HasBlockingActiveMapping())
                    return;
                if (RuntimeEngine.InvokeOnMainThread(Flush, "GLDataBuffer.Flush"))
                    return;
                Api.FlushMappedNamedBufferRange(BindingId, 0, Data.Length);
            }

            public void FlushRange(int offset, uint length)
            {
                if (HasBlockingActiveMapping())
                    return;
                if (RuntimeEngine.InvokeOnMainThread(() => FlushRange(offset, length), "GLDataBuffer.FlushRange"))
                    return;
                Api.FlushMappedNamedBufferRange(BindingId, offset, length);
            }

            private DataSource? _gpuSideSource = null;
            /// <summary>
            /// The data buffer stored on the GPU side.
            /// </summary>
            public DataSource? GPUSideSource
            {
                get => _gpuSideSource;
                set => SetField(ref _gpuSideSource, value);
            }

            private bool _immutableStorageSet = false;
            public bool ImmutableStorageSet
            {
                get => _immutableStorageSet;
                set => SetField(ref _immutableStorageSet, value);
            }

            public void MapBufferData()
            {
                // If any API wrapper has already mapped this XRDataBuffer, skip remapping
                if (Data.ActivelyMapping.Count > 0)
                {
                    return;
                }

                if (RuntimeEngine.InvokeOnMainThread(MapBufferData, "GLDataBuffer.MapBufferData"))
                    return;

                // Insert a client-mapped buffer barrier before mapping to ensure visibility of GPU writes to persistently mapped buffers
                Renderer.Api.MemoryBarrier((uint)MemoryBarrierMask.ClientMappedBufferBarrierBit);
                MapToClientSide();
            }

            public void MapToClientSide()
            {
                uint id = BindingId;
                uint length = GetLength();
                GPUSideSource?.Dispose();

                var glRange = (uint)ToGLEnum(Data.RangeFlags);
                var glStorage = (uint)ToGLEnum(Data.StorageFlags);
                if (IsGpuBufferLoggingEnabled())
                    Debug.OpenGL($"[GLBuffer/Map] {GetDescribingName()} name={BufferNameOrTarget()} id={id} len={length} target={Data.Target} storage={StorageFlagsString()} (0x{glStorage:X}) range={RangeFlagsString()} (0x{glRange:X})");

                var addr = Api.MapNamedBufferRange(id, IntPtr.Zero, length, glRange);
                if (addr is null)
                {
                    Debug.OpenGLWarning($"[GLBuffer/Map] {GetDescribingName()} name={BufferNameOrTarget()} returned null pointer.");
                    return;
                }
                GPUSideSource = new DataSource(addr, length);
                Data.ActivelyMapping.Add(this);
            }

            // ----- Added helpers for mapping/immutable storage -----
            private uint GetLength()
            {
                var existingSource = Data.ClientSideSource;
                return existingSource is not null ? existingSource.Length : Data.Length;
            }

            private void AllocateImmutable()
            {
                // Track VRAM deallocation of previous buffer if any
                if (_allocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                uint id = BindingId;
                uint length;
                var existingSource = Data.ClientSideSource;
                var glStorage = (uint)ToGLEnum(Data.StorageFlags);
                if (IsGpuBufferLoggingEnabled())
                    Debug.OpenGL($"[GLBuffer/Storage] {GetDescribingName()} name={BufferNameOrTarget()} id={id} len={(existingSource?.Length ?? Data.Length)} storage={StorageFlagsString()} (0x{glStorage:X})");
                if (existingSource is not null)
                    Api.NamedBufferStorage(id, length = existingSource.Length, existingSource.Address.Pointer, glStorage);
                else
                    Api.NamedBufferStorage(id, length = Data.Length, null, glStorage);

                _immutableStorageSet = true;

                // Track VRAM allocation
                _allocatedVRAMBytes = length;
                RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(_allocatedVRAMBytes);
            }
            // ------------------------------------------------------

            public void UnmapBufferData()
            {
                if (!Data.ActivelyMapping.Contains(this))
                    return;

                if (RuntimeEngine.InvokeOnMainThread(UnmapBufferData, "GLDataBuffer.UnmapBufferData"))
                    return;

                if (IsGpuBufferLoggingEnabled())
                    Debug.OpenGL($"[GLBuffer/Unmap] {GetDescribingName()} name={BufferNameOrTarget()} id={BindingId}");
                Api.UnmapNamedBuffer(BindingId);
                Data.ActivelyMapping.Remove(this);

                GPUSideSource?.Dispose();
                GPUSideSource = null;
            }

            public static GLEnum ToGLEnum(EBufferMapStorageFlags storageFlags)
            {
                GLEnum flags = 0;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Read))
                    flags |= GLEnum.MapReadBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Write))
                    flags |= GLEnum.MapWriteBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Persistent))
                    flags |= GLEnum.MapPersistentBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Coherent))
                    flags |= GLEnum.MapCoherentBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.ClientStorage))
                    flags |= GLEnum.ClientStorageBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage))
                    flags |= GLEnum.DynamicStorageBit;
                return flags;
            }

            public static GLEnum ToGLEnum(EBufferMapRangeFlags rangeFlags)
            {
                GLEnum flags = 0;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Read))
                    flags |= GLEnum.MapReadBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Write))
                    flags |= GLEnum.MapWriteBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Persistent))
                    flags |= GLEnum.MapPersistentBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Coherent))
                    flags |= GLEnum.MapCoherentBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateRange))
                    flags |= GLEnum.MapInvalidateRangeBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateBuffer))
                    flags |= GLEnum.MapInvalidateBufferBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.FlushExplicit))
                    flags |= GLEnum.MapFlushExplicitBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Unsynchronized))
                    flags |= GLEnum.MapUnsynchronizedBit;
                return flags;
            }

            public void SetUniformBlockName(XRRenderProgram program, string blockName)
            {
                var apiProgram = Renderer.GenericToAPI<GLRenderProgram>(program);
                if (apiProgram is null)
                    return;

                var bindingID = apiProgram.BindingId;
                if (bindingID == InvalidBindingId)
                    return;

                if (RuntimeEngine.InvokeOnMainThread(() => SetUniformBlockName(program, blockName), "GLDataBuffer.SetUniformBlockName"))
                    return;

                Bind();
                SetBlockIndex(Api.GetUniformBlockIndex(bindingID, blockName));
                Unbind();
            }

            public void SetBlockIndex(uint blockIndex)
            {
                if (blockIndex == uint.MaxValue)
                    return;

                if (RuntimeEngine.InvokeOnMainThread(() => SetBlockIndex(blockIndex), "GLDataBuffer.SetBlockIndex"))
                    return;

                Bind();
                // When using vertex buffers as SSBOs (compute skinning), bind with SSBO target even if the buffer was created as an array buffer.
                GLEnum bindTarget = ToGLEnum(Data.Target);
                if (bindTarget == GLEnum.ArrayBuffer)
                    bindTarget = GLEnum.ShaderStorageBuffer;

                Api.BindBufferBase(bindTarget, blockIndex, BindingId);
                Unbind();
            }

            /// <summary>
            /// Deletes the current GL buffer and creates a fresh mutable one,
            /// allowing subsequent glNamedBufferData calls to succeed.
            /// </summary>
            internal void RecreateBuffer()
            {
                uint oldId = _bindingId!.Value;
                Api.DeleteBuffer(oldId);
                Api.CreateBuffers(1, out uint newId);
                _bindingId = newId;
                _immutableStorageSet = false;
            }

            internal void RecreateBufferAsMutable()
                => RecreateBuffer();

            protected internal override void PreDeleted()
            {
                UnmapBufferData();
                _immutableStorageSet = false;
                _lastPushedLength = 0;
                FailQueuedUpload();

                // Track VRAM deallocation
                if (_allocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }
            }

            public void Bind()
            {
                if (RuntimeEngine.InvokeOnMainThread(Bind, "GLDataBuffer.Bind"))
                    return;

                Api.BindBuffer(ToGLEnum(Data.Target), BindingId);
            }
            public void Unbind()
            {
                if (RuntimeEngine.InvokeOnMainThread(Unbind, "GLDataBuffer.Unbind"))
                    return;

                Api.BindBuffer(ToGLEnum(Data.Target), 0);
            }

            public bool IsMapped => Data.ActivelyMapping.Contains(this);

            public VoidPtr? GetMappedAddress()
                => GPUSideSource?.Address;

            public void BindSSBO(XRRenderProgram program, uint? bindindIndexOverride = null)
            {
                BindSSBO(Renderer.GenericToAPI<GLRenderProgram>(program), bindindIndexOverride);
            }

            public void BindSSBO(GLRenderProgram? program, uint? bindindIndexOverride = null)
            {
                if (program is null)
                {
                    Debug.OpenGLWarning($"Failed to bind SSBO {GetDescribingName()} to program {program?.GetDescribingName() ?? "null"}.");
                    return;
                }

                // SSBOs may be GPU-written immediately after binding. If XRDataBuffer.Resize()
                // grew the client-side size, allocate the GL storage before compute dispatch so
                // shader writes do not target the previous, smaller allocation.
                if (!IsReadyForRendering)
                    EnsureStorageAllocatedForGpuCopy();

                uint resourceIndex;
                if (bindindIndexOverride.HasValue)
                {
                    resourceIndex = bindindIndexOverride.Value;
                }
                else if (Data.BindingIndexOverride.HasValue)
                {
                    resourceIndex = Data.BindingIndexOverride.Value;
                }
                else
                {
                    // Cache the resource index lookup per program to avoid expensive GL query every frame
                    uint programId = program.BindingId;
                    if (!_ssboResourceIndexCache.TryGetValue(programId, out resourceIndex))
                    {
                        resourceIndex = Api.GetProgramResourceIndex(programId, GLEnum.ShaderStorageBlock, Data.AttributeName);
                        _ssboResourceIndexCache[programId] = resourceIndex;
                    }
                }

                if (resourceIndex == uint.MaxValue)
                    return;

                // BindBufferBase binds directly by ID - no need for Bind()/Unbind() calls
                Api.BindBufferBase(ToGLEnum(EBufferTarget.ShaderStorageBuffer), resourceIndex, BindingId);
            }
        }
    }
}
