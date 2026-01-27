using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering
{
    /// <summary>
    /// Comprehensive logging system for GPU render dispatch debugging.
    /// Provides structured, categorized logging with performance metrics and state tracking.
    /// </summary>
    public static class GpuDispatchLogger
    {
        #region Configuration

        /// <summary>
        /// Log categories for GPU dispatch debugging.
        /// </summary>
        [Flags]
        public enum LogCategory
        {
            None = 0,
            /// <summary>Lifecycle events (init, dispose, render begin/end)</summary>
            Lifecycle = 1 << 0,
            /// <summary>Buffer operations (create, bind, map, unmap)</summary>
            Buffers = 1 << 1,
            /// <summary>Culling operations (frustum, BVH, occlusion)</summary>
            Culling = 1 << 2,
            /// <summary>Sorting operations (material sort, distance sort)</summary>
            Sorting = 1 << 3,
            /// <summary>Indirect command building</summary>
            Indirect = 1 << 4,
            /// <summary>Material batching and resolution</summary>
            Materials = 1 << 5,
            /// <summary>Statistics and metrics</summary>
            Stats = 1 << 6,
            /// <summary>Draw dispatch calls</summary>
            Draw = 1 << 7,
            /// <summary>VAO/attribute configuration</summary>
            VAO = 1 << 8,
            /// <summary>Shader program binding</summary>
            Shaders = 1 << 9,
            /// <summary>Uniform setting</summary>
            Uniforms = 1 << 10,
            /// <summary>Memory barriers and synchronization</summary>
            Sync = 1 << 11,
            /// <summary>Errors and warnings</summary>
            Errors = 1 << 12,
            /// <summary>Performance timing</summary>
            Timing = 1 << 13,
            /// <summary>Validation checks</summary>
            Validation = 1 << 14,
            /// <summary>State transitions</summary>
            State = 1 << 15,
            /// <summary>All categories enabled</summary>
            All = ~0
        }

        /// <summary>
        /// Log verbosity levels.
        /// </summary>
        public enum LogLevel
        {
            /// <summary>Only critical errors</summary>
            Error = 0,
            /// <summary>Warnings and errors</summary>
            Warning = 1,
            /// <summary>Informational messages</summary>
            Info = 2,
            /// <summary>Detailed debug information</summary>
            Debug = 3,
            /// <summary>Extremely verbose trace logging</summary>
            Trace = 4
        }

        private static LogCategory _enabledCategories = LogCategory.All;
        private static LogLevel _logLevel = LogLevel.Info;
        private static bool _includeTimestamps = true;
        private static bool _includeFrameNumbers = true;
        private static bool _includeThreadId = false;
        private static int _maxBufferDumpSize = 8;
        private static volatile bool _isPaused = false;

        /// <summary>
        /// Categories to log. Default is All.
        /// </summary>
        public static LogCategory EnabledCategories
        {
            get => _enabledCategories;
            set => _enabledCategories = value;
        }

        /// <summary>
        /// Current log level. Messages above this level are suppressed.
        /// </summary>
        public static LogLevel CurrentLogLevel
        {
            get => _logLevel;
            set => _logLevel = value;
        }

        /// <summary>
        /// Whether to include timestamps in log output.
        /// </summary>
        public static bool IncludeTimestamps
        {
            get => _includeTimestamps;
            set => _includeTimestamps = value;
        }

        /// <summary>
        /// Whether to include frame numbers in log output.
        /// </summary>
        public static bool IncludeFrameNumbers
        {
            get => _includeFrameNumbers;
            set => _includeFrameNumbers = value;
        }

        /// <summary>
        /// Whether to include thread IDs in log output.
        /// </summary>
        public static bool IncludeThreadId
        {
            get => _includeThreadId;
            set => _includeThreadId = value;
        }

        /// <summary>
        /// Maximum number of buffer elements to dump in diagnostics.
        /// </summary>
        public static int MaxBufferDumpSize
        {
            get => _maxBufferDumpSize;
            set => _maxBufferDumpSize = Math.Max(1, Math.Min(value, 64));
        }

        /// <summary>
        /// Temporarily pause all logging.
        /// </summary>
        public static bool IsPaused
        {
            get => _isPaused;
            set => _isPaused = value;
        }

        #endregion

        #region State Tracking

        private static long _frameNumber = 0;
        private static long _dispatchCount = 0;
        private static long _drawCallCount = 0;
        private static long _commandsIssued = 0;
        private static readonly Stopwatch _frameTimer = Stopwatch.StartNew();
        private static readonly ConcurrentDictionary<string, long> _categoryMessageCounts = new();
        private static readonly ConcurrentDictionary<string, double> _timingAccumulators = new();

        /// <summary>
        /// Current frame number for logging context.
        /// </summary>
        public static long FrameNumber => _frameNumber;

        /// <summary>
        /// Total dispatch calls this session.
        /// </summary>
        public static long DispatchCount => _dispatchCount;

        /// <summary>
        /// Total draw calls this session.
        /// </summary>
        public static long DrawCallCount => _drawCallCount;

        /// <summary>
        /// Call at the start of each frame to update frame context.
        /// </summary>
        public static void BeginFrame()
        {
            Interlocked.Increment(ref _frameNumber);
            _frameTimer.Restart();
        }

        /// <summary>
        /// Call at the end of each frame to finalize metrics.
        /// </summary>
        public static void EndFrame()
        {
            // Could emit frame summary here if needed
        }

        /// <summary>
        /// Reset all statistics counters.
        /// </summary>
        public static void ResetStats()
        {
            Interlocked.Exchange(ref _frameNumber, 0);
            Interlocked.Exchange(ref _dispatchCount, 0);
            Interlocked.Exchange(ref _drawCallCount, 0);
            Interlocked.Exchange(ref _commandsIssued, 0);
            _categoryMessageCounts.Clear();
            _timingAccumulators.Clear();
        }

        #endregion

        #region Core Logging Methods

        /// <summary>
        /// Check if logging is enabled for the given category and level.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEnabled(LogCategory category, LogLevel level = LogLevel.Info)
        {
            if (_isPaused)
                return false;
            if (!Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                return false;
            if (level > _logLevel)
                return false;
            if ((category & _enabledCategories) == 0)
                return false;
            return true;
        }

        /// <summary>
        /// Log a message with the specified category and level.
        /// </summary>
        public static void Log(LogCategory category, LogLevel level, string message,
            [CallerMemberName] string? caller = null,
            [CallerFilePath] string? file = null,
            [CallerLineNumber] int line = 0)
        {
            if (!IsEnabled(category, level))
                return;

            var sb = new StringBuilder(256);
            FormatPrefix(sb, category, level, caller);
            sb.Append(message);

            EmitLog(level, sb.ToString());
            TrackMessage(category.ToString());
        }

        /// <summary>
        /// Log a formatted message with the specified category and level.
        /// </summary>
        public static void Log(LogCategory category, LogLevel level, string format, params object?[] args)
        {
            if (!IsEnabled(category, level))
                return;

            var sb = new StringBuilder(256);
            FormatPrefix(sb, category, level, null);
            sb.AppendFormat(format, args);

            EmitLog(level, sb.ToString());
            TrackMessage(category.ToString());
        }

        /// <summary>
        /// Log an error message.
        /// </summary>
        public static void Error(LogCategory category, string message,
            [CallerMemberName] string? caller = null)
        {
            Log(category, LogLevel.Error, message, caller);
        }

        /// <summary>
        /// Log an error with format string.
        /// </summary>
        public static void Error(LogCategory category, string format, params object?[] args)
        {
            Log(category, LogLevel.Error, format, args);
        }

        /// <summary>
        /// Log an error with exception details.
        /// </summary>
        public static void Error(LogCategory category, string message, Exception ex,
            [CallerMemberName] string? caller = null)
        {
            if (!IsEnabled(category, LogLevel.Error))
                return;

            var sb = new StringBuilder(512);
            FormatPrefix(sb, category, LogLevel.Error, caller);
            sb.Append(message);
            sb.Append(" Exception: ");
            sb.Append(ex.GetType().Name);
            sb.Append(": ");
            sb.Append(ex.Message);

            EmitLog(LogLevel.Error, sb.ToString());
            TrackMessage(category.ToString());
        }

        /// <summary>
        /// Log a warning message.
        /// </summary>
        public static void Warn(LogCategory category, string message,
            [CallerMemberName] string? caller = null)
        {
            Log(category, LogLevel.Warning, message, caller);
        }

        /// <summary>
        /// Log a warning with format string.
        /// </summary>
        public static void Warn(LogCategory category, string format, params object?[] args)
        {
            Log(category, LogLevel.Warning, format, args);
        }

        /// <summary>
        /// Log an informational message.
        /// </summary>
        public static void Info(LogCategory category, string message,
            [CallerMemberName] string? caller = null)
        {
            Log(category, LogLevel.Info, message, caller);
        }

        /// <summary>
        /// Log an informational message with format string.
        /// </summary>
        public static void Info(LogCategory category, string format, params object?[] args)
        {
            Log(category, LogLevel.Info, format, args);
        }

        /// <summary>
        /// Log a debug message.
        /// </summary>
        public static void Debug(LogCategory category, string message,
            [CallerMemberName] string? caller = null)
        {
            Log(category, LogLevel.Debug, message, caller);
        }

        /// <summary>
        /// Log a debug message with format string.
        /// </summary>
        public static void Debug(LogCategory category, string format, params object?[] args)
        {
            Log(category, LogLevel.Debug, format, args);
        }

        /// <summary>
        /// Log a trace message (very verbose).
        /// </summary>
        public static void Trace(LogCategory category, string message,
            [CallerMemberName] string? caller = null)
        {
            Log(category, LogLevel.Trace, message, caller);
        }

        private static void FormatPrefix(StringBuilder sb, LogCategory category, LogLevel level, string? caller)
        {
            sb.Append('[');
            sb.Append(level switch
            {
                LogLevel.Error => "ERR",
                LogLevel.Warning => "WRN",
                LogLevel.Info => "INF",
                LogLevel.Debug => "DBG",
                LogLevel.Trace => "TRC",
                _ => "???"
            });
            sb.Append(']');

            if (_includeTimestamps)
            {
                sb.Append('[');
                sb.Append(_frameTimer.ElapsedMilliseconds.ToString("D4"));
                sb.Append("ms]");
            }

            if (_includeFrameNumbers)
            {
                sb.Append("[F");
                sb.Append(_frameNumber);
                sb.Append(']');
            }

            if (_includeThreadId)
            {
                sb.Append("[T");
                sb.Append(Environment.CurrentManagedThreadId);
                sb.Append(']');
            }

            sb.Append("[GPU/");
            sb.Append(category);
            sb.Append("] ");

            if (!string.IsNullOrEmpty(caller))
            {
                sb.Append(caller);
                sb.Append(": ");
            }
        }

        private static void EmitLog(LogLevel level, string message)
        {
            if (level == LogLevel.Error)
                XREngine.Debug.LogError(message);
            else if (level == LogLevel.Warning)
                XREngine.Debug.LogWarning(message);
            else
                XREngine.Debug.Out(message);
        }

        private static void TrackMessage(string category)
        {
            _categoryMessageCounts.AddOrUpdate(category, 1, (_, count) => count + 1);
        }

        #endregion

        #region Dispatch Logging

        /// <summary>
        /// Log the start of a render dispatch operation.
        /// </summary>
        public static void LogDispatchStart(string dispatchType, uint drawCount, uint maxCommands)
        {
            Interlocked.Increment(ref _dispatchCount);

            if (!IsEnabled(LogCategory.Draw, LogLevel.Info))
                return;

            Log(LogCategory.Draw, LogLevel.Info,
                "=== {0} START === drawCount={1}, maxCommands={2}",
                dispatchType, drawCount, maxCommands);
        }

        /// <summary>
        /// Log the end of a render dispatch operation.
        /// </summary>
        public static void LogDispatchEnd(string dispatchType, bool success, double elapsedMs = 0)
        {
            if (!IsEnabled(LogCategory.Draw, LogLevel.Info))
                return;

            if (elapsedMs > 0)
            {
                Log(LogCategory.Draw, LogLevel.Info,
                    "=== {0} END === success={1}, elapsed={2:F2}ms",
                    dispatchType, success, elapsedMs);
            }
            else
            {
                Log(LogCategory.Draw, LogLevel.Info,
                    "=== {0} END === success={1}",
                    dispatchType, success);
            }
        }

        /// <summary>
        /// Log a multi-draw indirect call.
        /// </summary>
        public static void LogMultiDrawIndirect(bool useCount, uint drawCountOrMax, uint stride, nuint byteOffset = 0)
        {
            Interlocked.Increment(ref _drawCallCount);
            Interlocked.Add(ref _commandsIssued, drawCountOrMax);

            if (!IsEnabled(LogCategory.Draw, LogLevel.Debug))
                return;

            string pathName = useCount ? "MultiDrawElementsIndirectCount" : "MultiDrawElementsIndirect";
            Log(LogCategory.Draw, LogLevel.Debug,
                "{0}: count/max={1}, stride={2}, byteOffset={3}",
                pathName, drawCountOrMax, stride, byteOffset);
        }

        /// <summary>
        /// Log batch dispatch information.
        /// </summary>
        public static void LogBatchDispatch(uint batchIndex, uint offset, uint count, uint materialId)
        {
            if (!IsEnabled(LogCategory.Materials, LogLevel.Debug))
                return;

            Log(LogCategory.Materials, LogLevel.Debug,
                "Batch[{0}]: offset={1}, count={2}, materialId={3}",
                batchIndex, offset, count, materialId);
        }

        #endregion

        #region Buffer Logging

        /// <summary>
        /// Log buffer binding operation.
        /// </summary>
        public static void LogBufferBind(string bufferName, string target, uint? bindingId = null)
        {
            if (!IsEnabled(LogCategory.Buffers, LogLevel.Debug))
                return;

            if (bindingId.HasValue)
                Log(LogCategory.Buffers, LogLevel.Debug, "Bind {0} -> {1} (id={2})", bufferName, target, bindingId.Value);
            else
                Log(LogCategory.Buffers, LogLevel.Debug, "Bind {0} -> {1}", bufferName, target);
        }

        /// <summary>
        /// Log buffer unbinding operation.
        /// </summary>
        public static void LogBufferUnbind(string bufferName, string target)
        {
            if (!IsEnabled(LogCategory.Buffers, LogLevel.Trace))
                return;

            Log(LogCategory.Buffers, LogLevel.Trace, "Unbind {0} <- {1}", bufferName, target);
        }

        /// <summary>
        /// Log buffer creation.
        /// </summary>
        public static void LogBufferCreate(string bufferName, uint capacity, uint elementSize, string usage)
        {
            if (!IsEnabled(LogCategory.Buffers, LogLevel.Info))
                return;

            Log(LogCategory.Buffers, LogLevel.Info,
                "Create {0}: capacity={1}, elementSize={2}, usage={3}, totalBytes={4}",
                bufferName, capacity, elementSize, usage, capacity * elementSize);
        }

        /// <summary>
        /// Log buffer resize operation.
        /// </summary>
        public static void LogBufferResize(string bufferName, uint oldCapacity, uint newCapacity)
        {
            if (!IsEnabled(LogCategory.Buffers, LogLevel.Info))
                return;

            Log(LogCategory.Buffers, LogLevel.Info,
                "Resize {0}: {1} -> {2} elements",
                bufferName, oldCapacity, newCapacity);
        }

        /// <summary>
        /// Log buffer map/unmap operations.
        /// </summary>
        public static void LogBufferMap(string bufferName, bool isMap, bool success)
        {
            if (!IsEnabled(LogCategory.Buffers, LogLevel.Trace))
                return;

            string op = isMap ? "Map" : "Unmap";
            Log(LogCategory.Buffers, LogLevel.Trace,
                "{0} {1}: {2}",
                op, bufferName, success ? "OK" : "FAILED");
        }

        /// <summary>
        /// Dump the contents of an indirect draw buffer.
        /// </summary>
        public static unsafe void DumpIndirectDrawBuffer(XRDataBuffer? buffer, uint sampleCount, string label = "IndirectDrawBuffer")
        {
            if (!IsEnabled(LogCategory.Buffers, LogLevel.Debug))
                return;

            if (buffer is null)
            {
                Log(LogCategory.Buffers, LogLevel.Debug, "DumpIndirectDrawBuffer({0}): buffer is null", label);
                return;
            }

            sampleCount = Math.Min(sampleCount, (uint)_maxBufferDumpSize);
            sampleCount = Math.Min(sampleCount, buffer.ElementCount);

            bool mappedHere = false;
            try
            {
                if (buffer.ActivelyMapping.Count == 0)
                {
                    buffer.MapBufferData();
                    mappedHere = true;
                }

                var ptr = buffer.GetMappedAddresses().FirstOrDefault(p => p.IsValid);
                if (!ptr.IsValid)
                {
                    Log(LogCategory.Buffers, LogLevel.Warning, "DumpIndirectDrawBuffer({0}): failed to get mapped pointer", label);
                    return;
                }

                var sb = new StringBuilder(512);
                sb.AppendFormat("DumpIndirectDrawBuffer({0}): sampleCount={1}", label, sampleCount);

                uint stride = buffer.ElementSize;
                if (stride == 0)
                    stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();

                byte* basePtr = (byte*)ptr.Pointer;
                for (uint i = 0; i < sampleCount; i++)
                {
                    var cmd = Unsafe.ReadUnaligned<DrawElementsIndirectCommand>(basePtr + i * stride);
                    sb.AppendFormat("\n  [{0}] count={1}, instances={2}, firstIndex={3}, baseVertex={4}, baseInstance={5}",
                        i, cmd.Count, cmd.InstanceCount, cmd.FirstIndex, cmd.BaseVertex, cmd.BaseInstance);
                }

                Log(LogCategory.Buffers, LogLevel.Debug, sb.ToString());
            }
            catch (Exception ex)
            {
                Log(LogCategory.Buffers, LogLevel.Error, "DumpIndirectDrawBuffer({0}) failed: {1}", label, ex.Message);
            }
            finally
            {
                if (mappedHere)
                    buffer.UnmapBufferData();
            }
        }

        /// <summary>
        /// Dump the contents of a culled command buffer.
        /// </summary>
        public static unsafe void DumpCulledCommandBuffer(XRDataBuffer? buffer, uint sampleCount, string label = "CulledCommands")
        {
            if (!IsEnabled(LogCategory.Culling, LogLevel.Debug))
                return;

            if (buffer is null)
            {
                Log(LogCategory.Culling, LogLevel.Debug, "DumpCulledCommandBuffer({0}): buffer is null", label);
                return;
            }

            sampleCount = Math.Min(sampleCount, (uint)_maxBufferDumpSize);
            sampleCount = Math.Min(sampleCount, buffer.ElementCount);

            bool mappedHere = false;
            try
            {
                if (buffer.ActivelyMapping.Count == 0)
                {
                    buffer.MapBufferData();
                    mappedHere = true;
                }

                var ptr = buffer.GetMappedAddresses().FirstOrDefault(p => p.IsValid);
                if (!ptr.IsValid)
                {
                    Log(LogCategory.Culling, LogLevel.Warning, "DumpCulledCommandBuffer({0}): failed to get mapped pointer", label);
                    return;
                }

                var sb = new StringBuilder(512);
                sb.AppendFormat("DumpCulledCommandBuffer({0}): sampleCount={1}", label, sampleCount);

                uint stride = buffer.ElementSize;
                if (stride == 0)
                    stride = (uint)Marshal.SizeOf<GPUIndirectRenderCommand>();

                byte* basePtr = (byte*)ptr.Pointer;
                for (uint i = 0; i < sampleCount; i++)
                {
                    var cmd = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(basePtr + i * stride);
                    sb.AppendFormat("\n  [{0}] mesh={1}, submesh={2}, mat={3}, instances={4}, pass={5}, flags={6}",
                        i, cmd.MeshID, cmd.SubmeshID, cmd.MaterialID, cmd.InstanceCount, cmd.RenderPass, cmd.Flags);
                }

                Log(LogCategory.Culling, LogLevel.Debug, sb.ToString());
            }
            catch (Exception ex)
            {
                Log(LogCategory.Culling, LogLevel.Error, "DumpCulledCommandBuffer({0}) failed: {1}", label, ex.Message);
            }
            finally
            {
                if (mappedHere)
                    buffer.UnmapBufferData();
            }
        }

        #endregion

        #region VAO/Shader Logging

        /// <summary>
        /// Log VAO binding operation.
        /// </summary>
        public static void LogVAOBind(string? vaoName, bool hasVersion)
        {
            if (!IsEnabled(LogCategory.VAO, LogLevel.Debug))
                return;

            Log(LogCategory.VAO, LogLevel.Debug,
                "Bind VAO: name={0}, hasVersion={1}",
                vaoName ?? "<null>", hasVersion);
        }

        /// <summary>
        /// Log VAO validation result.
        /// </summary>
        public static void LogVAOValidation(bool hasIndexBuffer, IndexSize? indexSize = null, uint? indexCount = null)
        {
            if (!IsEnabled(LogCategory.VAO, LogLevel.Debug))
                return;

            if (hasIndexBuffer && indexSize.HasValue && indexCount.HasValue)
            {
                Log(LogCategory.VAO, LogLevel.Debug,
                    "VAO validation: hasIndexBuffer={0}, indexSize={1}, indexCount={2}",
                    hasIndexBuffer, indexSize.Value, indexCount.Value);
            }
            else
            {
                Log(LogCategory.VAO, LogLevel.Debug,
                    "VAO validation: hasIndexBuffer={0}",
                    hasIndexBuffer);
            }
        }

        /// <summary>
        /// Log shader program binding.
        /// </summary>
        public static void LogShaderBind(string? programName, uint? programId = null)
        {
            if (!IsEnabled(LogCategory.Shaders, LogLevel.Debug))
                return;

            if (programId.HasValue)
                Log(LogCategory.Shaders, LogLevel.Debug, "Bind shader: {0} (id={1})", programName ?? "<null>", programId.Value);
            else
                Log(LogCategory.Shaders, LogLevel.Debug, "Bind shader: {0}", programName ?? "<null>");
        }

        /// <summary>
        /// Log uniform setting.
        /// </summary>
        public static void LogUniform(string uniformName, string valueDescription)
        {
            if (!IsEnabled(LogCategory.Uniforms, LogLevel.Trace))
                return;

            Log(LogCategory.Uniforms, LogLevel.Trace,
                "Set uniform {0} = {1}",
                uniformName, valueDescription);
        }

        /// <summary>
        /// Log uniform matrix.
        /// </summary>
        public static void LogUniformMatrix(string uniformName, Matrix4x4 matrix)
        {
            if (!IsEnabled(LogCategory.Uniforms, LogLevel.Trace))
                return;

            bool isIdentity = matrix.Equals(Matrix4x4.Identity);
            if (isIdentity)
            {
                Log(LogCategory.Uniforms, LogLevel.Trace, "Set uniform {0} = Identity", uniformName);
            }
            else
            {
                Log(LogCategory.Uniforms, LogLevel.Trace,
                    "Set uniform {0} = Translation({1:F2},{2:F2},{3:F2})",
                    uniformName, matrix.M41, matrix.M42, matrix.M43);
            }
        }

        #endregion

        #region Culling Logging

        /// <summary>
        /// Log culling operation start.
        /// </summary>
        public static void LogCullingStart(string cullType, uint inputCount)
        {
            if (!IsEnabled(LogCategory.Culling, LogLevel.Info))
                return;

            Log(LogCategory.Culling, LogLevel.Info,
                "{0} START: inputCount={1}",
                cullType, inputCount);
        }

        /// <summary>
        /// Log culling operation result.
        /// </summary>
        public static void LogCullingResult(string cullType, uint inputCount, uint visibleCount, uint instanceCount)
        {
            if (!IsEnabled(LogCategory.Culling, LogLevel.Info))
                return;

            float cullRate = inputCount > 0 ? (1.0f - (float)visibleCount / inputCount) * 100.0f : 0.0f;
            Log(LogCategory.Culling, LogLevel.Info,
                "{0} COMPLETE: input={1}, visible={2}, instances={3}, cullRate={4:F1}%",
                cullType, inputCount, visibleCount, instanceCount, cullRate);
        }

        /// <summary>
        /// Log frustum planes for debugging.
        /// </summary>
        public static void LogFrustumPlanes(Span<Vector4> planes)
        {
            if (!IsEnabled(LogCategory.Culling, LogLevel.Trace))
                return;

            var sb = new StringBuilder(256);
            sb.Append("Frustum planes:");
            for (int i = 0; i < planes.Length && i < 6; i++)
            {
                var p = planes[i];
                sb.AppendFormat("\n  [{0}] ({1:F3}, {2:F3}, {3:F3}, {4:F3})",
                    i, p.X, p.Y, p.Z, p.W);
            }

            Log(LogCategory.Culling, LogLevel.Trace, sb.ToString());
        }

        #endregion

        #region Stats Logging

        /// <summary>
        /// Log GPU stats from stats buffer.
        /// </summary>
        public static void LogGpuStats(uint inputCount, uint culledCount, uint drawCount,
            uint rejectedFrustum, uint rejectedDistance)
        {
            if (!IsEnabled(LogCategory.Stats, LogLevel.Info))
                return;

            Log(LogCategory.Stats, LogLevel.Info,
                "GPU Stats: input={0}, culled={1}, draws={2}, rejFrustum={3}, rejDistance={4}",
                inputCount, culledCount, drawCount, rejectedFrustum, rejectedDistance);
        }

        /// <summary>
        /// Log overflow flags.
        /// </summary>
        public static void LogOverflowFlags(uint cullingOverflow, uint indirectOverflow, uint truncation)
        {
            if (!IsEnabled(LogCategory.Stats, LogLevel.Warning))
                return;

            if (cullingOverflow > 0 || indirectOverflow > 0 || truncation > 0)
            {
                Log(LogCategory.Stats, LogLevel.Warning,
                    "OVERFLOW FLAGS: culling={0}, indirect={1}, truncation={2}",
                    cullingOverflow, indirectOverflow, truncation);
            }
        }

        /// <summary>
        /// Log session statistics summary.
        /// </summary>
        public static void LogSessionSummary()
        {
            if (!IsEnabled(LogCategory.Stats, LogLevel.Info))
                return;

            Log(LogCategory.Stats, LogLevel.Info,
                "Session Summary: frames={0}, dispatches={1}, drawCalls={2}, commandsIssued={3}",
                _frameNumber, _dispatchCount, _drawCallCount, _commandsIssued);

            var sb = new StringBuilder(256);
            sb.Append("Message counts by category:");
            foreach (var kvp in _categoryMessageCounts)
            {
                sb.AppendFormat("\n  {0}: {1}", kvp.Key, kvp.Value);
            }
            Log(LogCategory.Stats, LogLevel.Info, sb.ToString());
        }

        #endregion

        #region Validation Logging

        /// <summary>
        /// Log buffer validation result.
        /// </summary>
        public static void LogBufferValidation(string bufferName, bool valid, string? reason = null)
        {
            if (!IsEnabled(LogCategory.Validation, valid ? LogLevel.Trace : LogLevel.Warning))
                return;

            if (valid)
            {
                Log(LogCategory.Validation, LogLevel.Trace, "Buffer {0}: VALID", bufferName);
            }
            else
            {
                Log(LogCategory.Validation, LogLevel.Warning,
                    "Buffer {0}: INVALID - {1}",
                    bufferName, reason ?? "unknown reason");
            }
        }

        /// <summary>
        /// Log indirect buffer state validation.
        /// </summary>
        public static void LogIndirectBufferValidation(XRDataBuffer? buffer, uint expectedCommands, uint stride)
        {
            if (!IsEnabled(LogCategory.Validation, LogLevel.Debug))
                return;

            if (buffer is null)
            {
                Log(LogCategory.Validation, LogLevel.Warning, "IndirectBuffer validation: buffer is null");
                return;
            }

            uint actualCapacity = buffer.ElementCount;
            ulong requiredBytes = (ulong)expectedCommands * stride;
            ulong actualBytes = (ulong)actualCapacity * buffer.ElementSize;
            bool valid = actualBytes >= requiredBytes;

            Log(LogCategory.Validation, valid ? LogLevel.Debug : LogLevel.Warning,
                "IndirectBuffer validation: expected={0} commands, capacity={1}, stride={2}, required={3} bytes, actual={4} bytes, {5}",
                expectedCommands, actualCapacity, stride, requiredBytes, actualBytes, valid ? "OK" : "INSUFFICIENT");
        }

        #endregion

        #region Timing Helpers

        /// <summary>
        /// Start a timing section and return a disposable scope.
        /// </summary>
        public static TimingScope BeginTiming(string name)
        {
            return new TimingScope(name);
        }

        /// <summary>
        /// Disposable scope for timing operations.
        /// </summary>
        public readonly struct TimingScope : IDisposable
        {
            private readonly string _name;
            private readonly long _startTicks;

            public TimingScope(string name)
            {
                _name = name;
                _startTicks = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (!IsEnabled(LogCategory.Timing, LogLevel.Debug))
                    return;

                long endTicks = Stopwatch.GetTimestamp();
                double elapsedMs = (endTicks - _startTicks) * 1000.0 / Stopwatch.Frequency;

                Log(LogCategory.Timing, LogLevel.Debug,
                    "Timing [{0}]: {1:F3}ms",
                    _name, elapsedMs);

                // Accumulate for summary
                _timingAccumulators.AddOrUpdate(_name, elapsedMs, (_, total) => total + elapsedMs);
            }
        }

        #endregion
    }
}
