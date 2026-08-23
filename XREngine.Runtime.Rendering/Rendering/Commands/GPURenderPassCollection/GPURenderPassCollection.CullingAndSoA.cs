using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using XREngine;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Vulkan;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private const uint ComputeWorkGroupSize = 256;
        private static bool ForceGpuBvhCulling
            => XREnvironment.IsEnabled(XREngineEnvironmentVariables.ForceGpuBvhCulling);
        private GpuBvhSelectorCalibration _gpuBvhSelectorCalibration = new();
        private float _gpuBvhEstimatedVisibleRatio = 0.5f;

        private uint ResolveDisabledFlagsMask()
            => MeshSubmissionStrategy.IsGpuZeroReadbackStrategy()
                ? 0u
                : (uint)GPUIndirectRenderFlags.CpuFallbackOnly;

        /// <summary>
        /// Measured backend/view/visibility crossover table used by GPU BVH
        /// selection. Missing buckets remain on flat GPU culling.
        /// </summary>
        public GpuBvhSelectorCalibration GpuBvhCullingCalibration
        {
            get => _gpuBvhSelectorCalibration;
            set => _gpuBvhSelectorCalibration = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>The command threshold selected for the most recent cull.</summary>
        public uint LastGpuBvhCommandThreshold { get; private set; } =
            GpuBvhSelectorCalibration.UncalibratedCommandThreshold;

        // Set true to bypass GPU frustum/flag culling and treat all commands as visible (debug only).
        // Default is OFF; passthrough must be explicitly enabled in debug preferences.
        public bool ForcePassthroughCulling => RuntimeEngine.EditorPreferences?.Debug?.ForceGpuPassthroughCulling ?? false;

        private enum CullFrameMode
        {
            Passthrough,
            Frustum,
            Bvh,
            SoA
        }

        private int _culledSanitizerLogBudget = 8;
        private int _passthroughFallbackLogBudget = 4;
        private int _cpuFallbackRejectLogBudget = 6;
        private int _cpuFallbackDetailLogBudget = 8;
        private int _sanitizerDetailLogBudget = 4;
        private int _sanitizerSampleLogBudget = 12;
        private int _copyAtomicOverflowLogBudget = 4;
        private int _filteredCountLogBudget = 6;
        private int _shippingPolicyLogBudget = 8;
        private int _zeroVisibilityDiagnosticLogBudget = 6;
        private bool _loggedPassthroughCullMode;
        private bool _loggedFrustumCullMode;
        private bool _loggedBvhCullMode;
        private bool _loggedGpuBvhFallback;
        private bool _loggedExternalVrSharedVisibilityCullMode;
        private bool _skipGpuSubmissionThisPass;
        private string? _skipGpuSubmissionReason;
        private long _lastMaterialSnapshotTick = -1;
        private const int ValidationSignatureLogLimit = 256;

        private const uint PassFilterDebugComponentsPerSample = 4;
        private const uint PassFilterDebugMaxSamples = 32;

        /// <summary>
        /// Resets all log budgets to their initial values. Call periodically (e.g., per-scene or per-frame) to restore logging.
        /// </summary>
        public void ResetLogBudgets()
        {
            _culledSanitizerLogBudget = 8;
            _passthroughFallbackLogBudget = 4;
            _cpuFallbackRejectLogBudget = 6;
            _cpuFallbackDetailLogBudget = 8;
            _sanitizerDetailLogBudget = 4;
            _sanitizerSampleLogBudget = 12;
            _copyAtomicOverflowLogBudget = 4;
            _filteredCountLogBudget = 6;
            _shippingPolicyLogBudget = 8;

            _loggedGpuHiZOcclusionScaffold = false;
            _loggedCpuQueryAsyncScaffold = false;
        }

        /// <summary>
        /// Reads unsigned integer values from a mapped buffer into the specified span.
        /// </summary>
        /// <remarks>If the buffer is not mapped, the method logs a warning and sets all elements of the
        /// <paramref name="values"/> span to 0.</remarks>
        /// <param name="buf">The buffer from which to read data. The buffer must be mapped before calling this method.</param>
        /// <param name="values">The span to populate with the unsigned integer values read from the buffer. The length of the span
        /// determines the number of values to read.</param>
        /// <exception cref="Exception">Thrown if the buffer's mapped address is null.</exception>
        private void ReadUints(XRDataBuffer buf, Span<uint> values)
        {
            if (!buf.IsMapped)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Buffers")} ReadUints failed - buffer not mapped");
                for (int i = 0; i < values.Length; i++)
                    values[i] = 0;
                return;
            }

            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(values.Length * sizeof(uint));

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            if (!buf.TryReadMapped(ref values, static (scoped ReadOnlySpan<byte> bytes, ref Span<uint> destination) =>
            {
                MemoryMarshal.Cast<byte, uint>(bytes)[..destination.Length].CopyTo(destination);
                return true;
            }))
                throw new InvalidOperationException("ReadUints failed - buffer mapped address is null");
        }

        /// <summary>
        /// Writes an array of unsigned integers to the specified data buffer.
        /// </summary>
        /// <remarks>This method writes the provided unsigned integers to the specified buffer.  The
        /// caller is responsible for ensuring that the buffer has sufficient capacity to store the values.</remarks>
        /// <param name="buf">The data buffer to which the unsigned integers will be written.</param>
        /// <param name="values">An array of unsigned integers to write to the buffer. This parameter can be empty.</param>
        private void WriteUints(XRDataBuffer buf, params uint[] values)
            => WriteUints(buf, values.AsSpan());

        /// <summary>
        /// Writes an array of unsigned integers to the mapped memory of the specified buffer.
        /// </summary>
        /// <remarks>This method writes the provided values sequentially to the memory region mapped by
        /// the buffer.  If the buffer is not mapped, the method logs a warning and exits without performing any write
        /// operation.</remarks>
        /// <param name="buf">The <see cref="XRDataBuffer"/> to which the values will be written. The buffer must be mapped before calling
        /// this method.</param>
        /// <param name="values">A read-only span of unsigned integers to write to the buffer.</param>
        /// <exception cref="Exception">Thrown if the buffer's mapped address is null.</exception>
        private void WriteUints(XRDataBuffer buf, ReadOnlySpan<uint> values)
        {
            if (!buf.IsMapped)
            {
                for (uint i = 0; i < values.Length; i++)
                    buf.SetDataRawAtIndex(i, values[(int)i]);

                buf.PushSubData(0, (uint)(values.Length * sizeof(uint)));
                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.Command);
                return;
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            if (!buf.TryWriteMapped(ref values, static (scoped Span<byte> bytes, ref ReadOnlySpan<uint> source) =>
            {
                source.CopyTo(MemoryMarshal.Cast<byte, uint>(bytes));
                return true;
            }))
                throw new InvalidOperationException("WriteUints failed - buffer mapped address is null");

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
        }

        /// <summary>
        /// Reads an unsigned 32-bit integer from the specified index within the mapped memory of the given buffer.
        /// </summary>
        /// <remarks>Ensure that the buffer is properly mapped before calling this method. If the buffer
        /// is not mapped, a warning will be logged, and the method will return 0.</remarks>
        /// <param name="buf">The <see cref="XRDataBuffer"/> from which to read the value. The buffer must be mapped before calling this
        /// method.</param>
        /// <param name="index">The zero-based index of the value to read within the mapped memory.</param>
        /// <returns>The unsigned 32-bit integer located at the specified index.</returns>
        /// <exception cref="Exception">Thrown if the mapped memory address is null.</exception>
        private uint ReadUIntAt(XRDataBuffer buf, uint index)
        {
            bool mappedTemporarily = false;

            try
            {
                if (!buf.IsMapped)
                {
                    buf.MapBufferData();
                    if (!buf.IsMapped)
                        return buf.GetDataRawAtIndex<uint>(index);
                    mappedTemporarily = true;
                    RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                uint value = 0;
                if (!buf.TryReadMapped(bytes =>
                {
                    value = MemoryMarshal.Cast<byte, uint>(bytes)[checked((int)index)];
                    return true;
                }))
                    throw new InvalidOperationException("ReadUIntAt failed - buffer mapped address is null");
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(sizeof(uint));
                return value;
            }
            finally
            {
                if (mappedTemporarily)
                    buf.UnmapBufferData();
            }
        }

        /// <summary>
        /// Writes an unsigned integer value at the specified index within the mapped memory of the buffer.
        /// </summary>
        /// <remarks>If the buffer is not mapped, the method logs a warning and does not perform the write
        /// operation.</remarks>
        /// <param name="buf">The <see cref="XRDataBuffer"/> instance whose mapped memory will be written to. The buffer must be mapped
        /// before calling this method.</param>
        /// <param name="index">The zero-based index within the buffer's mapped memory where the value will be written.</param>
        /// <param name="value">The unsigned integer value to write at the specified index.</param>
        /// <exception cref="Exception">Thrown if the buffer's mapped address is null.</exception>
        private void WriteUIntAt(XRDataBuffer buf, uint index, uint value)
        {
            if (!buf.IsMapped)
            {
                buf.SetDataRawAtIndex(index, value);
                int byteOffset = checked((int)(index * sizeof(uint)));
                buf.PushSubData(byteOffset, (uint)sizeof(uint));
                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.Command);
                return;
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            if (!buf.TryWriteMapped(bytes =>
            {
                MemoryMarshal.Cast<byte, uint>(bytes)[checked((int)index)] = value;
                return true;
            }))
                throw new InvalidOperationException("WriteUIntAt failed - buffer mapped address is null");

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
        }

        /// <summary>
        /// Reads an unsigned integer value from the specified GPU-mapped XR data buffer at index 0.
        /// </summary>
        /// <param name="buf">The <see cref="XRDataBuffer"/> from which to read the value. The buffer must be mapped.</param>
        /// <returns>The unsigned integer value read from the buffer. Returns 0 if the buffer is not mapped.</returns>
        /// <exception cref="Exception">Thrown if the buffer is mapped but the mapped address is a null pointer.</exception>
    private uint ReadUInt(XRDataBuffer buf)
        {
            bool mappedTemporarily = false;

            try
            {
                if (!buf.IsMapped)
                {
                    buf.MapBufferData();
                    if (!buf.IsMapped)
                        return buf.GetDataRawAtIndex<uint>(0);
                    mappedTemporarily = true;
                    RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                uint value = 0;
                if (!buf.TryReadMapped(bytes =>
                {
                    value = MemoryMarshal.Cast<byte, uint>(bytes)[0];
                    return true;
                }))
                    throw new InvalidOperationException("ReadUInt failed - buffer mapped address is null");
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(sizeof(uint));
                return value;
            }
            finally
            {
                if (mappedTemporarily)
                    buf.UnmapBufferData();
            }
        }

        /// <summary>
        /// Writes an unsigned integer value to the specified GPU-mapped XR data buffer at index 0.
        /// </summary>
        /// <param name="buf">The <see cref="XRDataBuffer"/> to which the value will be written. The buffer must be mapped.</param>
        /// <param name="value">The unsigned integer value to write to the buffer.</param>
        /// <exception cref="InvalidOperationException">Thrown if the buffer is mapped but the mapped address is null.</exception>
        private void WriteUInt(XRDataBuffer buf, uint value)
        {
            if (!buf.IsMapped)
            {
                buf.SetDataRawAtIndex(0, value);
                buf.PushSubData(0, (uint)sizeof(uint));
                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.Command);
            }
            else
            {
                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                if (!buf.TryWriteMapped(bytes =>
                {
                    MemoryMarshal.Cast<byte, uint>(bytes)[0] = value;
                    return true;
                }))
                    throw new InvalidOperationException("WriteUInt failed - buffer mapped address is null");

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
            }

            if (IsCountBufferWriteLoggingEnabledForPass())
            {
                string label = buf.AttributeName ?? buf.Target.ToString();
                Debug.Meshes($"{FormatDebugPrefix("Indirect")} [Indirect/Count] {label} <= {value}");
            }
        }

        private static void BindStorageBuffer(XRRenderProgram program, XRDataBuffer buffer, uint location)
        {
            if (buffer.Target == EBufferTarget.ParameterBuffer)
                buffer.BindTo(program, location);
            else
                program.BindBuffer(buffer, location);
        }

        private void ResetVisibleCounters()
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ResetVisibleCounters");

            VisibleCommandCount = 0;
            VisibleInstanceCount = 0;
            if (_culledCountBuffer is null)
                return;

            // The zero-readback path records GPURenderResetCounters into the frame command stream.
            // A one-shot upload here is outside that stream and can race the prior in-flight frame,
            // clearing CulledCount before its downstream material-scatter dispatch consumes it.
            // ResetBaseCountersOnCpu still performs the upload when the GPU reset contract is absent.
            if (IsCpuReadbackCountDisabledForPass() && _resetCountersComputeShader is not null)
                return;

            WriteUints(_culledCountBuffer, 0u, 0u, 0u);
        }

        private void UpdateVisibleCountersFromBuffer()
        {
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
        }

        private void UpdateVisibleCountersFromBuffer(XRDataBuffer? countBuffer)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.UpdateVisibleCountersFromBuffer");

            if (IsCpuReadbackCountDisabledForPass())
            {
                // GPU-driven path: the count buffer is consumed directly by GPU dispatches.
                // Keep a CPU-side upper bound for dispatch sizing without falling back to the
                // full allocated capacity; the real count remains in the GPU count buffer.
                uint upperBound = _visibleCommandUpperBoundValid
                    ? Math.Min(_visibleCommandUpperBound, CommandCapacity)
                    : CommandCapacity;
                VisibleCommandCount = upperBound;
                VisibleInstanceCount = upperBound;
                return;
            }

            if (countBuffer is null)
            {
                VisibleCommandCount = 0;
                VisibleInstanceCount = 0;
                return;
            }

            uint draws = ReadUIntAt(countBuffer, GPUScene.VisibleCountDrawIndex);
            uint instances = ReadUIntAt(countBuffer, GPUScene.VisibleCountInstanceIndex);

            if (draws == 0 && ReferenceEquals(countBuffer, _culledCountBuffer) && _statsBuffer is not null)
            {
                AbstractRenderer.Current?.MemoryBarrier(
                    EMemoryBarrierMask.ShaderStorage |
                    EMemoryBarrierMask.Command |
                    EMemoryBarrierMask.ClientMappedBuffer);

                uint statsCulled = ReadUIntAt(_statsBuffer, GpuStatsLayout.StatsCulledCount);
                if (statsCulled > 0)
                {
                    uint recoveredDraws = Math.Min(statsCulled, CommandCapacity);
                    if (_visibleCommandUpperBoundValid)
                        recoveredDraws = Math.Min(recoveredDraws, _visibleCommandUpperBound);

                    if (recoveredDraws > 0)
                    {
                        uint recoveredInstances = instances > 0 ? instances : recoveredDraws;

                        draws = recoveredDraws;
                        instances = recoveredInstances;

                        // The culled command buffer was populated, but the count buffer read as stale zero.
                        // Restore the recovered counts so downstream GPU scatter/draw stages do not consume zero.
                        ReadOnlySpan<uint> recoveredCounts = stackalloc uint[]
                        {
                            recoveredDraws,
                            recoveredInstances,
                            0u
                        };
                        WriteUints(countBuffer, recoveredCounts);
                    }

                    if (_filteredCountLogBudget > 0)
                    {
                        Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Visible-count readback returned 0, but stats buffer reported {statsCulled} visible commands. Restored {draws} visible commands into the count buffer for this frame.");
                        _filteredCountLogBudget--;
                    }
                }
            }

            VisibleCommandCount = draws;
            VisibleInstanceCount = instances;
        }

        private void WriteVisibleCounters(uint draws, uint instances, uint overflow = 0)
        {
            VisibleCommandCount = draws;
            VisibleInstanceCount = instances;
            if (_culledCountBuffer is null)
                return;

            WriteUints(_culledCountBuffer, draws, instances, overflow);
        }

        private void EnsurePassFilterDebugBuffer(uint sampleCount, bool clearContents = true)
        {
            if (sampleCount == 0)
                return;

            uint requiredElements = Math.Max(sampleCount * PassFilterDebugComponentsPerSample, 1u);
            bool recreated = false;

            if (_passFilterDebugBuffer is null || _passFilterDebugBuffer.ElementCount < requiredElements)
            {
                _passFilterDebugBuffer?.Dispose();
                _passFilterDebugBuffer = new XRDataBuffer("PassFilterDebug", EBufferTarget.ShaderStorageBuffer, requiredElements, EComponentType.UInt, 1, false, true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true
                };
                _passFilterDebugBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read;
                _passFilterDebugBuffer.RangeFlags |= EBufferMapRangeFlags.Read;
                _passFilterDebugBuffer.Generate();
                recreated = true;
            }

            if (!recreated && !clearContents)
                return;

            for (uint i = 0; i < requiredElements; ++i)
                _passFilterDebugBuffer!.SetDataRawAtIndex(i, 0u);

            uint byteCount = requiredElements * (uint)sizeof(uint);
            _passFilterDebugBuffer!.PushSubData(0, byteCount);
        }

        private unsafe void DumpPassFilterDebug(uint sampleCount)
        {
            if (!IsDebugLoggingEnabledForPass())
                return;

            if (_passFilterDebugBuffer is null || sampleCount == 0)
                return;

            bool mappedLocally = false;

            try
            {
                if (_passFilterDebugBuffer.ActivelyMapping.Count == 0)
                {
                    _passFilterDebugBuffer.MapBufferData();
                    mappedLocally = true;
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);
                uint loggedSamples = Math.Min(sampleCount, PassFilterDebugMaxSamples);

                var sb = new StringBuilder();
                sb.Append("PassFilterDebug samples: ");
                if (!_passFilterDebugBuffer.TryReadMapped(bytes =>
                {
                    ReadOnlySpan<uint> data = MemoryMarshal.Cast<byte, uint>(bytes);
                    for (uint i = 0; i < loggedSamples; ++i)
                    {
                        int baseIndex = checked((int)(i * PassFilterDebugComponentsPerSample));
                        uint cmdIndex = data[baseIndex + 0];
                        uint passValue = data[baseIndex + 1];
                        uint accepted = data[baseIndex + 2];
                        uint expected = data[baseIndex + 3];
                        if (i > 0)
                            sb.Append(" | ");
                        sb.Append('#').Append(cmdIndex).Append(" pass=").Append(passValue);
                        if (expected != 0xFFFFFFFFu)
                            sb.Append(" expected=").Append(expected);
                        sb.Append(accepted == 1 ? " accepted" : " rejected");
                    }
                    return true;
                }))
                {
                    Dbg("PassFilterDebug aborted; debug buffer not mapped.", "Culling");
                    return;
                }

                Dbg(sb.ToString(), "Culling");
            }
            finally
            {
                if (mappedLocally)
                    _passFilterDebugBuffer?.UnmapBufferData();
            }
        }

        public void Cull(GPUScene gpuCommands, XRCamera? camera, bool deferGpuHiZ = false)
        {
            using var timing = BeginTiming("GPURenderPassCollection.Cull");
            Stopwatch cullStopwatch = Stopwatch.StartNew();
            InvalidateExactCommandViewMasks();

            void RecordCullTiming()
            {
                if (!cullStopwatch.IsRunning)
                    return;

                cullStopwatch.Stop();
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                    RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Cull,
                    cullStopwatch.Elapsed);
            }
            
            LogCullingStart("Cull", gpuCommands.TotalCommandCount);
            Dbg("Cull invoked","Culling");
            ResetOcclusionFrameStats();

            // Rebuild internal BVH if dirty (before we try to use it)
            gpuCommands.RebuildBvhIfDirty();

            //Early out if no commands
            uint numCommands = gpuCommands.TotalCommandCount;
            _visibleCommandUpperBound = Math.Min(numCommands, CommandCapacity);
            _visibleCommandUpperBoundValid = true;
            if (numCommands == 0)
            {
                VisibleCommandCount = 0;
                VisibleInstanceCount = 0;
                Dbg("Cull: no commands","Culling");
                Log(LogCategory.Culling, LogLevel.Debug, "Cull: no commands - early exit");
                RecordCullTiming();
                return;
            }


            bool externalVrSharedVisibility = ShouldUseExternalVrSharedVisibilityPassFilter(camera);

            if (externalVrSharedVisibility)
            {
                LogExternalVrSharedVisibilityCullMode();
                PassthroughCull(gpuCommands, numCommands);
            }
            // Passthrough path is diagnostics-only on Vulkan runtime profiles.
            else if (ShouldUsePassthroughCulling())
            {
                LogCullModeActivation(CullFrameMode.Passthrough);
                PassthroughCull(gpuCommands, numCommands);
            }
            else if (ShouldUseBvhCulling(gpuCommands, numCommands))
            {
                LogCullModeActivation(CullFrameMode.Bvh);
                BvhCull(gpuCommands, camera, numCommands);
            }
            else
            {
                LogCullModeActivation(CullFrameMode.Frustum);
                FrustumCull(gpuCommands, camera, numCommands);
            }

            ApplyOcclusionCulling(gpuCommands, camera, deferGpuHiZ);

            bool sanitizerOk = true;
            if (VisibleCommandCount > 0 && !IsCpuReadbackCountDisabledForPass())
                sanitizerOk = SanitizeCulledCommands(gpuCommands);

            if (_skipGpuSubmissionThisPass || !sanitizerOk)
            {
                ResetVisibleCounters();

                string reason = _skipGpuSubmissionReason ?? "command corruption detected";
                Warn(LogCategory.Culling, "Skipping GPU submission: {0}", reason);
                RecordCullTiming();
                return;
            }

            LogCullingResult("Cull", numCommands, VisibleCommandCount, VisibleInstanceCount);

            if (IsDebugLoggingEnabledForPass())
                XREngine.Debug.Meshes($"GPURenderPassCollection.Cull: {numCommands} input commands -> {VisibleCommandCount} visible commands ({VisibleInstanceCount} instances) in CulledSceneToRenderBuffer");

            RecordCullTiming();
        }

        private void LogCullModeActivation(CullFrameMode mode)
        {
            bool shouldLog;
            string modeName;

            switch (mode)
            {
                case CullFrameMode.Passthrough:
                    shouldLog = !_loggedPassthroughCullMode;
                    _loggedPassthroughCullMode = true;
                    modeName = "passthrough";
                    break;
                case CullFrameMode.Bvh:
                    shouldLog = !_loggedBvhCullMode;
                    _loggedBvhCullMode = true;
                    modeName = "BVH";
                    break;
                default:
                    shouldLog = !_loggedFrustumCullMode;
                    _loggedFrustumCullMode = true;
                    modeName = "frustum";
                    break;
            }

            if (!shouldLog)
                return;

            Log(LogCategory.Culling, LogLevel.Info, "Culling mode active: {0} (pass={1})", modeName, RenderPass);
        }


        private static void RecordCpuFallbackUsage(uint recoveredCommands)
            => RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, (int)Math.Min(recoveredCommands, int.MaxValue));

        private bool ShouldUsePassthroughCulling()
        {
            if (!ForcePassthroughCulling)
                return false;

            if (!VulkanFeatureProfile.IsActive)
                return true;

            if (VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics)
                return true;

            if (_shippingPolicyLogBudget > 0)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Passthrough culling request ignored for profile {VulkanFeatureProfile.ActiveProfile}; canonical GPU cull path remains active.");
                _shippingPolicyLogBudget--;
            }

            return false;
        }

        private bool ShouldAllowCpuFallback()
        {
            if (_passPolicySnapshotValid)
            {
                if (VulkanFeatureProfile.EnforceStrictNoFallbacks)
                    return false;

                return _passAllowCpuFallback;
            }

            if (VulkanFeatureProfile.EnforceStrictNoFallbacks)
                return false;

            bool fallbackRequested = (RuntimeEngine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
                || (RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging && RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback);

            if (!fallbackRequested)
                return false;

            if (!VulkanFeatureProfile.IsActive)
                return true;

            return VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;
        }

        private bool IsCpuFallbackRequestedForPass()
            => _passPolicySnapshotValid
                ? IsInstrumentedGpuStrategy(MeshSubmissionStrategy) && _passCpuFallbackRequested
                : IsInstrumentedGpuStrategy(MeshSubmissionStrategy) &&
                    ((RuntimeEngine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
                     || (RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging &&
                         RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback));

        private void LogCpuFallbackSuppressed(string stageName)
        {
            // A zero-visible result is valid culling output. It is not a blocked
            // fallback unless an operator explicitly requested CPU recovery.
            if (!IsCpuFallbackRequestedForPass())
                return;

            RecordForbiddenFallback($"{stageName} attempted CPU recovery with strict no-fallback profile.");
            if (_passthroughFallbackLogBudget <= 0)
                return;

            string profileName = VulkanFeatureProfile.IsActive
                ? VulkanFeatureProfile.ActiveProfile.ToString()
                : "non-vulkan";

            Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} {stageName} returned 0 for pass {RenderPass}; CPU fallback suppressed by profile {profileName}.");
            _passthroughFallbackLogBudget--;
        }

        /// <summary>
        /// GPU frustum culling mode – performs actual frustum culling on the GPU using the existing culling compute shader.
        /// </summary>
        /// <remarks>
        /// Uses the GPURenderCulling.comp shader to perform per-command frustum sphere tests.
        /// Commands outside the camera frustum are rejected before being appended to the culled buffer.
        /// </remarks>
        private void FrustumCull(GPUScene scene, XRCamera? camera, uint numCommands)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.FrustumCull");

            _skipGpuSubmissionThisPass = false;
            _skipGpuSubmissionReason = null;

            // Fall back to passthrough if we don't have the required resources
            if (CulledSceneToRenderBuffer is null || _cullingComputeShader is null || _culledCountBuffer is null || camera is null)
            {
                Dbg("FrustumCull: missing resources, falling back to passthrough", "Culling");
                PassthroughCull(scene, numCommands);
                return;
            }

            XRDataBuffer src = scene.CullControlBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint inputCount = Math.Min(numCommands, capacity);
            uint activeViewCount = _activeViewCount == 0u ? 1u : _activeViewCount;

            if (inputCount == 0)
            {
                ResetVisibleCounters();
                Dbg("FrustumCull: no commands", "Culling");
                return;
            }

            PrepareCommandViewMasks(src, inputCount);

            bool debugLoggingEnabled = IsDebugLoggingEnabledForPass();

            if (IsSourceCommandProbeEnabledForPass())
                DumpSourceCommandProbe(scene, inputCount);

            // Reset counters before dispatch
            ResetVisibleCounters();
            if (_cullingOverflowFlagBuffer is not null)
                WriteUInt(_cullingOverflowFlagBuffer, 0u);

            // Extract frustum planes from camera
            Frustum? frustumNullable = camera.WorldFrustum();
            if (frustumNullable is null)
            {
                Dbg("FrustumCull: no frustum available, falling back to passthrough", "Culling");
                PassthroughCull(scene, numCommands);
                return;
            }
            Frustum frustum = frustumNullable.Value;

            // Get frustum planes (6 planes: near, far, left, right, top, bottom)
            // Each plane is stored as vec4(normal.xyz, d) where the plane equation is: dot(normal, point) + d = 0
            Vector4[] planeData = ExtractFrustumPlanesAsVec4(frustum);

            // Set uniforms for the culling shader
            _cullingComputeShader.Uniform("FrustumPlanes", planeData);
            _cullingComputeShader.Uniform("MaxRenderDistance", camera.FarZ * camera.FarZ); // squared distance
            uint mask = unchecked((uint)camera.CullingMask.Value);
            _cullingComputeShader.Uniform("CameraLayerMask", mask);
            _cullingComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _cullingComputeShader.Uniform("InputCommandCount", (int)inputCount);
            _cullingComputeShader.Uniform("MaxCulledCommands", (int)capacity);
            _cullingComputeShader.Uniform("DisabledFlagsMask", ResolveDisabledFlagsMask());
            _cullingComputeShader.Uniform("CameraPosition", camera.Transform?.RenderTranslation ?? System.Numerics.Vector3.Zero);
            _cullingComputeShader.Uniform("ActiveViewCount", (int)activeViewCount);

            // Bind Phase C SoA scene buffers.
            scene.CullControlBuffer.BindTo(_cullingComputeShader, 0);
            scene.CullBoundsBuffer.BindTo(_cullingComputeShader, 1);
            _cullingComputeShader.BindBuffer(dst, 2);
            BindStorageBuffer(_cullingComputeShader, _culledCountBuffer!, 3);
            if (_cullingOverflowFlagBuffer is not null)
                _cullingComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 4);
            if (_statsBuffer is not null)
                _cullingComputeShader.BindBuffer(_statsBuffer, 8);
            BindViewSetBuffers(_cullingComputeShader);

            // Dispatch compute shader
            const EMemoryBarrierMask postCullBarrier =
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command | EMemoryBarrierMask.ClientMappedBuffer;
            (uint x, uint y, uint z) = XRRenderProgram.ComputeDispatch.ForCommands(inputCount);
            {
                using var cullTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Cull, inputCount);
                _cullingComputeShader.DispatchCompute(x, y, z, postCullBarrier);
            }


            // Check for overflow
            if (_cullingOverflowFlagBuffer is not null && ShouldCaptureDiagnosticReadbacksForPass())
            {
                uint overflowCount = ReadUInt(_cullingOverflowFlagBuffer);
                if (overflowCount > 0)
                {
                    if (_copyAtomicOverflowLogBudget > 0)
                    {
                        Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Frustum cull overflow: {overflowCount} commands exceeded capacity {capacity}.");
                        _copyAtomicOverflowLogBudget--;
                    }
                }
            }

            // Read back visible counts
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            uint visibleCount = Math.Min(VisibleCommandCount, inputCount);

            if (visibleCount == 0 && inputCount > 0)
                LogZeroVisibilityDiagnostics(scene, camera, inputCount);

            if (debugLoggingEnabled)
            {
                Debug.Meshes($"{FormatDebugPrefix("Culling")} FrustumCull: {inputCount} input -> {visibleCount} visible ({VisibleInstanceCount} instances)");
            }

            // Handle CPU fallback if GPU produced no results
            bool allowCpuFallback = ShouldAllowCpuFallback();

            if (visibleCount == 0 && RenderPass >= 0)
            {
                if (allowCpuFallback)
                {
                    uint cpuRecovered = CpuCopyCommandsForPass(scene, inputCount, commit: true, out uint cpuInstanceCount);
                    RecordCpuFallbackUsage(cpuRecovered);
                    if (cpuRecovered > 0)
                    {
                        visibleCount = cpuRecovered;
                        WriteVisibleCounters(cpuRecovered, cpuInstanceCount);
                        if (_passthroughFallbackLogBudget > 0)
                        {
                            Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} GPU frustum cull returned 0; CPU fallback restored {cpuRecovered} commands for pass {RenderPass}.");
                            _passthroughFallbackLogBudget--;
                        }
                    }
                }
                else
                    LogCpuFallbackSuppressed("GPU frustum cull");
            }

            VisibleCommandCount = Math.Min(visibleCount, inputCount);
            Dbg($"FrustumCull complete: visible={VisibleCommandCount} instances={VisibleInstanceCount}", "Culling");

            // Update stats buffer
            if (_statsBuffer is not null)
            {
                ReadOnlySpan<uint> statSeed = stackalloc uint[]
                {
                    inputCount,
                    VisibleCommandCount,
                    0u,
                    0u,
                    0u
                };
                WriteUints(_statsBuffer, statSeed);
            }
        }

        private bool ShouldUseExternalVrSharedVisibilityPassFilter(XRCamera? camera)
        {
            if (camera?.StereoEyeLeft.HasValue != true)
                return false;

            IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
            IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
            IRuntimeRenderCommandExecutionState? renderState = frameTiming.ActiveRenderCommandExecutionState;
            if (renderState?.StereoPass == true || RuntimeEngine.Rendering.State.IsStereoPass)
                return false;

            bool externalOpenXr =
                renderState?.WindowViewport is XRViewport { RendersToExternalSwapchainTarget: true } &&
                presentation.IsOpenXrRuntimeRequested;
            return externalOpenXr && RetainExternalOpenXrSharedVisibilityException();
        }

        private void LogExternalVrSharedVisibilityCullMode()
        {
            if (_loggedExternalVrSharedVisibilityCullMode)
                return;

            _loggedExternalVrSharedVisibilityCullMode = true;
            Log(
                LogCategory.Culling,
                LogLevel.Info,
                "Culling mode active: external OpenXR stereo shared-visibility pass filter (pass={0})",
                RenderPass);
        }

        /// <summary>
        /// Extracts frustum planes from a Frustum object into a Vector4 array for GPU upload.
        /// Each plane is stored as vec4(normal.xyz, d) where the plane equation is: dot(normal, point) + d = 0
        /// </summary>
        private static Vector4[] ExtractFrustumPlanesAsVec4(Frustum frustum)
        {
            IReadOnlyList<System.Numerics.Plane> planes = frustum.Planes;
            Vector4[] result = new Vector4[6];
            for (int i = 0; i < 6 && i < planes.Count; i++)
            {
                var plane = planes[i];
                result[i] = new Vector4(plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D);
            }
            return result;
        }

        /// <summary>
        /// Determines whether BVH-accelerated culling should be used based on strategy and resource readiness.
        /// </summary>
        private bool ShouldUseBvhCulling(GPUScene scene, uint commandCount)
        {
            if (!scene.UseGpuBvh)
                return false;

            GpuBvhCullingBackend backend = AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan
                ? GpuBvhCullingBackend.Vulkan
                : GpuBvhCullingBackend.OpenGl;
            GpuBvhSelectorBucket bucket = GpuBvhSelectorBucket.From(
                backend,
                _activeViewCount == 0u ? 1u : _activeViewCount,
                _gpuBvhEstimatedVisibleRatio);
            LastGpuBvhCommandThreshold = ForceGpuBvhCulling
                ? 1u
                : _gpuBvhSelectorCalibration.GetCommandThreshold(bucket);
            if (!ForceGpuBvhCulling && !_gpuBvhSelectorCalibration.ShouldUseBvh(bucket, commandCount))
                return false;

            if (_bvhFrustumCullProgram is null)
            {
                LogGpuBvhFallback("BVH frustum-cull shader is unavailable");
                return false;
            }

            var provider = scene.BvhProvider;
            if (provider is null)
            {
                LogGpuBvhFallback("scene has no GPU BVH provider");
                return false;
            }

            if (!provider.IsBvhReady)
            {
                LogGpuBvhFallback("GPU BVH resources are not ready");
                return false;
            }

            return true;
        }

        private void LogGpuBvhFallback(string reason)
        {
            if (_loggedGpuBvhFallback)
                return;

            _loggedGpuBvhFallback = true;
            Warn(
                LogCategory.Culling,
                "GPU BVH was selected for pass {0}, but {1}; using flat GPU frustum culling until it is ready.",
                RenderPass,
                reason);
        }

        /// <summary>
        /// BVH-accelerated frustum culling mode – traverses the GPU BVH hierarchy to quickly reject
        /// large portions of the scene before testing individual commands.
        /// </summary>
        /// <remarks>
        /// This path uses a bounded cooperative root-down traversal. Internal-node rejection
        /// skips complete primitive ranges and queue pressure falls back conservatively.
        /// </remarks>
        private void BvhCull(GPUScene scene, XRCamera? camera, uint numCommands)
        {
            _skipGpuSubmissionThisPass = false;
            _skipGpuSubmissionReason = null;

            var bvhProvider = scene.BvhProvider;

            // Ensure GPU BVH/AABB data is up-to-date before culling
            scene.PrepareBvhForCulling(numCommands);

            // Validate prerequisites
            if (CulledSceneToRenderBuffer is null ||
                _bvhFrustumCullProgram is null || 
                _culledCountBuffer is null || 
                camera is null || 
                bvhProvider is null || 
                !bvhProvider.IsBvhReady)
            {
                Dbg("BvhCull: missing resources, falling back to FrustumCull", "Culling");
                FrustumCull(scene, camera, numCommands);
                return;
            }

            XRDataBuffer? bvhNodes = bvhProvider.BvhNodeBuffer;
            XRDataBuffer? bvhMorton = bvhProvider.BvhMortonBuffer;

            if (bvhNodes is null || bvhMorton is null)
            {
                Dbg("BvhCull: BVH buffers not ready, falling back to FrustumCull", "Culling");
                FrustumCull(scene, camera, numCommands);
                return;
            }

            XRDataBuffer src = scene.CullControlBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint inputCount = Math.Min(numCommands, capacity);
            uint activeViewCount = _activeViewCount == 0u ? 1u : _activeViewCount;

            if (inputCount == 0)
            {
                ResetVisibleCounters();
                Dbg("BvhCull: no commands", "Culling");
                return;
            }

            PrepareCommandViewMasks(src, inputCount);

            bool debugLoggingEnabled = IsDebugLoggingEnabledForPass();

            if (IsSourceCommandProbeEnabledForPass())
                DumpSourceCommandProbe(scene, inputCount);

            // Reset counters before dispatch
            ResetVisibleCounters();
            if (_cullingOverflowFlagBuffer is not null)
                WriteUInt(_cullingOverflowFlagBuffer, 0u);

            // Extract frustum planes from camera
            Frustum? frustumNullable = camera.WorldFrustum();
            if (frustumNullable is null)
            {
                Dbg("BvhCull: no frustum available, falling back to FrustumCull", "Culling");
                FrustumCull(scene, camera, numCommands);
                return;
            }

            // Get frustum planes
            Frustum frustum = frustumNullable.Value;
            Vector4[] planeData = ExtractFrustumPlanesAsVec4(frustum);

            // Set uniforms for the BVH culling shader
            _bvhFrustumCullProgram.Uniform("FrustumPlanes", planeData);
            _bvhFrustumCullProgram.Uniform("UseClusterPlanes", 0u);
            _bvhFrustumCullProgram.Uniform("UseClusterPlaneBuffer", 0u);
            _bvhFrustumCullProgram.Uniform("ClusterPlaneOffset", 0u);
            _bvhFrustumCullProgram.Uniform("ClusterPlaneStride", 0u);
            _bvhFrustumCullProgram.Uniform("MaxRenderDistance", camera.FarZ * camera.FarZ); // squared distance
            uint mask = unchecked((uint)camera.CullingMask.Value);
            _bvhFrustumCullProgram.Uniform("CameraLayerMask", mask);
            _bvhFrustumCullProgram.Uniform("CurrentRenderPass", RenderPass);
            _bvhFrustumCullProgram.Uniform("InputCommandCount", (int)inputCount);
            _bvhFrustumCullProgram.Uniform("MaxCulledCommands", (int)capacity);
            _bvhFrustumCullProgram.Uniform("DisabledFlagsMask", ResolveDisabledFlagsMask());
            _bvhFrustumCullProgram.Uniform("CameraPosition", camera.Transform?.RenderTranslation ?? Vector3.Zero);
            _bvhFrustumCullProgram.Uniform("StatsEnabled", _statsBuffer is not null ? 1u : 0u);
            _bvhFrustumCullProgram.Uniform("OverflowDebugEnabled", 0u);
            _bvhFrustumCullProgram.Uniform("ENABLE_CPU_GPU_COMPARE", 0u); // OpenGL-compatible uniform (was Vulkan specialization constant)
            _bvhFrustumCullProgram.Uniform("ActiveViewCount", (int)activeViewCount);

            // Bind Phase C SoA scene buffers (metadata + bounds) and compact command output.
            scene.CullControlBuffer.BindTo(_bvhFrustumCullProgram, 0);
            scene.CullBoundsBuffer.BindTo(_bvhFrustumCullProgram, 1);
            _bvhFrustumCullProgram.BindBuffer(dst, 2);
            BindStorageBuffer(_bvhFrustumCullProgram, _culledCountBuffer!, 3);
            if (_cullingOverflowFlagBuffer is not null)
                _bvhFrustumCullProgram.BindBuffer(_cullingOverflowFlagBuffer, 4);

            // Bind BVH buffers
            _bvhFrustumCullProgram.BindBuffer(bvhNodes, 5);
            _bvhFrustumCullProgram.BindBuffer(bvhMorton, 7);

            // Bind optional buffers
            if (_statsBuffer is not null)
                _bvhFrustumCullProgram.BindBuffer(_statsBuffer, 8);
            if (_overflowDebugBuffer is not null)
                _bvhFrustumCullProgram.BindBuffer(_overflowDebugBuffer, 9);
            BindViewSetBuffers(_bvhFrustumCullProgram);

            uint nodeCount = bvhProvider.BvhNodeCount;
            uint leafCount = (nodeCount + 1u) / 2u;
            uint traversalWorkgroups = GpuBvhCullingDispatch.CalculateWorkgroupCount(inputCount);

            const EMemoryBarrierMask bvhPostCullBarrier =
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command | EMemoryBarrierMask.ClientMappedBuffer;
            {
                using var cullTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Cull, inputCount);
                using var traversalTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Traversal, nodeCount);
                _bvhFrustumCullProgram.DispatchCompute(traversalWorkgroups, 1u, 1u, bvhPostCullBarrier);
            }

            PublishExactCommandViewMasks();

            // Check for overflow
            if (_cullingOverflowFlagBuffer is not null && ShouldCaptureDiagnosticReadbacksForPass())
            {
                uint overflowCount = ReadUInt(_cullingOverflowFlagBuffer);
                if (overflowCount > 0)
                {
                    if (_copyAtomicOverflowLogBudget > 0)
                    {
                        Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} BVH cull overflow: {overflowCount} commands exceeded capacity {capacity}.");
                        _copyAtomicOverflowLogBudget--;
                    }
                }
            }

            // Read back visible counts
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            uint visibleCount = Math.Min(VisibleCommandCount, inputCount);

            if (debugLoggingEnabled)
            {
                Debug.Meshes($"{FormatDebugPrefix("Culling")} BvhCull: {inputCount} input -> {visibleCount} visible ({VisibleInstanceCount} instances) [BVH nodes={nodeCount}, leaves={leafCount}]");
            }

            // Handle CPU fallback if GPU produced no results
            bool allowCpuFallback = ShouldAllowCpuFallback();

            if (visibleCount == 0 && RenderPass >= 0)
            {
                if (allowCpuFallback)
                {
                    uint cpuRecovered = CpuCopyCommandsForPass(scene, inputCount, commit: true, out uint cpuInstanceCount);
                    RecordCpuFallbackUsage(cpuRecovered);
                    if (cpuRecovered > 0)
                    {
                        visibleCount = cpuRecovered;
                        WriteVisibleCounters(cpuRecovered, cpuInstanceCount);
                        if (_passthroughFallbackLogBudget > 0)
                        {
                            Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} GPU BVH cull returned 0; CPU fallback restored {cpuRecovered} commands for pass {RenderPass}.");
                            _passthroughFallbackLogBudget--;
                        }
                    }
                }
                else
                    LogCpuFallbackSuppressed("GPU BVH cull");
            }

            VisibleCommandCount = Math.Min(visibleCount, inputCount);
            Dbg($"BvhCull complete: visible={VisibleCommandCount} instances={VisibleInstanceCount}", "Culling");

            // Update stats buffer
            if (_statsBuffer is not null)
            {
                ReadOnlySpan<uint> statSeed = stackalloc uint[]
                {
                    inputCount,
                    VisibleCommandCount,
                    0u,
                    0u,
                    0u
                };
                WriteUints(_statsBuffer, statSeed);
            }
        }

        /// <summary>
        /// Culling passthrough mode – copy all input commands to culled buffer and mark all visible.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="numCommands"></param>
        private void PassthroughCull(GPUScene scene, uint numCommands)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.PassthroughCull");

            _skipGpuSubmissionThisPass = false;
            _skipGpuSubmissionReason = null;

            if (CulledSceneToRenderBuffer is null || _copyCommandsProgram is null || _culledCountBuffer is null)
            {
                ResetVisibleCounters();
                return;
            }
            
            XRDataBuffer src = scene.CullControlBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint copyCount = Math.Min(numCommands, capacity);
            uint activeViewCount = _activeViewCount == 0u ? 1u : _activeViewCount;

            if (copyCount == 0)
            {
                ResetVisibleCounters();
                Dbg("Cull passthrough no commands", "Culling");
                return;
            }

            PrepareCommandViewMasks(src, copyCount);

            bool debugLoggingEnabled = IsDebugLoggingEnabledForPass();

            if (IsSourceCommandProbeEnabledForPass())
                DumpSourceCommandProbe(scene, copyCount);

            // Copy commands
            ResetVisibleCounters();
            if (_cullingOverflowFlagBuffer is not null)
                WriteUInt(_cullingOverflowFlagBuffer, 0u);

            uint debugSamples = debugLoggingEnabled ? Math.Min(copyCount, PassFilterDebugMaxSamples) : 0u;
            EnsurePassFilterDebugBuffer(Math.Max(debugSamples, 1u), clearContents: debugSamples > 0u);
            if (_passFilterDebugBuffer is not null)
                _copyCommandsProgram.BindBuffer(_passFilterDebugBuffer, 3);

            if (debugSamples > 0)
            {
                _copyCommandsProgram.Uniform("DebugEnabled", 1);
                _copyCommandsProgram.Uniform("DebugMaxSamples", (int)debugSamples);
                _copyCommandsProgram.Uniform("DebugInstanceStride", (int)PassFilterDebugComponentsPerSample);
            }
            else
            {
                _copyCommandsProgram.Uniform("DebugEnabled", 0);
                _copyCommandsProgram.Uniform("DebugMaxSamples", 0);
                _copyCommandsProgram.Uniform("DebugInstanceStride", (int)PassFilterDebugComponentsPerSample);
            }

            _copyCommandsProgram.Uniform("CopyCount", copyCount);
            _copyCommandsProgram.Uniform("TargetPass", RenderPass);
            _copyCommandsProgram.Uniform("OutputCapacity", capacity);
            _copyCommandsProgram.Uniform("ActiveViewCount", (int)activeViewCount);
            int boundsCheckEnabled = (IsCopyBoundsValidationEnabledForPass() && _cullingOverflowFlagBuffer is not null) ? 1 : 0;
            _copyCommandsProgram.Uniform("BoundsCheckEnabled", boundsCheckEnabled);
            _copyCommandsProgram.BindBuffer(src, 0);
            _copyCommandsProgram.BindBuffer(dst, 1);
            BindStorageBuffer(_copyCommandsProgram, _culledCountBuffer!, 2);
            if (_cullingOverflowFlagBuffer is not null)
                _copyCommandsProgram.BindBuffer(_cullingOverflowFlagBuffer, 4);
            BindViewSetBuffers(_copyCommandsProgram);

            (uint x, uint y, uint z) = XRRenderProgram.ComputeDispatch.ForCommands(copyCount);
            {
                using var cullTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Cull, copyCount);
                _copyCommandsProgram.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command | EMemoryBarrierMask.ClientMappedBuffer);
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command | EMemoryBarrierMask.ClientMappedBuffer);

            if (debugSamples > 0)
                DumpPassFilterDebug(debugSamples);

            if (boundsCheckEnabled == 1 && _cullingOverflowFlagBuffer is not null && ShouldCaptureDiagnosticReadbacksForPass())
            {
                uint overflowMarker = ReadUInt(_cullingOverflowFlagBuffer);
                if (overflowMarker != 0u)
                {
                    uint offendingIndex = overflowMarker - 1u;
                    if (_copyAtomicOverflowLogBudget > 0)
                    {
                        Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Copy shader overflow detected at cmd={offendingIndex} (capacity={capacity}, copyCount={copyCount}).");
                        _copyAtomicOverflowLogBudget--;
                    }
                    _skipGpuSubmissionThisPass = true;
                    _skipGpuSubmissionReason ??= $"copy shader overflow detected at cmd={offendingIndex}";
                }
            }

            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            uint filteredCount = VisibleCommandCount;
            if (_filteredCountLogBudget > 0)
            {
                Debug.Meshes($"{FormatDebugPrefix("Culling")} Copy shader reported filteredCount={filteredCount} (copyCount={copyCount})");
                _filteredCountLogBudget--;
            }
            bool allowCpuFallback = ShouldAllowCpuFallback();

            if (filteredCount == 0 && RenderPass >= 0)
            {
                if (allowCpuFallback)
                {
                    uint cpuRecovered = CpuCopyCommandsForPass(scene, copyCount, commit: true, out uint cpuInstanceCount);
                    RecordCpuFallbackUsage(cpuRecovered);
                    if (cpuRecovered > 0)
                    {
                        filteredCount = cpuRecovered;
                        WriteVisibleCounters(cpuRecovered, cpuInstanceCount);
                        if (_passthroughFallbackLogBudget > 0)
                        {
                            Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} GPU pass filter returned 0; CPU fallback restored {cpuRecovered} commands for pass {RenderPass}.");
                            _passthroughFallbackLogBudget--;
                        }
                        Dbg($"Cull passthrough GPU produced 0; CPU fallback restored {cpuRecovered} commands", "Culling");
                    }
                }
                else
                {
                    LogCpuFallbackSuppressed("GPU pass filter");
                    if (IsDebugLoggingEnabledForPass())
                        LogCommandPassSample(scene, copyCount);
                }
            }

            VisibleCommandCount = Math.Min(filteredCount, copyCount);
          
            Dbg($"Cull passthrough visible={VisibleCommandCount} instances={VisibleInstanceCount} (input={copyCount})", "Culling");
            RunGpuCpuValidation(scene, copyCount, VisibleCommandCount);

            if (_statsBuffer is not null)
            {
                ReadOnlySpan<uint> statSeed = stackalloc uint[]
                {
                    copyCount,
                    VisibleCommandCount,
                    0u,
                    0u,
                    0u
                };
                WriteUints(_statsBuffer, statSeed);
            }
        }

        private uint CpuCopyCommandsForPass(GPUScene scene, uint copyCount, bool commit, out uint instanceCount)
        {
            instanceCount = 0;
            if (CulledSceneToRenderBuffer is null)
                return 0;

            bool matchAll = RenderPass < 0;
            uint targetPass = unchecked((uint)RenderPass);

            XRDataBuffer src = scene.CullControlBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint elementSize = dst.ElementSize;

            uint outIndex = 0;
            uint rejected = 0;
            uint fatalRejected = 0;
            ulong instanceAccumulator = 0;
            string? firstFatalRejection = null;
            for (uint i = 0; i < copyCount; ++i)
            {
                DrawMetadata cmd = src.GetDataRawAtIndex<DrawMetadata>(i);
                if (!TryPrepareCpuFallbackCommand(scene, matchAll, targetPass, ref cmd, out string? rejectionReason))
                {
                    rejected++;
                    bool isFatal = IsFatalCpuFallbackRejection(rejectionReason);
                    if (isFatal)
                    {
                        fatalRejected++;
                        if (rejectionReason is not null && _cpuFallbackDetailLogBudget > 0 && Interlocked.Decrement(ref _cpuFallbackDetailLogBudget) >= 0)
                            Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} CPU fallback reject idx={i} reason={rejectionReason} draw={cmd.DrawID} mesh={cmd.MeshID} material={cmd.MaterialID}");

                        if (firstFatalRejection is null)
                        {
                            string commandSummary = $"draw={cmd.DrawID} mesh={cmd.MeshID} material={cmd.MaterialID}";
                            firstFatalRejection = $"idx={i} reason={rejectionReason ?? "unknown"} {commandSummary}";
                        }
                    }
                    continue;
                }

                if (commit)
                    dst.SetDataRawAtIndex(outIndex, cmd.DrawID);
                outIndex++;
                instanceAccumulator += cmd.InstanceCount;
            }

            if (fatalRejected > 0)
            {
                if (_cpuFallbackRejectLogBudget > 0)
                {
                    Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} CPU fallback rejected {fatalRejected} commands for pass {RenderPass} due to invalid metadata.");
                    _cpuFallbackRejectLogBudget--;
                }

                _skipGpuSubmissionThisPass = true;
                if (string.IsNullOrEmpty(_skipGpuSubmissionReason))
                {
                    string detailSuffix = firstFatalRejection is not null ? $" (first {firstFatalRejection})" : string.Empty;
                    _skipGpuSubmissionReason = $"CPU fallback rejected {fatalRejected} of {copyCount} commands{detailSuffix}.";
                }
            }
            else if (rejected > 0)
            {
                Dbg($"CPU fallback skipped {rejected} commands for pass {RenderPass} (non-fatal reasons).", "Culling");
            }

            if (commit && outIndex > 0)
            {
                uint byteCount = outIndex * elementSize;
                dst.PushSubData(0, byteCount);
            }

            instanceCount = (uint)Math.Min(instanceAccumulator, uint.MaxValue);
            return outIndex;
        }

        private static bool IsFatalCpuFallbackRejection(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return true;

            if (reason.StartsWith("render-pass-mismatch", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private bool TryPrepareCpuFallbackCommand(GPUScene scene, bool matchAll, uint targetPass, ref DrawMetadata cmd, out string? reason)
        {
            reason = null;

            if (!matchAll && cmd.RenderPass != targetPass && cmd.RenderPass != uint.MaxValue)
            {
                reason = $"render-pass-mismatch (cmd={cmd.RenderPass} expected={targetPass})";
                return false;
            }

            if (cmd.MaterialID == 0u || cmd.MaterialID == uint.MaxValue)
            {
                reason = "material-sentinel";
                return false;
            }

            if (!scene.MaterialMap.ContainsKey(cmd.MaterialID))
            {
                reason = $"material-missing id={cmd.MaterialID}";
                return false;
            }

            if (cmd.MeshID == 0u || cmd.MeshID == uint.MaxValue)
            {
                reason = "mesh-sentinel";
                return false;
            }

            if (!scene.TryGetMeshDataEntry(cmd.MeshID, out GPUScene.MeshDataEntry meshEntry) || meshEntry.IndexCount == 0)
            {
                reason = $"mesh-metadata-missing id={cmd.MeshID}";
                return false;
            }

            if (cmd.InstanceCount == 0u)
            {
                reason = "zero-instances";
                return false;
            }

            return true;
        }

        private void DumpSourceCommandProbe(GPUScene scene, uint copyCount)
        {
            uint requested = Math.Max(IndirectDebug.ProbeSourceCommandCount, 1u);
            uint sampleCount = Math.Min(copyCount, requested);

            if (sampleCount == 0)
                return;

            try
            {
                var sb = new StringBuilder();
                sb.Append("Pre-pass copy probe (target=").Append(RenderPass).Append(" count=").Append(sampleCount).Append("): ");
                for (uint i = 0; i < sampleCount; ++i)
                {
                    if (i > 0)
                        sb.Append(" | ");
                    GetDrawSnapshot(scene, i, out DrawMetadata metadata, out BoundsGpu bounds);
                    sb.Append('#').Append(i).Append(' ').Append(FormatCommandSnapshot(metadata, bounds));
                }

                Debug.Meshes($"{FormatDebugPrefix("Culling")} ProbeSource {sb}");
            }
            catch (Exception ex)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Failed to probe source commands: {ex.Message}");
            }
        }

        private void LogCommandPassSample(GPUScene scene, uint copyCount)
        {
            try
            {
                uint sampleCount = Math.Min(copyCount, 8u);
                if (sampleCount == 0)
                    return;

                var sb = new StringBuilder();
                sb.Append("Cull passthrough sample passes (target=").Append(RenderPass).Append("): ");
                for (uint i = 0; i < sampleCount; ++i)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append('[').Append(i).Append("]=").Append(scene.CullControlBuffer.GetDataRawAtIndex<DrawMetadata>(i).RenderPass);
                }

                Dbg(sb.ToString(), "Culling");
            }
            catch (Exception ex)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Failed to log pass sample: {ex.Message}");
            }
        }

        private struct SoftIssueInfo
        {
            public int Count;
            public uint FirstIndex;
            public DrawMetadata FirstMetadata;
            public BoundsGpu FirstBounds;
        }

        private static void RecordSoftIssue(Dictionary<string, SoftIssueInfo> map, string reason, uint index, in DrawMetadata metadata, in BoundsGpu bounds)
        {
            if (map.TryGetValue(reason, out SoftIssueInfo info))
            {
                info.Count++;
                map[reason] = info;
            }
            else
            {
                map[reason] = new SoftIssueInfo
                {
                    Count = 1,
                    FirstIndex = index,
                    FirstMetadata = metadata,
                    FirstBounds = bounds
                };
            }
        }

        private void CollectSoftIssues(in DrawMetadata metadata, in BoundsGpu bounds, uint index, Dictionary<string, SoftIssueInfo> softIssues)
        {
            if (metadata.InstanceCount == 0)
                RecordSoftIssue(softIssues, "instance-count-zero", index, metadata, bounds);

            if (RenderPass >= 0 && metadata.RenderPass != (uint)RenderPass && metadata.RenderPass != uint.MaxValue)
                RecordSoftIssue(softIssues, "render-pass-mismatch", index, metadata, bounds);
        }

        private bool SanitizeCulledCommands(GPUScene scene)
        {
            if (_culledSceneToRenderBuffer is null || _culledCountBuffer is null)
            {
                ResetVisibleCounters();
                return true;
            }

            uint visible = VisibleCommandCount;
            if (visible == 0u)
            {
                VisibleInstanceCount = 0u;
                return true;
            }

            var invalidCommands = new List<(uint index, DrawMetadata metadata, BoundsGpu bounds, string reason)>();
            var softIssues = new Dictionary<string, SoftIssueInfo>(StringComparer.OrdinalIgnoreCase);
            var missingMaterialIds = new HashSet<uint>();
            uint writeIndex = 0u;
            ulong instanceTotal = 0u;

            for (uint i = 0; i < visible; ++i)
            {
                uint drawId = _culledSceneToRenderBuffer.GetDataRawAtIndex<uint>(i);
                if (!TryGetDrawSnapshot(scene, drawId, out DrawMetadata metadata, out BoundsGpu bounds))
                {
                    invalidCommands.Add((i, default, default, "draw-id-out-of-range"));
                    continue;
                }

                if (IsDebugLoggingEnabledForPass() && _sanitizerSampleLogBudget > 0 && Interlocked.Decrement(ref _sanitizerSampleLogBudget) >= 0)
                    Dbg($"Sanitize sample idx={i} draw={drawId} material={metadata.MaterialID} known={scene.MaterialMap.ContainsKey(metadata.MaterialID)} mesh={metadata.MeshID} pass={metadata.RenderPass} instances={metadata.InstanceCount}", "Materials");

                CollectSoftIssues(metadata, bounds, i, softIssues);
                if (IsCulledCommandValid(scene, metadata, missingMaterialIds, out string? failureReason))
                {
                    _culledSceneToRenderBuffer.SetDataRawAtIndex(writeIndex++, drawId);
                    instanceTotal += metadata.InstanceCount;
                    continue;
                }

                string reason = failureReason ?? "invalid";
                invalidCommands.Add((i, metadata, bounds, reason));
                if (_sanitizerDetailLogBudget > 0 && Interlocked.Decrement(ref _sanitizerDetailLogBudget) >= 0)
                    Debug.MeshesWarning($"{FormatDebugPrefix("Materials")} Sanitize drop idx={i} draw={drawId} reason={reason} {FormatCommandSnapshot(metadata, bounds)}");
            }

            _culledSceneToRenderBuffer.PushSubData(0, writeIndex * _culledSceneToRenderBuffer.ElementSize);
            WriteVisibleCounters(writeIndex, (uint)Math.Min(instanceTotal, uint.MaxValue));

            if (invalidCommands.Count == 0)
            {
                if (softIssues.Count > 0 && _culledSanitizerLogBudget-- > 0)
                    Dbg(BuildSoftIssueSummary(visible, softIssues, RenderPass), "Materials");
                return true;
            }

            if (_culledSanitizerLogBudget-- > 0)
                Dbg(BuildSanitizerSummary(visible, invalidCommands, softIssues, RenderPass), "Materials");
            if (missingMaterialIds.Count > 0)
                LogMaterialSnapshot(scene, missingMaterialIds);
            return true;
        }

        private static bool TryGetDrawSnapshot(GPUScene scene, uint drawId, out DrawMetadata metadata, out BoundsGpu bounds)
        {
            metadata = default;
            bounds = default;
            if (drawId >= scene.CullControlBuffer.ElementCount)
                return false;

            metadata = scene.CullControlBuffer.GetDataRawAtIndex<DrawMetadata>(drawId);
            if (metadata.BoundsID >= scene.CullBoundsBuffer.ElementCount)
                return false;

            bounds = scene.CullBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(metadata.BoundsID);
            return true;
        }

        private static void GetDrawSnapshot(GPUScene scene, uint drawId, out DrawMetadata metadata, out BoundsGpu bounds)
            => _ = TryGetDrawSnapshot(scene, drawId, out metadata, out bounds);

        private static string FormatCommandSnapshot(in DrawMetadata metadata, in BoundsGpu bounds)
            => $"mesh={metadata.MeshID} material={metadata.MaterialID} pass={metadata.RenderPass} instances={metadata.InstanceCount} layer=0x{metadata.LayerMask:X8} center=<{bounds.BoundingSphere.X:F2},{bounds.BoundingSphere.Y:F2},{bounds.BoundingSphere.Z:F2}> radius={bounds.BoundingSphere.W:F2}";

        private void LogZeroVisibilityDiagnostics(GPUScene scene, XRCamera camera, uint inputCount)
        {
            if (_zeroVisibilityDiagnosticLogBudget <= 0)
                return;

            if (Interlocked.Decrement(ref _zeroVisibilityDiagnosticLogBudget) < 0)
                return;

            uint rejectedFrustum = ReadStatCounter(3u);
            uint rejectedDistance = ReadStatCounter(4u);
            uint sampleCount = Math.Min(inputCount, 4u);
            if (sampleCount == 0u)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} Zero-visible diagnostic: no source commands available. rejectedFrustum={rejectedFrustum} rejectedDistance={rejectedDistance} cameraMask=0x{unchecked((uint)camera.CullingMask.Value):X8} farZ={camera.FarZ:F2}");
                return;
            }

            Frustum frustum = camera.WorldFrustum();
            uint cameraMask = unchecked((uint)camera.CullingMask.Value);
            Vector3 cameraPosition = camera.Transform?.RenderTranslation ?? Vector3.Zero;
            float maxDistanceSq = camera.FarZ > 0.0f ? camera.FarZ * camera.FarZ : float.PositiveInfinity;

            var sb = new StringBuilder(512);
            sb.Append($"{FormatDebugPrefix("Culling")} Zero-visible diagnostic: rejectedFrustum={rejectedFrustum} rejectedDistance={rejectedDistance} cameraMask=0x{cameraMask:X8} farZ={camera.FarZ:F2}");
            for (uint i = 0; i < sampleCount; ++i)
            {
                GetDrawSnapshot(scene, i, out DrawMetadata metadata, out BoundsGpu bounds);
                string reason = DescribeCpuFrustumRejectReason(metadata, bounds, frustum, cameraPosition, cameraMask, maxDistanceSq);
                sb.Append(" | #").Append(i).Append(' ').Append(reason).Append(' ').Append(FormatCommandSnapshot(metadata, bounds));
            }

            Debug.MeshesWarning(sb.ToString());
        }

        private uint ReadStatCounter(uint index)
        {
            if (_statsBuffer is null || _statsBuffer.ElementCount <= index)
                return 0u;

            try
            {
                return ReadUIntAt(_statsBuffer, index);
            }
            catch
            {
                return 0u;
            }
        }

        private string DescribeCpuFrustumRejectReason(in DrawMetadata metadata, in BoundsGpu bounds, Frustum frustum, Vector3 cameraPosition, uint cameraMask, float maxDistanceSq)
        {
            if (metadata.InstanceCount == 0u)
                return "reject=instance-count";

            if ((metadata.LayerMask & cameraMask) == 0u)
                return "reject=layer-mask";

            if (RenderPass >= 0 && metadata.RenderPass != (uint)RenderPass && metadata.RenderPass != uint.MaxValue)
                return "reject=render-pass";

            Vector3 center = new(bounds.BoundingSphere.X, bounds.BoundingSphere.Y, bounds.BoundingSphere.Z);
            float radius = bounds.BoundingSphere.W;
            if (float.IsNaN(center.X) || float.IsNaN(center.Y) || float.IsNaN(center.Z) || float.IsNaN(radius))
                return "reject=nan-bounds";

            if (radius < 0.0f || float.IsInfinity(radius))
                return "reject=invalid-radius";

            float distanceSq = Vector3.DistanceSquared(center, cameraPosition);
            if (distanceSq > maxDistanceSq)
                return "reject=distance";

            Sphere sphere = new(center, radius);
            return frustum.ContainsSphere(sphere) == EContainment.Disjoint
                ? "reject=frustum"
                : "candidate=visible";
        }

        private static (uint MeshId, uint MaterialId, uint Pass) BuildVisibilitySignature(in DrawMetadata cmd)
            => (cmd.MeshID, cmd.MaterialID, cmd.RenderPass);

        private List<(uint MeshId, uint MaterialId, uint Pass)> BuildCpuVisibilitySignatures(GPUScene scene, uint copyCount, out uint cpuVisibleCount)
        {
            bool matchAll = RenderPass < 0;
            uint targetPass = unchecked((uint)RenderPass);
            XRDataBuffer src = scene.CullControlBuffer;

            cpuVisibleCount = 0;
            var signatures = new List<(uint MeshId, uint MaterialId, uint Pass)>(Math.Min((int)copyCount, ValidationSignatureLogLimit));

            for (uint i = 0; i < copyCount; ++i)
            {
                DrawMetadata cmd = src.GetDataRawAtIndex<DrawMetadata>(i);
                if (!TryPrepareCpuFallbackCommand(scene, matchAll, targetPass, ref cmd, out _))
                    continue;

                cpuVisibleCount++;
                if (signatures.Count < ValidationSignatureLogLimit)
                    signatures.Add(BuildVisibilitySignature(cmd));
            }

            return signatures;
        }

        private List<(uint MeshId, uint MaterialId, uint Pass)> BuildGpuVisibilitySignatures(GPUScene scene, uint gpuVisibleCount)
        {
            var signatures = new List<(uint MeshId, uint MaterialId, uint Pass)>(Math.Min((int)gpuVisibleCount, ValidationSignatureLogLimit));

            if (_culledSceneToRenderBuffer is null || gpuVisibleCount == 0)
                return signatures;

            uint sampleCount = Math.Min(gpuVisibleCount, (uint)ValidationSignatureLogLimit);
            for (uint i = 0; i < sampleCount; ++i)
            {
                uint drawId = _culledSceneToRenderBuffer.GetDataRawAtIndex<uint>(i);
                if (drawId >= scene.CullControlBuffer.ElementCount)
                    continue;
                DrawMetadata cmd = scene.CullControlBuffer.GetDataRawAtIndex<DrawMetadata>(drawId);
                signatures.Add(BuildVisibilitySignature(cmd));
            }

            return signatures;
        }

        private void RunGpuCpuValidation(GPUScene scene, uint copyCount, uint gpuVisibleCount)
        {
            if (!IsValidationLoggingEnabledForPass())
                return;

            List<(uint MeshId, uint MaterialId, uint Pass)> cpu = BuildCpuVisibilitySignatures(scene, copyCount, out uint cpuVisibleCount);
            List<(uint MeshId, uint MaterialId, uint Pass)> gpu = BuildGpuVisibilitySignatures(scene, gpuVisibleCount);

            if (cpuVisibleCount != gpuVisibleCount)
                Debug.MeshesWarning($"{FormatDebugPrefix("Validation")} GPU/CPU visible count mismatch: gpu={gpuVisibleCount} cpu={cpuVisibleCount} (copyCount={copyCount}, pass={RenderPass})");
            
            var cpuSet = new HashSet<(uint MeshId, uint MaterialId, uint Pass)>(cpu);
            var gpuSet = new HashSet<(uint MeshId, uint MaterialId, uint Pass)>(gpu);

            var missingOnGpu = cpuSet.Except(gpuSet).Take(ValidationSignatureLogLimit).ToList();
            var extraOnGpu = gpuSet.Except(cpuSet).Take(ValidationSignatureLogLimit).ToList();

            bool logDebug = IsDebugLoggingEnabledForPass();

            if (missingOnGpu.Count > 0 && logDebug)
            {
                var sb = new StringBuilder();
                sb.Append("GPU validation missing signatures: ");
                AppendSignatureList(sb, missingOnGpu);
                Dbg(sb.ToString(), "Validation");
            }

            if (extraOnGpu.Count > 0 && logDebug)
            {
                var sb = new StringBuilder();
                sb.Append("GPU validation extra signatures: ");
                AppendSignatureList(sb, extraOnGpu);
                Dbg(sb.ToString(), "Validation");
            }
        }

        private static void AppendSignatureList(StringBuilder sb, IEnumerable<(uint MeshId, uint MaterialId, uint Pass)> signatures)
        {
            bool first = true;
            foreach (var (MeshId, MaterialId, Pass) in signatures)
            {
                if (!first)
                    sb.Append(" | ");
                sb.Append($"mesh={MeshId} mat={MaterialId} pass={Pass}");
                first = false;
            }
        }

        private static bool IsCulledCommandValid(GPUScene scene, in DrawMetadata cmd, ISet<uint> missingMaterialIds, out string? reason)
        {
            if (cmd.MaterialID == 0u || cmd.MaterialID == uint.MaxValue)
            {
                reason = "material-sentinel";
                return false;
            }

            if (!scene.MaterialMap.ContainsKey(cmd.MaterialID))
            {
                reason = "material-missing";
                missingMaterialIds.Add(cmd.MaterialID);
                return false;
            }

            if (cmd.MeshID == 0u || cmd.MeshID == uint.MaxValue)
            {
                reason = "mesh-sentinel";
                return false;
            }

            if (!scene.TryGetMeshDataEntry(cmd.MeshID, out GPUScene.MeshDataEntry entry) || entry.IndexCount == 0)
            {
                reason = "mesh-metadata-missing";
                return false;
            }

            reason = null;
            return true;
        }

        private static string BuildSanitizerSummary(uint originalCount, IReadOnlyCollection<(uint index, DrawMetadata metadata, BoundsGpu bounds, string reason)> invalidCommands, IReadOnlyDictionary<string, SoftIssueInfo> softIssues, int expectedPass)
        {
            var sb = new StringBuilder();
            sb.Append($"SanitizeCulledCommands dropped {invalidCommands.Count} of {originalCount} commands");

            if (invalidCommands.Count > 0)
            {
                var reasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (_, _, _, reason) in invalidCommands)
                {
                    string key = reason;
                    if (reasonCounts.TryGetValue(key, out int existing))
                        reasonCounts[key] = existing + 1;
                    else
                        reasonCounts[key] = 1;
                }

                sb.Append(" | reasons: ");
                sb.Append(string.Join(", ", reasonCounts.Select(kvp => $"{kvp.Key}={kvp.Value}")));

                var first = invalidCommands.First();
                sb.Append($" | first idx={first.index} mesh={first.metadata.MeshID} material={first.metadata.MaterialID} pass={first.metadata.RenderPass}");
                if (expectedPass >= 0)
                    sb.Append($" expectedPass={expectedPass}");
            }

            if (softIssues.Count > 0)
            {
                sb.Append(" | warnings: ");
                sb.Append(BuildSoftIssueDetails(softIssues, expectedPass));
            }

            return sb.ToString();
        }

        private static string BuildSoftIssueSummary(uint originalCount, IReadOnlyDictionary<string, SoftIssueInfo> softIssues, int expectedPass)
        {
            var sb = new StringBuilder();
            sb.Append($"SanitizeCulledCommands retained {originalCount} commands with warnings");
            sb.Append(" | warnings: ");
            sb.Append(BuildSoftIssueDetails(softIssues, expectedPass));
            return sb.ToString();
        }

        private static string BuildSoftIssueDetails(IReadOnlyDictionary<string, SoftIssueInfo> softIssues, int expectedPass)
        {
            if (softIssues.Count == 0)
                return string.Empty;

            var parts = new List<string>(softIssues.Count);
            foreach (var kvp in softIssues)
            {
                string reason = kvp.Key;
                SoftIssueInfo info = kvp.Value;
                string descriptor = reason;

                if (reason.Equals("render-pass-mismatch", StringComparison.OrdinalIgnoreCase))
                {
                    descriptor += $"={info.Count}(first idx={info.FirstIndex} actualPass={info.FirstMetadata.RenderPass}";
                    if (expectedPass >= 0)
                        descriptor += $" expectedPass={expectedPass}";
                    descriptor += $" mesh={info.FirstMetadata.MeshID} material={info.FirstMetadata.MaterialID})";
                }
                else if (reason.Equals("instance-count-zero", StringComparison.OrdinalIgnoreCase))
                {
                    descriptor += $"={info.Count}(first idx={info.FirstIndex} mesh={info.FirstMetadata.MeshID} material={info.FirstMetadata.MaterialID})";
                }
                else
                {
                    descriptor += $"={info.Count}(first idx={info.FirstIndex} mesh={info.FirstMetadata.MeshID} material={info.FirstMetadata.MaterialID})";
                }

                parts.Add(descriptor);
            }

            return string.Join(", ", parts);
        }

        private void LogMaterialSnapshot(GPUScene scene, IReadOnlyCollection<uint> missingMaterialIds)
        {
            if (!IsDebugLoggingEnabledForPass())
                return;

            const long SnapshotCooldownMs = 1_500;
            long now = Environment.TickCount64;

            if (_lastMaterialSnapshotTick >= 0 && now - _lastMaterialSnapshotTick < SnapshotCooldownMs)
                return;

            _lastMaterialSnapshotTick = now;

            var missingPreview = missingMaterialIds
                .OrderBy(id => id)
                .Take(8)
                .Select(id => id.ToString())
                .ToArray();

            var materialMap = scene.MaterialMap;
            var materialSample = materialMap
                .OrderBy(kvp => kvp.Key)
                .Take(12)
                .Select(kvp =>
                {
                    string? name = kvp.Value?.Name;
                    if (string.IsNullOrWhiteSpace(name) && kvp.Value is not null)
                        name = kvp.Value.GetType().Name;
                    return $"{kvp.Key}:{name ?? "<null>"}";
                })
                .ToArray();

            var sb = new StringBuilder();
            sb.Append($"Material snapshot missing={missingMaterialIds.Count}");
            if (missingPreview.Length > 0)
            {
                string previewText = string.Join(", ", missingPreview);
                if (missingMaterialIds.Count > missingPreview.Length)
                    previewText += ", ...";
                sb.Append($" ids=[{previewText}]");
            }

            sb.Append($" mapCount={materialMap.Count}");

            if (materialSample.Length > 0)
                sb.Append($" sample=[{string.Join(", ", materialSample)}]");

            Dbg(sb.ToString(), "Materials");
        }

        public void DebugDraw(XRCamera camera, GPUScene scene)
        {
            Dbg("DebugDraw begin","Stats");

            if (_debugDrawProgram is null || _culledSceneToRenderBuffer is null || _culledCountBuffer is null)
                return;

            uint count = VisibleCommandCount;
            if (count == 0)
                return;

            _debugDrawProgram.Uniform("CurrentRenderPass", RenderPass);
            _debugDrawProgram.Uniform("CameraPosition", camera.Transform.RenderTranslation);
            _debugDrawProgram.Uniform("MaxRenderDistance", camera.FarZ);
            _debugDrawProgram.Uniform("InputCommandCount", (int)count);
            _debugDrawProgram.Uniform("CulledCommandCount", (int)ReadUInt(_culledCountBuffer));

            _debugDrawProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _debugDrawProgram.BindBuffer(scene.CullControlBuffer, 1);
            BindStorageBuffer(_debugDrawProgram, _culledCountBuffer, 2);

            uint numGroups = (count + ComputeWorkGroupSize - 1) / ComputeWorkGroupSize;
            _debugDrawProgram.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"DebugDraw dispatched groups={numGroups} count={count}","Stats");
        }
    }
}
