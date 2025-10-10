using System.Numerics;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        // TESTING: set true to bypass GPU frustum/flag culling and treat all commands as visible
        public bool ForcePassthroughCulling { get; set; } = true; // enable for investigation

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

            if (!isMapped)
                return buf.GetDataRawAtIndex<uint>(index);

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            var addr = buf.GetMappedAddresses().FirstOrDefault();
            if (addr == IntPtr.Zero)
                throw new Exception("ReadUIntAt failed - null pointer");
            uint value = ((uint*)addr.Pointer)[index];

            return value;
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

            if (!isMapped)
                return buf.GetDataRawAtIndex<uint>(0);

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            var addr = buf.GetMappedAddresses().FirstOrDefault();
            if (addr == IntPtr.Zero)
                throw new Exception("ReadUInt failed - null pointer");
            var value = addr.UInt;

            return value;
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

            if (IndirectDebug.LogCountBufferWrites)
            {
                string label = buf.AttributeName ?? buf.Target.ToString();
                Debug.Out($"{FormatDebugPrefix("Indirect")} [Indirect/Count] {label} <= {value}");
            }

            return value;
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

            // Passthrough path (testing) � copy all input commands to culled buffer and mark all visible
            if (ForcePassthroughCulling)
                PassthroughCull(gpuCommands, numCommands);
            else
                Cull(gpuCommands, camera, numCommands);
        }

        private void Cull(GPUScene gpuCommands, XRCamera? camera, uint numCommands)
        {
            if (_cullingComputeShader is null || camera is null)
                return;
            
            var planes = camera.WorldFrustum().Planes.Select(x => x.AsVector4()).ToArray();
            if (planes.Length >= 6)
                _cullingComputeShader.Uniform("FrustumPlanes", planes);
            _cullingComputeShader.Uniform("MaxRenderDistance", camera.FarZ);
            _cullingComputeShader.Uniform("CameraLayerMask", unchecked((uint)0xFFFFFFFF));
            _cullingComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _cullingComputeShader.Uniform("InputCommandCount", (int)numCommands);
            _cullingComputeShader.Uniform("MaxCulledCommands", (int)CulledSceneToRenderBuffer.ElementCount);
            _cullingComputeShader.Uniform("DisabledFlagsMask", 0u);
            _cullingComputeShader.Uniform("CameraPosition", camera.Transform.WorldTranslation);

            _cullingComputeShader.BindBuffer(gpuCommands.CommandsInputBuffer, 0);
            _cullingComputeShader.BindBuffer(CulledSceneToRenderBuffer, 1);
            if (_culledCountBuffer is not null)
                _cullingComputeShader.BindBuffer(_culledCountBuffer, 2);
            if (_cullingOverflowFlagBuffer is not null)
                _cullingComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 3);
            if (_statsBuffer != null)
                _cullingComputeShader.BindBuffer(_statsBuffer, 8);

            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(numCommands);
            _cullingComputeShader.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            Dbg($"Cull dispatched groups={x} cmds={numCommands}", "Culling");

            if (_culledCountBuffer is not null)
            {
                try
                {
                    VisibleCommandCount = ReadUInt(_culledCountBuffer); // existing helper
                    Dbg($"Cull visible={VisibleCommandCount}", "Culling");
                }
                catch
                {
                    VisibleCommandCount = CulledSceneToRenderBuffer.ElementCount;
                    Dbg("Cull readback failed - fallback", "Culling");
                }
            }
            else
                VisibleCommandCount = CulledSceneToRenderBuffer.ElementCount;
        }

        /// <summary>
        /// Culling passthrough mode � copy all input commands to culled buffer and mark all visible.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="numCommands"></param>
        private void PassthroughCull(GPUScene scene, uint numCommands)
        {
            if (CulledSceneToRenderBuffer is null || _copyCommandsProgram is null || _culledCountBuffer is null)
                return;
            
            //TODO: use compute shader to avoid CPU roundtripping
            XRDataBuffer src = scene.CommandsInputBuffer;
            XRDataBuffer dst = CulledSceneToRenderBuffer;

            uint capacity = CulledSceneToRenderBuffer.ElementCount;
            uint copyCount = Math.Min(numCommands, capacity);

            //Copy commands
            _copyCommandsProgram?.BindBuffer(src, 0);
            _copyCommandsProgram?.BindBuffer(dst, 1);
            _copyCommandsProgram?.BindBuffer(_culledCountBuffer, 2);

            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(copyCount);
            _copyCommandsProgram?.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            VisibleCommandCount = copyCount;
            Dbg($"Cull passthrough visible={VisibleCommandCount}", "Culling");
        }

        private void ExtractSoA(GPUScene scene)
        {
            Dbg("ExtractSoA begin","SoA");

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
            _extractSoAComputeShader.BindBuffer(scene.CommandsInputBuffer, 0);
            _extractSoAComputeShader.BindBuffer(spheres, 1);
            _extractSoAComputeShader.BindBuffer(meta, 2);

            uint groupSize = 256;
            uint groups = (count + groupSize - 1) / groupSize;
            _extractSoAComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

            Dbg($"ExtractSoA dispatched groups={groups} count={count}","SoA");
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

        private void GatherCulled(GPUScene scene)
        {
            Dbg("GatherCulled begin","SoA");

            if (_gatherProgram is null || _soaIndexList is null || CulledSceneToRenderBuffer is null || _culledCountBuffer is null)
                return;

            uint count = VisibleCommandCount;
            if (count == 0)
                return;

            _gatherProgram.BindBuffer(scene.CommandsInputBuffer, 0);
            _gatherProgram.BindBuffer(_soaIndexList, 1);
            _gatherProgram.BindBuffer(CulledSceneToRenderBuffer, 2);
            _gatherProgram.BindBuffer(_culledCountBuffer, 3);

            uint groupSize = 256;
            uint groups = (count + groupSize - 1) / groupSize;
            if (groups == 0)
                groups = 1;

            _gatherProgram.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

            Dbg($"GatherCulled dispatched groups={groups} count={count}","SoA");
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
            _debugDrawProgram.BindBuffer(scene.CommandsInputBuffer, 1);
            _debugDrawProgram.BindBuffer(_culledCountBuffer, 2);

            uint groupSize = 256;
            uint numGroups = (count + groupSize - 1) / groupSize;
            _debugDrawProgram.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"DebugDraw dispatched groups={numGroups} count={count}","Stats");
        }
    }
}
