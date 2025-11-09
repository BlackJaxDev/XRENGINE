using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        // Set true to bypass GPU frustum/flag culling and treat all commands as visible (debug only)
        public bool ForcePassthroughCulling { get; set; } = true;

        private int _culledSanitizerLogBudget = 8;
        private int _passthroughFallbackLogBudget = 4;
        private int _passthroughFallbackForceLogBudget = 2;
        private int _cpuFallbackRejectLogBudget = 6;
        private int _cpuFallbackDetailLogBudget = 8;
    private int _sanitizerDetailLogBudget = 4;
    private int _sanitizerSampleLogBudget = 12;
        private bool _skipGpuSubmissionThisPass;
        private string? _skipGpuSubmissionReason;
        private long _lastMaterialSnapshotTick = -1;

        private const uint PassFilterDebugComponentsPerSample = 4;
        private const uint PassFilterDebugMaxSamples = 32;

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
            if (buf.APIWrappers.FirstOrDefault() is GLDataBuffer firstApiWrapper && !firstApiWrapper.IsMapped)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} ReadUints failed - buffer not mapped");
                for (int i = 0; i < values.Length; i++)
                    values[i] = 0;
                return;
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            var addr = buf.GetMappedAddresses().FirstOrDefault();
            if (addr == IntPtr.Zero)
                throw new Exception("ReadUints failed - null pointer");

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
            var glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
            bool isMapped = glWrapper?.IsMapped ?? false;

            if (!isMapped)
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
                throw new Exception("WriteUints failed - null pointer");

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
            var glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
            bool isMapped = glWrapper?.IsMapped ?? false;
            bool mappedTemporarily = false;

            try
            {
                if (!isMapped)
                {
                    buf.MapBufferData();
                    glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
                    isMapped = glWrapper?.IsMapped ?? false;
                    if (!isMapped)
                        return buf.GetDataRawAtIndex<uint>(index);
                    mappedTemporarily = true;
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                var addr = buf.GetMappedAddresses().FirstOrDefault();
                if (addr == IntPtr.Zero)
                    throw new Exception("ReadUIntAt failed - null pointer");

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
            var glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
            bool isMapped = glWrapper?.IsMapped ?? false;

            if (!isMapped)
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
                throw new Exception("WriteUIntAt failed - null pointer");

            ((uint*)addr.Pointer)[index] = value;

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
            var glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
            bool isMapped = glWrapper?.IsMapped ?? false;
            bool mappedTemporarily = false;

            try
            {
                if (!isMapped)
                {
                    buf.MapBufferData();
                    glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
                    isMapped = glWrapper?.IsMapped ?? false;
                    if (!isMapped)
                        return buf.GetDataRawAtIndex<uint>(0);
                    mappedTemporarily = true;
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

                var addr = buf.GetMappedAddresses().FirstOrDefault();
                if (addr == IntPtr.Zero)
                    throw new Exception("ReadUInt failed - null pointer");

                return addr.UInt;
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
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private uint WriteUInt(XRDataBuffer buf, uint value)
        {
            var glWrapper = buf.APIWrappers.FirstOrDefault() as GLDataBuffer;
            bool isMapped = glWrapper?.IsMapped ?? false;

            if (!isMapped)
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
                    throw new Exception("WriteUInt failed - null pointer");
                addr.UInt = value;

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
            }

            if (IndirectDebug.LogCountBufferWrites && (Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false))
            {
                string label = buf.AttributeName ?? buf.Target.ToString();
                Debug.Out($"{FormatDebugPrefix("Indirect")} [Indirect/Count] {label} <= {value}");
            }

            return value;
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
            if (! (Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false))
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
            Dbg("Cull invoked","Culling");

            //Early out if no commands
            uint numCommands = gpuCommands.TotalCommandCount;
            if (numCommands == 0)
            {
                VisibleCommandCount = 0;
                Dbg("Cull: no commands","Culling");
                return;
            }

            // Passthrough path (testing) & copy all input commands to culled buffer and mark all visible
            //if (ForcePassthroughCulling)
                PassthroughCull(gpuCommands, numCommands);
            //else
            //    Cull(gpuCommands, camera, numCommands);

            bool sanitizerOk = true;
            if (VisibleCommandCount > 0)
                sanitizerOk = SanitizeCulledCommands(gpuCommands);

            if (_skipGpuSubmissionThisPass || !sanitizerOk)
            {
                if (_culledCountBuffer is not null)
                    WriteUInt(_culledCountBuffer, 0u);

                VisibleCommandCount = 0;

                string reason = _skipGpuSubmissionReason ?? "command corruption detected";
                Debug.LogWarning($"{FormatDebugPrefix("Culling")} Skipping GPU submission: {reason}");
                return;
            }

            if (Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false)
                Debug.Out($"GPURenderPassCollection.Cull: {numCommands} input commands -> {VisibleCommandCount} visible commands in CulledSceneToRenderBuffer");
        }

        //private void Cull(GPUScene gpuCommands, XRCamera? camera, uint numCommands)
        //{
        //    if (_cullingComputeShader is null || camera is null)
        //        return;
            
        //    var planes = camera.WorldFrustum().Planes.Select(x => x.AsVector4()).ToArray();
        //    if (planes.Length >= 6)
        //        _cullingComputeShader.Uniform("FrustumPlanes", planes);
        //    _cullingComputeShader.Uniform("MaxRenderDistance", camera.FarZ);
        //    _cullingComputeShader.Uniform("CameraLayerMask", unchecked((uint)0xFFFFFFFF));
        //    _cullingComputeShader.Uniform("CurrentRenderPass", RenderPass);
        //    _cullingComputeShader.Uniform("InputCommandCount", (int)numCommands);
        //    _cullingComputeShader.Uniform("MaxCulledCommands", (int)CulledSceneToRenderBuffer.ElementCount);
        //    _cullingComputeShader.Uniform("DisabledFlagsMask", 0u);
        //    _cullingComputeShader.Uniform("CameraPosition", camera.Transform.WorldTranslation);

        //    _cullingComputeShader.BindBuffer(gpuCommands.CommandsInputBuffer, 0);
        //    _cullingComputeShader.BindBuffer(CulledSceneToRenderBuffer, 1);
        //    if (_culledCountBuffer is not null)
        //        _cullingComputeShader.BindBuffer(_culledCountBuffer, 2);
        //    if (_cullingOverflowFlagBuffer is not null)
        //        _cullingComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 3);
        //    if (_statsBuffer != null)
        //        _cullingComputeShader.BindBuffer(_statsBuffer, 8);

        //    (uint x, uint y, uint z) = ComputeDispatch.ForCommands(numCommands);
        //    _cullingComputeShader.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        //    Dbg($"Cull dispatched groups={x} cmds={numCommands}", "Culling");

        //    if (_culledCountBuffer is not null)
        //    {
        //        try
        //        {
        //            VisibleCommandCount = ReadUInt(_culledCountBuffer); // existing helper
        //            Dbg($"Cull visible={VisibleCommandCount}", "Culling");
        //        }
        //        catch
        //        {
        //            VisibleCommandCount = CulledSceneToRenderBuffer.ElementCount;
        //            Dbg("Cull readback failed - fallback", "Culling");
        //        }
        //    }
        //    else
        //        VisibleCommandCount = CulledSceneToRenderBuffer.ElementCount;
        //}

        /// <summary>
        /// Culling passthrough mode � copy all input commands to culled buffer and mark all visible.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="numCommands"></param>
        private void PassthroughCull(GPUScene scene, uint numCommands)
        {
            _skipGpuSubmissionThisPass = false;
            _skipGpuSubmissionReason = null;

            if (CulledSceneToRenderBuffer is null || _copyCommandsProgram is null || _culledCountBuffer is null)
                return;
            
            //TODO: use compute shader to avoid CPU roundtripping
            XRDataBuffer src = scene.AllLoadedCommandsBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint copyCount = Math.Min(numCommands, capacity);

            if (copyCount == 0)
            {
                VisibleCommandCount = 0;
                if (_culledCountBuffer is not null)
                    WriteUInt(_culledCountBuffer, 0u);
                Dbg("Cull passthrough no commands", "Culling");
                return;
            }

            //Copy commands
            WriteUInt(_culledCountBuffer, 0u);

            bool debugLoggingEnabled = Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false;
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
            _copyCommandsProgram?.BindBuffer(src, 0);
            _copyCommandsProgram?.BindBuffer(dst, 1);
            _copyCommandsProgram?.BindBuffer(_culledCountBuffer, 2);

            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(copyCount);
            _copyCommandsProgram?.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            if (debugSamples > 0)
                DumpPassFilterDebug(debugSamples);

            uint filteredCount = ReadUInt(_culledCountBuffer);
            bool allowCpuFallback = Engine.UserSettings?.EnableGpuIndirectCpuFallback ?? false;
            if (!allowCpuFallback && debugLoggingEnabled)
            {
                allowCpuFallback = true;
                if (_passthroughFallbackForceLogBudget > 0)
                {
                    Debug.LogWarning($"{FormatDebugPrefix("Culling")} Forcing CPU fallback while GPU indirect debug logging is active (pass {RenderPass}).");
                    _passthroughFallbackForceLogBudget--;
                }
            }

            if (filteredCount == 0 && RenderPass >= 0)
            {
                if (allowCpuFallback)
                {
                    uint cpuRecovered = CpuCopyCommandsForPass(scene, copyCount, commit: true);
                    if (cpuRecovered > 0)
                    {
                        filteredCount = cpuRecovered;
                        WriteUInt(_culledCountBuffer, cpuRecovered);
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
                        Debug.LogWarning($"{FormatDebugPrefix("Culling")} GPU pass filter returned 0 for pass {RenderPass}; CPU fallback disabled (set UserSettings.EnableGpuIndirectCpuFallback to true to allow recovery).");
                        _passthroughFallbackLogBudget--;
                    }
                    if (Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false)
                        LogCommandPassSample(scene, copyCount);
                }
            }

            VisibleCommandCount = Math.Min(filteredCount, copyCount);
            Dbg($"Cull passthrough visible={VisibleCommandCount} (input={copyCount})", "Culling");
        }

        private uint CpuCopyCommandsForPass(GPUScene scene, uint copyCount, bool commit)
        {
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

            uint groupSize = 256;
            uint groups = (count + groupSize - 1) / groupSize;
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
                return true;

            uint visible = VisibleCommandCount;
            if (visible == 0)
                return true;

            var invalidCommands = new List<(uint index, GPUIndirectRenderCommand command, string reason)>();
            var softIssues = new Dictionary<string, SoftIssueInfo>(StringComparer.OrdinalIgnoreCase);
            var missingMaterialIds = new HashSet<uint>();
            uint writeIndex = 0u;

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

                    if ((Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false) && _sanitizerSampleLogBudget > 0)
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

                for (uint destIndex = writeIndex; destIndex < visible; ++destIndex)
                    _culledSceneToRenderBuffer.SetDataRawAtIndex(destIndex, default(GPUIndirectRenderCommand));

                uint bytes = visible * (_culledSceneToRenderBuffer.ElementSize == 0
                    ? GPUScene.CommandFloatCount * sizeof(float)
                    : _culledSceneToRenderBuffer.ElementSize);
                _culledSceneToRenderBuffer.PushSubData(0, bytes);

                uint newVisible = writeIndex;
                VisibleCommandCount = newVisible;
                WriteUInt(_culledCountBuffer, newVisible);

                if (_culledSanitizerLogBudget > 0)
                {
                    string summary = BuildSanitizerSummary(visible, invalidCommands, softIssues, RenderPass);
                    Dbg(summary, "Materials");
                    _culledSanitizerLogBudget--;
                }

                if (missingMaterialIds.Count > 0)
                    LogMaterialSnapshot(scene, missingMaterialIds);

                string dropSummary = BuildSanitizerSummary(visible, invalidCommands, softIssues, RenderPass);
                if (Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false)
                {
                    Dbg($"MaterialMap count={scene.MaterialMap.Count}", "Materials");
                }
                _skipGpuSubmissionThisPass = true;
                _skipGpuSubmissionReason = dropSummary;

                return false;
            }
            finally
            {
                if (mappedLocally)
                    _culledSceneToRenderBuffer.UnmapBufferData();
            }

            return invalidCommands.Count == 0;
        }

        private static string FormatCommandSnapshot(in GPUIndirectRenderCommand cmd)
            => $"mesh={cmd.MeshID} material={cmd.MaterialID} pass={cmd.RenderPass} instances={cmd.InstanceCount}";

        private bool IsCulledCommandValid(GPUScene scene, in GPUIndirectRenderCommand cmd, ISet<uint> missingMaterialIds, out string? reason)
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
                foreach (var entry in invalidCommands)
                {
                    string key = entry.reason;
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
            if (!(Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false))
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
            shader.Uniform("CameraLayerMask", unchecked((uint)0xFFFFFFFF));
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

            uint groupSize = 256;
            uint groups = (count + groupSize - 1) / groupSize;
            shader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

            VisibleCommandCount = ReadUInt(_culledCountBuffer);

            Dbg($"SoACull visible={VisibleCommandCount}","SoA");
        }

        //private void GatherCulled(GPUScene scene)
        //{
        //    Dbg("GatherCulled begin","SoA");

        //    if (_gatherProgram is null || _soaIndexList is null || CulledSceneToRenderBuffer is null || _culledCountBuffer is null)
        //        return;

        //    uint count = VisibleCommandCount;
        //    if (count == 0)
        //        return;

        //    _gatherProgram.BindBuffer(scene.CommandsInputBuffer, 0);
        //    _gatherProgram.BindBuffer(_soaIndexList, 1);
        //    _gatherProgram.BindBuffer(CulledSceneToRenderBuffer, 2);
        //    _gatherProgram.BindBuffer(_culledCountBuffer, 3);

        //    uint groupSize = 256;
        //    uint groups = (count + groupSize - 1) / groupSize;
        //    if (groups == 0)
        //        groups = 1;

        //    _gatherProgram.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

        //    Dbg($"GatherCulled dispatched groups={groups} count={count}","SoA");
        //}

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

            uint groupSize = 256;
            uint numGroups = (count + groupSize - 1) / groupSize;
            _debugDrawProgram.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"DebugDraw dispatched groups={numGroups} count={count}","Stats");
        }
    }
}
