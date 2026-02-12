using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private const uint ComputeWorkGroupSize = 256;

        // Set true to bypass GPU frustum/flag culling and treat all commands as visible (debug only).
        // Default is OFF; passthrough must be explicitly enabled in debug preferences.
        public bool ForcePassthroughCulling => Engine.EditorPreferences?.Debug?.ForceGpuPassthroughCulling ?? false;

        private enum CullFrameMode
        {
            Passthrough,
            Frustum,
            Bvh
        }

        private int _culledSanitizerLogBudget = 8;
        private int _passthroughFallbackLogBudget = 4;
        private int _cpuFallbackRejectLogBudget = 6;
        private int _cpuFallbackDetailLogBudget = 8;
        private int _sanitizerDetailLogBudget = 4;
        private int _sanitizerSampleLogBudget = 12;
        private int _copyAtomicOverflowLogBudget = 4;
        private int _filteredCountLogBudget = 6;
        private bool _loggedPassthroughCullMode;
        private bool _loggedFrustumCullMode;
        private bool _loggedBvhCullMode;
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
        private unsafe void ReadUints(XRDataBuffer buf, Span<uint> values)
        {
            if (!buf.IsMapped)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} ReadUints failed - buffer not mapped");
                for (int i = 0; i < values.Length; i++)
                    values[i] = 0;
                return;
            }

            Engine.Rendering.Stats.RecordGpuReadbackBytes(values.Length * sizeof(uint));

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            var addr = buf.GetMappedAddresses().FirstOrDefault();
            if (addr == IntPtr.Zero)
                throw new InvalidOperationException("ReadUints failed - buffer mapped address is null");

            uint* ptr = (uint*)addr.Pointer;
            for (int i = 0; i < values.Length; i++)
                values[i] = ptr[i];
        }

        /// <summary>
        /// Writes an array of unsigned integers to the specified data buffer.
        /// </summary>
        /// <remarks>This method writes the provided unsigned integers to the specified buffer.  The
        /// caller is responsible for ensuring that the buffer has sufficient capacity to store the values.</remarks>
        /// <param name="buf">The data buffer to which the unsigned integers will be written.</param>
        /// <param name="values">An array of unsigned integers to write to the buffer. This parameter can be empty.</param>
        private unsafe void WriteUints(XRDataBuffer buf, params uint[] values)
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
        private unsafe void WriteUints(XRDataBuffer buf, ReadOnlySpan<uint> values)
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

            var addr = buf.GetMappedAddresses().FirstOrDefault();
            if (addr == IntPtr.Zero)
                throw new InvalidOperationException("WriteUints failed - buffer mapped address is null");

            uint* ptr = (uint*)addr.Pointer;
            for (int i = 0; i < values.Length; i++)
                ptr[i] = values[i];

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
        private unsafe uint ReadUIntAt(XRDataBuffer buf, uint index)
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
                    Engine.Rendering.Stats.RecordGpuBufferMapped();
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                var addr = buf.GetMappedAddresses().FirstOrDefault();
                if (addr == IntPtr.Zero)
                    throw new InvalidOperationException("ReadUIntAt failed - buffer mapped address is null");
                Engine.Rendering.Stats.RecordGpuReadbackBytes(sizeof(uint));
                return ((uint*)addr.Pointer)[index];
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
        private unsafe void WriteUIntAt(XRDataBuffer buf, uint index, uint value)
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

            var addr = buf.GetMappedAddresses().FirstOrDefault();
            if (addr == IntPtr.Zero)
                throw new InvalidOperationException("WriteUIntAt failed - buffer mapped address is null");

            ((uint*)addr.Pointer)[index] = value;

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
        }

        /// <summary>
        /// Reads an unsigned integer value from the specified GPU-mapped XR data buffer at index 0.
        /// </summary>
        /// <param name="buf">The <see cref="XRDataBuffer"/> from which to read the value. The buffer must be mapped.</param>
        /// <returns>The unsigned integer value read from the buffer. Returns 0 if the buffer is not mapped.</returns>
        /// <exception cref="Exception">Thrown if the buffer is mapped but the mapped address is a null pointer.</exception>
    private unsafe uint ReadUInt(XRDataBuffer buf)
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
                    Engine.Rendering.Stats.RecordGpuBufferMapped();
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                var addr = buf.GetMappedAddresses().FirstOrDefault();
                if (addr == IntPtr.Zero)
                    throw new InvalidOperationException("ReadUInt failed - buffer mapped address is null");
                Engine.Rendering.Stats.RecordGpuReadbackBytes(sizeof(uint));
                return *((uint*)addr.Pointer);
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
        private unsafe void WriteUInt(XRDataBuffer buf, uint value)
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

                var addr = buf.GetMappedAddresses().FirstOrDefault();
                if (addr == IntPtr.Zero)
                    throw new InvalidOperationException("WriteUInt failed - buffer mapped address is null");

                *(uint*)addr.Pointer = value;

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
            }

            if (IndirectDebug.LogCountBufferWrites && Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                string label = buf.AttributeName ?? buf.Target.ToString();
                Debug.Out($"{FormatDebugPrefix("Indirect")} [Indirect/Count] {label} <= {value}");
            }
        }

        private void ResetVisibleCounters()
        {
            VisibleCommandCount = 0;
            VisibleInstanceCount = 0;
            if (_culledCountBuffer is null)
                return;

            WriteUints(_culledCountBuffer, 0u, 0u, 0u);
        }

        private void UpdateVisibleCountersFromBuffer()
        {
            if (IndirectDebug.DisableCpuReadbackCount)
                return;

            if (_culledCountBuffer is null)
            {
                VisibleCommandCount = 0;
                VisibleInstanceCount = 0;
                return;
            }

            uint draws = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            uint instances = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountInstanceIndex);
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

        private void EnsurePassFilterDebugBuffer(uint sampleCount)
        {
            if (sampleCount == 0)
                return;

            uint requiredElements = Math.Max(sampleCount * PassFilterDebugComponentsPerSample, 1u);

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
            }

            for (uint i = 0; i < requiredElements; ++i)
                _passFilterDebugBuffer!.SetDataRawAtIndex(i, 0u);

            uint byteCount = requiredElements * (uint)sizeof(uint);
            _passFilterDebugBuffer!.PushSubData(0, byteCount);
        }

        private unsafe void DumpPassFilterDebug(uint sampleCount)
        {
            if (!Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
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

                VoidPtr mapped = _passFilterDebugBuffer.GetMappedAddresses().FirstOrDefault();
                if (!mapped.IsValid)
                {
                    if (mappedLocally)
                        _passFilterDebugBuffer.UnmapBufferData();
                    Dbg("PassFilterDebug aborted; debug buffer not mapped.", "Culling");
                    return;
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                uint* data = (uint*)mapped.Pointer;
                uint loggedSamples = Math.Min(sampleCount, PassFilterDebugMaxSamples);

                var sb = new StringBuilder();
                sb.Append("PassFilterDebug samples: ");

                for (uint i = 0; i < loggedSamples; ++i)
                {
                    uint baseIndex = i * PassFilterDebugComponentsPerSample;
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

                Dbg(sb.ToString(), "Culling");
            }
            finally
            {
                if (mappedLocally)
                    _passFilterDebugBuffer?.UnmapBufferData();
            }
        }

        public void Cull(GPUScene gpuCommands, XRCamera? camera)
        {
            using var timing = BeginTiming("GPURenderPassCollection.Cull");
            
            LogCullingStart("Cull", gpuCommands.TotalCommandCount);
            Dbg("Cull invoked","Culling");

            // Rebuild internal BVH if dirty (before we try to use it)
            gpuCommands.RebuildBvhIfDirty();

            //Early out if no commands
            uint numCommands = gpuCommands.TotalCommandCount;
            if (numCommands == 0)
            {
                VisibleCommandCount = 0;
                VisibleInstanceCount = 0;
                Dbg("Cull: no commands","Culling");
                Log(LogCategory.Culling, LogLevel.Debug, "Cull: no commands - early exit");
                return;
            }

            // Passthrough path (testing) & copy all input commands to culled buffer and mark all visible
            if (ForcePassthroughCulling)
            {
                LogCullModeActivation(CullFrameMode.Passthrough);
                PassthroughCull(gpuCommands, numCommands);
            }
            else if (ShouldUseBvhCulling(gpuCommands))
            {
                LogCullModeActivation(CullFrameMode.Bvh);
                BvhCull(gpuCommands, camera, numCommands);
            }
            else
            {
                LogCullModeActivation(CullFrameMode.Frustum);
                FrustumCull(gpuCommands, camera, numCommands);
            }

            bool sanitizerOk = true;
            if (VisibleCommandCount > 0)
                sanitizerOk = SanitizeCulledCommands(gpuCommands);

            if (_skipGpuSubmissionThisPass || !sanitizerOk)
            {
                ResetVisibleCounters();

                string reason = _skipGpuSubmissionReason ?? "command corruption detected";
                Warn(LogCategory.Culling, "Skipping GPU submission: {0}", reason);
                return;
            }

            LogCullingResult("Cull", numCommands, VisibleCommandCount, VisibleInstanceCount);

            if (Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                XREngine.Debug.Out($"GPURenderPassCollection.Cull: {numCommands} input commands -> {VisibleCommandCount} visible commands ({VisibleInstanceCount} instances) in CulledSceneToRenderBuffer");
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
            => Engine.Rendering.Stats.RecordGpuCpuFallback(1, (int)Math.Min(recoveredCommands, int.MaxValue));

        /// <summary>
        /// GPU frustum culling mode – performs actual frustum culling on the GPU using the existing culling compute shader.
        /// </summary>
        /// <remarks>
        /// Uses the GPURenderCulling.comp shader to perform per-command frustum sphere tests.
        /// Commands outside the camera frustum are rejected before being appended to the culled buffer.
        /// </remarks>
        private void FrustumCull(GPUScene scene, XRCamera? camera, uint numCommands)
        {
            _skipGpuSubmissionThisPass = false;
            _skipGpuSubmissionReason = null;

            // Fall back to passthrough if we don't have the required resources
            if (CulledSceneToRenderBuffer is null || _cullingComputeShader is null || _culledCountBuffer is null || camera is null)
            {
                Dbg("FrustumCull: missing resources, falling back to passthrough", "Culling");
                PassthroughCull(scene, numCommands);
                return;
            }

            XRDataBuffer src = scene.AllLoadedCommandsBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint inputCount = Math.Min(numCommands, capacity);

            if (inputCount == 0)
            {
                ResetVisibleCounters();
                Dbg("FrustumCull: no commands", "Culling");
                return;
            }

            bool debugLoggingEnabled = Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

            if (IndirectDebug.ProbeSourceCommandsBeforeCopy)
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
            _cullingComputeShader.Uniform("DisabledFlagsMask", 0u);
            _cullingComputeShader.Uniform("CameraPosition", camera.Transform?.WorldTranslation ?? System.Numerics.Vector3.Zero);

            // Bind buffers
            _cullingComputeShader.BindBuffer(src, 0);
            _cullingComputeShader.BindBuffer(dst, 1);
            _cullingComputeShader.BindBuffer(_culledCountBuffer, 2);
            if (_cullingOverflowFlagBuffer is not null)
                _cullingComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 3);
            if (_statsBuffer is not null)
                _cullingComputeShader.BindBuffer(_statsBuffer, 8);

            // Dispatch compute shader
            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(inputCount);
            {
                using var cullTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Cull, inputCount);
                _cullingComputeShader.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            // Check for overflow
            if (_cullingOverflowFlagBuffer is not null)
            {
                uint overflowCount = ReadUInt(_cullingOverflowFlagBuffer);
                if (overflowCount > 0)
                {
                    if (_copyAtomicOverflowLogBudget > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} Frustum cull overflow: {overflowCount} commands exceeded capacity {capacity}.");
                        _copyAtomicOverflowLogBudget--;
                    }
                }
            }

            // Read back visible counts
            UpdateVisibleCountersFromBuffer();
            uint visibleCount = VisibleCommandCount;

            if (debugLoggingEnabled)
            {
                Debug.Out($"{FormatDebugPrefix("Culling")} FrustumCull: {inputCount} input -> {visibleCount} visible ({VisibleInstanceCount} instances)");
            }

            // Handle CPU fallback if GPU produced no results
            bool allowCpuFallback = (Engine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
                || (debugLoggingEnabled && Engine.EffectiveSettings.EnableGpuIndirectCpuFallback);

            if (visibleCount == 0 && RenderPass >= 0 && allowCpuFallback)
            {
                uint cpuRecovered = CpuCopyCommandsForPass(scene, inputCount, commit: true, out uint cpuInstanceCount);
                RecordCpuFallbackUsage(cpuRecovered);
                if (cpuRecovered > 0)
                {
                    visibleCount = cpuRecovered;
                    WriteVisibleCounters(cpuRecovered, cpuInstanceCount);
                    if (_passthroughFallbackLogBudget > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} GPU frustum cull returned 0; CPU fallback restored {cpuRecovered} commands for pass {RenderPass}.");
                        _passthroughFallbackLogBudget--;
                    }
                }
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
        /// Determines whether BVH-accelerated culling should be used based on availability and settings.
        /// </summary>
        private bool ShouldUseBvhCulling(GPUScene scene)
        {
            // Check if BVH culling is enabled in settings
            if (!scene.UseGpuBvh)
                return false;

            // Check if we have the BVH culling shader
            if (_bvhFrustumCullProgram is null)
                return false;

            // Check if the scene has a valid BVH provider
            var provider = scene.BvhProvider;
            if (provider is null || !provider.IsBvhReady)
                return false;

            return true;
        }

        /// <summary>
        /// BVH-accelerated frustum culling mode – traverses the GPU BVH hierarchy to quickly reject
        /// large portions of the scene before testing individual commands.
        /// </summary>
        /// <remarks>
        /// This path uses the bvh_frustum_cull.comp shader which traverses the BVH bottom-up from leaves,
        /// rejecting entire subtrees that fall outside the frustum before testing individual command spheres.
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
            XRDataBuffer? bvhRanges = bvhProvider.BvhRangeBuffer;
            XRDataBuffer? bvhMorton = bvhProvider.BvhMortonBuffer;

            if (bvhNodes is null || bvhRanges is null || bvhMorton is null)
            {
                Dbg("BvhCull: BVH buffers not ready, falling back to FrustumCull", "Culling");
                FrustumCull(scene, camera, numCommands);
                return;
            }

            XRDataBuffer src = scene.AllLoadedCommandsBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint inputCount = Math.Min(numCommands, capacity);

            if (inputCount == 0)
            {
                ResetVisibleCounters();
                Dbg("BvhCull: no commands", "Culling");
                return;
            }

            bool debugLoggingEnabled = Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

            if (IndirectDebug.ProbeSourceCommandsBeforeCopy)
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
            _bvhFrustumCullProgram.Uniform("DisabledFlagsMask", 0u);
            _bvhFrustumCullProgram.Uniform("CameraPosition", camera.Transform?.WorldTranslation ?? Vector3.Zero);
            _bvhFrustumCullProgram.Uniform("StatsEnabled", _statsBuffer is not null ? 1u : 0u);
            _bvhFrustumCullProgram.Uniform("OverflowDebugEnabled", 0u);
            _bvhFrustumCullProgram.Uniform("ENABLE_CPU_GPU_COMPARE", 0u); // OpenGL-compatible uniform (was Vulkan specialization constant)

            // Bind command buffers (same as linear culling)
            _bvhFrustumCullProgram.BindBuffer(src, 0);
            _bvhFrustumCullProgram.BindBuffer(dst, 1);
            _bvhFrustumCullProgram.BindBuffer(_culledCountBuffer, 2);
            if (_cullingOverflowFlagBuffer is not null)
                _bvhFrustumCullProgram.BindBuffer(_cullingOverflowFlagBuffer, 3);

            // Bind BVH buffers
            _bvhFrustumCullProgram.BindBuffer(bvhNodes, 4);
            _bvhFrustumCullProgram.BindBuffer(bvhRanges, 5);
            _bvhFrustumCullProgram.BindBuffer(bvhMorton, 6);

            // Bind optional buffers
            if (_statsBuffer is not null)
                _bvhFrustumCullProgram.BindBuffer(_statsBuffer, 8);

            // Dispatch based on leaf count (each thread processes one leaf)
            // BVH has (N+1)/2 leaves for N nodes
            uint nodeCount = bvhProvider.BvhNodeCount;
            uint leafCount = (nodeCount + 1u) / 2u;
            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(Math.Max(leafCount, 1u));

            {
                using var cullTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Cull, inputCount);
                _bvhFrustumCullProgram.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            // Check for overflow
            if (_cullingOverflowFlagBuffer is not null)
            {
                uint overflowCount = ReadUInt(_cullingOverflowFlagBuffer);
                if (overflowCount > 0)
                {
                    if (_copyAtomicOverflowLogBudget > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} BVH cull overflow: {overflowCount} commands exceeded capacity {capacity}.");
                        _copyAtomicOverflowLogBudget--;
                    }
                }
            }

            // Read back visible counts
            UpdateVisibleCountersFromBuffer();
            uint visibleCount = VisibleCommandCount;

            if (debugLoggingEnabled)
            {
                Debug.Out($"{FormatDebugPrefix("Culling")} BvhCull: {inputCount} input -> {visibleCount} visible ({VisibleInstanceCount} instances) [BVH nodes={nodeCount}, leaves={leafCount}]");
            }

            // Handle CPU fallback if GPU produced no results
            bool allowCpuFallback = (Engine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
                || (debugLoggingEnabled && Engine.EffectiveSettings.EnableGpuIndirectCpuFallback);

            if (visibleCount == 0 && RenderPass >= 0 && allowCpuFallback)
            {
                uint cpuRecovered = CpuCopyCommandsForPass(scene, inputCount, commit: true, out uint cpuInstanceCount);
                RecordCpuFallbackUsage(cpuRecovered);
                if (cpuRecovered > 0)
                {
                    visibleCount = cpuRecovered;
                    WriteVisibleCounters(cpuRecovered, cpuInstanceCount);
                    if (_passthroughFallbackLogBudget > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} GPU BVH cull returned 0; CPU fallback restored {cpuRecovered} commands for pass {RenderPass}.");
                        _passthroughFallbackLogBudget--;
                    }
                }
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
            _skipGpuSubmissionThisPass = false;
            _skipGpuSubmissionReason = null;

            if (CulledSceneToRenderBuffer is null || _copyCommandsProgram is null || _culledCountBuffer is null)
            {
                ResetVisibleCounters();
                return;
            }
            
            //TODO: use compute shader to avoid CPU roundtripping
            XRDataBuffer src = scene.AllLoadedCommandsBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint copyCount = Math.Min(numCommands, capacity);

            if (copyCount == 0)
            {
                ResetVisibleCounters();
                Dbg("Cull passthrough no commands", "Culling");
                return;
            }

            bool debugLoggingEnabled = Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

            if (IndirectDebug.ProbeSourceCommandsBeforeCopy)
                DumpSourceCommandProbe(scene, copyCount);

            // Copy commands
            ResetVisibleCounters();
            if (_cullingOverflowFlagBuffer is not null)
                WriteUInt(_cullingOverflowFlagBuffer, 0u);

            uint debugSamples = debugLoggingEnabled ? Math.Min(copyCount, PassFilterDebugMaxSamples) : 0u;

            if (debugSamples > 0)
            {
                EnsurePassFilterDebugBuffer(debugSamples);
                _copyCommandsProgram.Uniform("DebugEnabled", 1);
                _copyCommandsProgram.Uniform("DebugMaxSamples", (int)debugSamples);
                _copyCommandsProgram.Uniform("DebugInstanceStride", (int)PassFilterDebugComponentsPerSample);
                if (_passFilterDebugBuffer is not null)
                    _copyCommandsProgram.BindBuffer(_passFilterDebugBuffer, 3);
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
            int boundsCheckEnabled = (IndirectDebug.ValidateCopyCommandAtomicBounds && _cullingOverflowFlagBuffer is not null) ? 1 : 0;
            _copyCommandsProgram.Uniform("BoundsCheckEnabled", boundsCheckEnabled);
            _copyCommandsProgram.BindBuffer(src, 0);
            _copyCommandsProgram.BindBuffer(dst, 1);
            _copyCommandsProgram.BindBuffer(_culledCountBuffer, 2);
            if (_cullingOverflowFlagBuffer is not null)
                _copyCommandsProgram.BindBuffer(_cullingOverflowFlagBuffer, 4);

            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(copyCount);
            {
                using var cullTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Cull, copyCount);
                _copyCommandsProgram.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            if (debugSamples > 0)
                DumpPassFilterDebug(debugSamples);

            if (boundsCheckEnabled == 1 && _cullingOverflowFlagBuffer is not null)
            {
                uint overflowMarker = ReadUInt(_cullingOverflowFlagBuffer);
                if (overflowMarker != 0u)
                {
                    uint offendingIndex = overflowMarker - 1u;
                    if (_copyAtomicOverflowLogBudget > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} Copy shader overflow detected at cmd={offendingIndex} (capacity={capacity}, copyCount={copyCount}).");
                        _copyAtomicOverflowLogBudget--;
                    }
                    _skipGpuSubmissionThisPass = true;
                    _skipGpuSubmissionReason ??= $"copy shader overflow detected at cmd={offendingIndex}";
                }
            }

            UpdateVisibleCountersFromBuffer();
            uint filteredCount = VisibleCommandCount;
            if (_filteredCountLogBudget > 0)
            {
                Debug.Out($"{FormatDebugPrefix("Culling")} Copy shader reported filteredCount={filteredCount} (copyCount={copyCount})");
                _filteredCountLogBudget--;
            }
            // Check EditorPreferences first for debugging, fall back to EffectiveSettings for production
            bool allowCpuFallback = (Engine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
                || (debugLoggingEnabled && Engine.EffectiveSettings.EnableGpuIndirectCpuFallback);
            if (!allowCpuFallback && debugLoggingEnabled)
            {
                // allowCpuFallback = true;
            }

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
                            Debug.LogWarning($"{FormatDebugPrefix("Culling")} GPU pass filter returned 0; CPU fallback restored {cpuRecovered} commands for pass {RenderPass}.");
                            _passthroughFallbackLogBudget--;
                        }
                        Dbg($"Cull passthrough GPU produced 0; CPU fallback restored {cpuRecovered} commands", "Culling");
                    }
                }
                else
                {
                    if (_passthroughFallbackLogBudget > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} GPU pass filter returned 0 for pass {RenderPass}; CPU fallback disabled (set EditorPreferences.Debug.AllowGpuCpuFallback to true to allow recovery).");
                        _passthroughFallbackLogBudget--;
                    }
                    if (Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
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

            XRDataBuffer src = scene.AllLoadedCommandsBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint elementSize = dst.ElementSize;
            if (elementSize == 0)
                elementSize = GPUScene.CommandFloatCount * sizeof(float);

            uint outIndex = 0;
            uint rejected = 0;
            uint fatalRejected = 0;
            ulong instanceAccumulator = 0;
            string? firstFatalRejection = null;
            for (uint i = 0; i < copyCount; ++i)
            {
                GPUIndirectRenderCommand cmd = src.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                if (!TryPrepareCpuFallbackCommand(scene, matchAll, targetPass, ref cmd, out string? rejectionReason))
                {
                    rejected++;
                    bool isFatal = IsFatalCpuFallbackRejection(rejectionReason);
                    if (isFatal)
                    {
                        fatalRejected++;
                        if (rejectionReason is not null && _cpuFallbackDetailLogBudget > 0 && Interlocked.Decrement(ref _cpuFallbackDetailLogBudget) >= 0)
                            Debug.LogWarning($"{FormatDebugPrefix("Culling")} CPU fallback reject idx={i} reason={rejectionReason} {FormatCommandSnapshot(cmd)}");

                        if (firstFatalRejection is null)
                        {
                            string commandSummary = FormatCommandSnapshot(cmd);
                            firstFatalRejection = $"idx={i} reason={rejectionReason ?? "unknown"} {commandSummary}";
                        }
                    }
                    continue;
                }

                if (commit)
                    dst.SetDataRawAtIndex(outIndex, cmd);
                outIndex++;
                instanceAccumulator += cmd.InstanceCount;
            }

            if (fatalRejected > 0)
            {
                if (_cpuFallbackRejectLogBudget > 0)
                {
                    Debug.LogWarning($"{FormatDebugPrefix("Culling")} CPU fallback rejected {fatalRejected} commands for pass {RenderPass} due to invalid metadata.");
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

        private bool TryPrepareCpuFallbackCommand(GPUScene scene, bool matchAll, uint targetPass, ref GPUIndirectRenderCommand cmd, out string? reason)
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
                cmd.InstanceCount = 1u;

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
                GPUIndirectRenderCommand[] sample = scene.AllLoadedCommandsBuffer.GetDataArrayRawAtIndex<GPUIndirectRenderCommand>(0, (int)sampleCount);
                var sb = new StringBuilder();
                sb.Append("Pre-pass copy probe (target=").Append(RenderPass).Append(" count=").Append(sampleCount).Append("): ");
                for (int i = 0; i < sample.Length; i++)
                {
                    if (i > 0)
                        sb.Append(" | ");
                    sb.Append('#').Append(i).Append(' ').Append(FormatCommandSnapshot(sample[i]));
                }

                Debug.Out($"{FormatDebugPrefix("Culling")} ProbeSource {sb}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Culling")} Failed to probe source commands: {ex.Message}");
            }
        }

        private void LogCommandPassSample(GPUScene scene, uint copyCount)
        {
            try
            {
                uint sampleCount = Math.Min(copyCount, 8u);
                if (sampleCount == 0)
                    return;

                GPUIndirectRenderCommand[] sample = scene.AllLoadedCommandsBuffer.GetDataArrayRawAtIndex<GPUIndirectRenderCommand>(0, (int)sampleCount);
                var sb = new StringBuilder();
                sb.Append("Cull passthrough sample passes (target=").Append(RenderPass).Append("): ");
                for (int i = 0; i < sample.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append('[').Append(i).Append("]=").Append(sample[i].RenderPass);
                }

                Dbg(sb.ToString(), "Culling");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Culling")} Failed to log pass sample: {ex.Message}");
            }
        }

        private void ExtractSoA(GPUScene scene)
        {
            Dbg("ExtractSoA begin", "SoA");

            if (_extractSoAComputeShader is null)
                return;

            uint count = scene.TotalCommandCount;
            if (count == 0)
                return;

            EnsureSoABuffers(scene.AllocatedMaxCommandCount);

            var spheres = _useBufferAForRender
                ? _soaBoundingSpheresA
                : _soaBoundingSpheresB;

            var meta = _useBufferAForRender
                ? _soaMetadataA
                : _soaMetadataB;

            if (spheres is null || meta is null)
            {
                Debug.LogWarning($"{FormatDebugPrefix("SoA")} SoA extraction buffers not available");
                return;
            }

            _extractSoAComputeShader.Uniform("InputCommandCount", (int)count);
            _extractSoAComputeShader.BindBuffer(scene.AllLoadedCommandsBuffer, 0);
            _extractSoAComputeShader.BindBuffer(spheres, 1);
            _extractSoAComputeShader.BindBuffer(meta, 2);

            uint groups = (count + ComputeWorkGroupSize - 1) / ComputeWorkGroupSize;
            _extractSoAComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

            Dbg($"ExtractSoA dispatched groups={groups} count={count}", "SoA");
        }

        private struct SoftIssueInfo
        {
            public int Count;
            public uint FirstIndex;
            public GPUIndirectRenderCommand FirstCommand;
        }

        private static void RecordSoftIssue(Dictionary<string, SoftIssueInfo> map, string reason, uint index, in GPUIndirectRenderCommand cmd)
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
                    FirstCommand = cmd
                };
            }
        }

        private void CollectSoftIssues(in GPUIndirectRenderCommand cmd, uint index, Dictionary<string, SoftIssueInfo> softIssues)
        {
            if (cmd.InstanceCount == 0)
                RecordSoftIssue(softIssues, "instance-count-zero", index, cmd);

            if (RenderPass >= 0 && cmd.RenderPass != (uint)RenderPass && cmd.RenderPass != uint.MaxValue)
                RecordSoftIssue(softIssues, "render-pass-mismatch", index, cmd);
        }

        private unsafe bool SanitizeCulledCommands(GPUScene scene)
        {
            if (_culledSceneToRenderBuffer is null || _culledCountBuffer is null)
            {
                ResetVisibleCounters();
                return true;
            }

            uint visible = VisibleCommandCount;
            if (visible == 0)
            {
                VisibleInstanceCount = 0;
                return true;
            }

            var invalidCommands = new List<(uint index, GPUIndirectRenderCommand command, string reason)>();
            var softIssues = new Dictionary<string, SoftIssueInfo>(StringComparer.OrdinalIgnoreCase);
            var missingMaterialIds = new HashSet<uint>();
            uint writeIndex = 0u;
            ulong instanceTotal = 0u;

            bool mappedLocally = false;
            VoidPtr mappedPtr = default;
            try
            {
                if (_culledSceneToRenderBuffer.ActivelyMapping.Count == 0)
                {
                    _culledSceneToRenderBuffer.StorageFlags |= EBufferMapStorageFlags.Read;
                    _culledSceneToRenderBuffer.RangeFlags |= EBufferMapRangeFlags.Read;
                    _culledSceneToRenderBuffer.MapBufferData();
                    mappedLocally = true;
                }

                mappedPtr = _culledSceneToRenderBuffer.GetMappedAddresses().FirstOrDefault();
                if (!mappedPtr.IsValid)
                {
                    if (mappedLocally)
                        _culledSceneToRenderBuffer.UnmapBufferData();
                    Dbg("SanitizeCulledCommands aborted; culled buffer not mapped for read.", "Materials");
                    return true;
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);

                byte* basePtr = (byte*)mappedPtr.Pointer;
                uint elementSize = _culledSceneToRenderBuffer.ElementSize;
                if (elementSize == 0)
                    elementSize = GPUScene.CommandFloatCount * sizeof(float);

                for (uint i = 0; i < visible; ++i)
                {
                    byte* src = basePtr + (i * elementSize);
                    GPUIndirectRenderCommand cmd = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(src);

                    if ((Engine.EffectiveSettings.EnableGpuIndirectDebugLogging) && _sanitizerSampleLogBudget > 0)
                    {
                        if (Interlocked.Decrement(ref _sanitizerSampleLogBudget) >= 0)
                        {
                            bool materialKnown = scene.MaterialMap.ContainsKey(cmd.MaterialID);
                            string sampleInfo = 
                                $"Sanitize sample idx={i} material={cmd.MaterialID} known={materialKnown} mesh={cmd.MeshID} pass={cmd.RenderPass} instances={cmd.InstanceCount}";
                            Dbg(sampleInfo, "Materials");
                        }
                    }

                    CollectSoftIssues(cmd, i, softIssues);

                    if (IsCulledCommandValid(scene, cmd, missingMaterialIds, out string? failureReason))
                    {
                        _culledSceneToRenderBuffer.SetDataRawAtIndex(writeIndex, cmd);
                        writeIndex++;
                        instanceTotal += cmd.InstanceCount;
                    }
                    else
                    {
                        string reason = failureReason ?? "invalid";
                        invalidCommands.Add((i, cmd, reason));
                        if (_sanitizerDetailLogBudget > 0 && Interlocked.Decrement(ref _sanitizerDetailLogBudget) >= 0)
                            Debug.LogWarning($"{FormatDebugPrefix("Materials")} Sanitize drop idx={i} reason={reason} {FormatCommandSnapshot(cmd)}");
                    }
                }

                bool hasSoftIssues = softIssues.Count > 0;

                uint newVisible = writeIndex;
                uint instanceCount = (uint)Math.Min(instanceTotal, uint.MaxValue);
                WriteVisibleCounters(newVisible, instanceCount);

                if (invalidCommands.Count == 0)
                {
                    if (hasSoftIssues && _culledSanitizerLogBudget > 0)
                    {
                        string softSummary = BuildSoftIssueSummary(visible, softIssues, RenderPass);
                        Dbg(softSummary, "Materials");
                        _culledSanitizerLogBudget--;
                    }
                    return true;
                }

                if (_culledSanitizerLogBudget > 0)
                {
                    string summary = BuildSanitizerSummary(visible, invalidCommands, softIssues, RenderPass);
                    Dbg(summary, "Materials");
                    _culledSanitizerLogBudget--;
                }

                if (missingMaterialIds.Count > 0)
                    LogMaterialSnapshot(scene, missingMaterialIds);

                // Even if we dropped some commands, the sanitization itself was successful.
                // The remaining commands in the buffer are valid and ready to be rendered.
                return true;
            }
            finally
            {
                if (mappedLocally)
                    _culledSceneToRenderBuffer.UnmapBufferData();
            }
        }

        private static string FormatCommandSnapshot(in GPUIndirectRenderCommand cmd)
            => $"mesh={cmd.MeshID} material={cmd.MaterialID} pass={cmd.RenderPass} instances={cmd.InstanceCount}";

        private static (uint MeshId, uint MaterialId, uint Pass) BuildVisibilitySignature(in GPUIndirectRenderCommand cmd)
            => (cmd.MeshID, cmd.MaterialID, cmd.RenderPass);

        private List<(uint MeshId, uint MaterialId, uint Pass)> BuildCpuVisibilitySignatures(GPUScene scene, uint copyCount, out uint cpuVisibleCount)
        {
            bool matchAll = RenderPass < 0;
            uint targetPass = unchecked((uint)RenderPass);
            XRDataBuffer src = scene.AllLoadedCommandsBuffer;

            cpuVisibleCount = 0;
            var signatures = new List<(uint MeshId, uint MaterialId, uint Pass)>(Math.Min((int)copyCount, ValidationSignatureLogLimit));

            for (uint i = 0; i < copyCount; ++i)
            {
                GPUIndirectRenderCommand cmd = src.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                if (!TryPrepareCpuFallbackCommand(scene, matchAll, targetPass, ref cmd, out _))
                    continue;

                cpuVisibleCount++;
                if (signatures.Count < ValidationSignatureLogLimit)
                    signatures.Add(BuildVisibilitySignature(cmd));
            }

            return signatures;
        }

        private List<(uint MeshId, uint MaterialId, uint Pass)> BuildGpuVisibilitySignatures(uint gpuVisibleCount)
        {
            var signatures = new List<(uint MeshId, uint MaterialId, uint Pass)>(Math.Min((int)gpuVisibleCount, ValidationSignatureLogLimit));

            if (_culledSceneToRenderBuffer is null || gpuVisibleCount == 0)
                return signatures;

            uint sampleCount = Math.Min(gpuVisibleCount, (uint)ValidationSignatureLogLimit);
            for (uint i = 0; i < sampleCount; ++i)
            {
                GPUIndirectRenderCommand cmd = _culledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                signatures.Add(BuildVisibilitySignature(cmd));
            }

            return signatures;
        }

        private void RunGpuCpuValidation(GPUScene scene, uint copyCount, uint gpuVisibleCount)
        {
            if (!Engine.EffectiveSettings.EnableGpuIndirectValidationLogging)
                return;

            List<(uint MeshId, uint MaterialId, uint Pass)> cpu = BuildCpuVisibilitySignatures(scene, copyCount, out uint cpuVisibleCount);
            List<(uint MeshId, uint MaterialId, uint Pass)> gpu = BuildGpuVisibilitySignatures(gpuVisibleCount);

            if (cpuVisibleCount != gpuVisibleCount)
                Debug.LogWarning($"{FormatDebugPrefix("Validation")} GPU/CPU visible count mismatch: gpu={gpuVisibleCount} cpu={cpuVisibleCount} (copyCount={copyCount}, pass={RenderPass})");
            
            var cpuSet = new HashSet<(uint MeshId, uint MaterialId, uint Pass)>(cpu);
            var gpuSet = new HashSet<(uint MeshId, uint MaterialId, uint Pass)>(gpu);

            var missingOnGpu = cpuSet.Except(gpuSet).Take(ValidationSignatureLogLimit).ToList();
            var extraOnGpu = gpuSet.Except(cpuSet).Take(ValidationSignatureLogLimit).ToList();

            bool logDebug = Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

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

        private static bool IsCulledCommandValid(GPUScene scene, in GPUIndirectRenderCommand cmd, ISet<uint> missingMaterialIds, out string? reason)
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

        private static string BuildSanitizerSummary(uint originalCount, IReadOnlyCollection<(uint index, GPUIndirectRenderCommand command, string reason)> invalidCommands, IReadOnlyDictionary<string, SoftIssueInfo> softIssues, int expectedPass)
        {
            var sb = new StringBuilder();
            sb.Append($"SanitizeCulledCommands dropped {invalidCommands.Count} of {originalCount} commands");

            if (invalidCommands.Count > 0)
            {
                var reasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (index, command, reason) in invalidCommands)
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
                sb.Append($" | first idx={first.index} mesh={first.command.MeshID} material={first.command.MaterialID} pass={first.command.RenderPass}");
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
                    descriptor += $"={info.Count}(first idx={info.FirstIndex} actualPass={info.FirstCommand.RenderPass}";
                    if (expectedPass >= 0)
                        descriptor += $" expectedPass={expectedPass}";
                    descriptor += $" mesh={info.FirstCommand.MeshID} material={info.FirstCommand.MaterialID})";
                }
                else if (reason.Equals("instance-count-zero", StringComparison.OrdinalIgnoreCase))
                {
                    descriptor += $"={info.Count}(first idx={info.FirstIndex} mesh={info.FirstCommand.MeshID} material={info.FirstCommand.MaterialID})";
                }
                else
                {
                    descriptor += $"={info.Count}(first idx={info.FirstIndex} mesh={info.FirstCommand.MeshID} material={info.FirstCommand.MaterialID})";
                }

                parts.Add(descriptor);
            }

            return string.Join(", ", parts);
        }

        private void LogMaterialSnapshot(GPUScene scene, IReadOnlyCollection<uint> missingMaterialIds)
        {
            if (!(Engine.EffectiveSettings.EnableGpuIndirectDebugLogging))
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

        //public void SetHiZDepthPyramid(XRTexture? tex, int maxMip) { _hiZDepthPyramid = tex; HiZMaxMip = maxMip; }

        private void SoACull(XRCamera camera, GPUScene scene)
        {
            Dbg("SoACull begin","SoA");

            if (_culledCountBuffer == null)
                return;

            uint count = scene.TotalCommandCount;
            if (count == 0)
                return;

            EnsureIndexList(count);

            if (_soaIndexList is null)
            {
                Dbg("SoACull missing index list", "SoA");
                return;
            }

            var spheres = _useBufferAForRender
                ? _soaBoundingSpheresA
                : _soaBoundingSpheresB;

            var meta = _useBufferAForRender
                ? _soaMetadataA
                : _soaMetadataB;

            if (spheres is null || meta is null)
                return;

            _soaIndexList.SetDataRawAtIndex(0, 0u);
            _soaIndexList.PushSubData();

            var shader = 
                //UseHiZ
            //    ? HiZSoACullingComputeShader
            //    : 
                _soACullingComputeShader;

            if (shader is null)
                return;

            shader.Uniform("CameraPosition", camera.Transform.WorldTranslation);
            shader.Uniform("MaxRenderDistance", camera.FarZ * camera.FarZ);
            shader.Uniform("CameraLayerMask", unchecked((uint)camera.CullingMask.Value));
            shader.Uniform("CurrentRenderPass", RenderPass);
            shader.Uniform("DisabledFlagsMask", 0u);
            shader.Uniform("InputCommandCount", (int)count);

            var planes = camera.WorldFrustum().Planes.Select(x => x.AsVector4()).ToArray();
            if (planes.Length >= 6)
                shader.Uniform("FrustumPlanes", planes);

            //if (UseHiZ)
            //{
            //    shader.Uniform("HiZMaxMip", HiZMaxMip);
            //    if (_hiZDepthPyramid != null)
            //        shader.Sampler(_hiZDepthPyramid.Name ?? "HiZDepthPyramid", _hiZDepthPyramid, 0);
            //}

            shader.BindBuffer(spheres, 0);
            shader.BindBuffer(meta, 1);
            shader.BindBuffer(_soaIndexList, 2);
            shader.BindBuffer(_culledCountBuffer, 3);

            if (_cullingOverflowFlagBuffer != null)
                shader.BindBuffer(_cullingOverflowFlagBuffer, 4);

            if (_statsBuffer != null)
                shader.BindBuffer(_statsBuffer, 8);

            uint groups = (count + ComputeWorkGroupSize - 1) / ComputeWorkGroupSize;
            shader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

            UpdateVisibleCountersFromBuffer();

            Dbg($"SoACull visible={VisibleCommandCount} instances={VisibleInstanceCount}","SoA");
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
            _debugDrawProgram.Uniform("CameraPosition", camera.Transform.WorldTranslation);
            _debugDrawProgram.Uniform("MaxRenderDistance", camera.FarZ);
            _debugDrawProgram.Uniform("InputCommandCount", (int)count);
            _debugDrawProgram.Uniform("CulledCommandCount", (int)ReadUInt(_culledCountBuffer));

            _debugDrawProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _debugDrawProgram.BindBuffer(scene.AllLoadedCommandsBuffer, 1);
            _debugDrawProgram.BindBuffer(_culledCountBuffer, 2);

            uint numGroups = (count + ComputeWorkGroupSize - 1) / ComputeWorkGroupSize;
            _debugDrawProgram.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"DebugDraw dispatched groups={numGroups} count={count}","Stats");
        }
    }
}
