using System;
using System.Linq;
using System.Runtime.InteropServices;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Rendering
{
    /// <summary>
    /// Manages both traditional indirect rendering and modern meshlet-based rendering.
    /// </summary>
    public class HybridRenderingManager : XRBase, IDisposable
    {
        private XRRenderProgram? _indirectCompProgram;

        private bool _useMeshletPipeline = false;
        public bool UseMeshletPipeline
        {
            get => _useMeshletPipeline;
            set => SetField(ref _useMeshletPipeline, value);
        }

    // Cache of graphics programs created per material (combined program MVP)
    private readonly Dictionary<uint, XRRenderProgram> _materialPrograms = new();

    private static GPURenderPassCollection.IndirectDebugSettings DebugSettings => GPURenderPassCollection.IndirectDebug;

        public HybridRenderingManager()
        {
            InitializeTraditionalPipeline();
        }

        private void InitializeTraditionalPipeline()
        {
            // Load the traditional compute shader for indirect rendering
            _indirectCompProgram?.Destroy();
            _indirectCompProgram = new XRRenderProgram(
                linkNow: false,
                separable: false,
                ShaderHelper.LoadEngineShader("Compute\\GPURenderIndirect.comp", EShaderType.Compute)
            );
            _indirectCompProgram.AllowLink();
        }

        /// <summary>
        /// Batch description for issuing portions of the indirect buffer.
        /// Offset is in draws (not bytes); Count is number of draws.
        /// </summary>
        public readonly struct DrawBatch(uint offset, uint count, uint materialID)
        {
            public readonly uint Offset = offset; // draw index inside indirect buffer
            public readonly uint Count = count;  // number of draws in this batch
            public readonly uint MaterialID = materialID;
        }

        /// <summary>
        /// Render using the selected pipeline
        /// </summary>
        public void Render(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            XRDataBuffer indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            int currentRenderPass,
            XRDataBuffer? parameterBuffer,
            IReadOnlyList<DrawBatch>? batches = null)
        {
            if (camera is null || scene is null || (_useMeshletPipeline && scene.Meshlets.Render(camera)))
                return;

            // Material map from scene (ID -> XRMaterial)
            var materialMap = renderPasses.GetMaterialMap(scene);

            if (batches is null || batches.Count == 0)
                RenderTraditional(renderPasses, camera, scene, indirectDrawBuffer, vaoRenderer, currentRenderPass, parameterBuffer);
            else
                RenderTraditionalBatched(renderPasses, camera, scene, indirectDrawBuffer, vaoRenderer, currentRenderPass, parameterBuffer, batches, materialMap);
        }

        private static void LogIndirectPath(bool useCount, uint drawCountOrMax, uint stride, uint? offset = null)
        {
            string path = useCount ? "IndirectCount" : (offset.HasValue ? "IndirectWithOffset" : "Indirect");
            string msg = offset.HasValue
                ? $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride} byteOffset={offset.Value}"
                : $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride}";
            Debug.Out(msg);
        }

        private static bool TryReadDrawCount(XRDataBuffer? parameterBuffer, out uint drawCount)
        {
            drawCount = 0;
            if (parameterBuffer is null)
                return false;

            if (DebugSettings.ForceCpuFallbackCount)
                return false;

            if (!EnsureParameterBufferReady(parameterBuffer))
                return false;

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);

            try
            {
                var addr = parameterBuffer.GetMappedAddresses().FirstOrDefault();
                if (addr == IntPtr.Zero)
                    return false;

                drawCount = addr.UInt;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read draw count from parameter buffer: {ex.Message}");
                return false;
            }
        }

        private static void ClearIndirectTail(XRDataBuffer indirectDrawBuffer, XRDataBuffer? parameterBuffer, uint maxCommands)
        {
            if (maxCommands == 0 || indirectDrawBuffer is null)
                return;

            if (DebugSettings.SkipIndirectTailClear)
                return;

            if (!TryReadDrawCount(parameterBuffer, out uint drawCount))
                return;

            if (drawCount >= maxCommands)
                return;

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint staleCount = maxCommands - drawCount;
            ulong byteOffset = (ulong)drawCount * stride;
            ulong byteLength = (ulong)staleCount * stride;

            if (byteLength == 0)
                return;

            if (byteOffset > int.MaxValue || byteLength > uint.MaxValue)
            {
                Debug.LogWarning($"Skipping indirect tail clear: offset={byteOffset} length={byteLength} exceeds CPU copy limits.");
                return;
            }

            var zeroCommand = default(DrawElementsIndirectCommand);
            for (uint i = 0; i < staleCount; ++i)
                indirectDrawBuffer.SetDataRawAtIndex(drawCount + i, zeroCommand);

            indirectDrawBuffer.PushSubData((int)byteOffset, (uint)byteLength);
        }

        private static void DispatchRenderIndirect(
            XRDataBuffer? indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            uint drawCount,
            uint maxCommands,
            XRDataBuffer? parameterBuffer)
        {
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.LogWarning("No active renderer found for indirect draw.");
                return;
            }

            if (indirectDrawBuffer is null || maxCommands == 0)
                return;

            // Bind the provided VAO (if any)
            var version = vaoRenderer?.GetDefaultVersion();
            renderer.BindVAOForRenderer(version);

            // Validate element buffer presence (required for *ElementsIndirect* variants)
            if (!renderer.ValidateIndexedVAO(version))
            {
                Debug.LogWarning("Indirect draw aborted: no index (element) buffer bound to VAO. Skipping MultiDrawElementsIndirect.");
                renderer.BindVAOForRenderer(null);
                return;
            }

            renderer.BindDrawIndirectBuffer(indirectDrawBuffer);

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            bool parameterReady = parameterBuffer is not null && EnsureParameterBufferReady(parameterBuffer);
            bool useCount = parameterReady && !DebugSettings.DisableCountDrawPath && renderer.SupportsIndirectCountDraw();

            if (DebugSettings.ValidateBufferLayouts)
                ValidateIndirectBufferState(indirectDrawBuffer, maxCommands, stride);

            if (useCount)
            {
                renderer.BindParameterBuffer(parameterBuffer!);
                renderer.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                LogIndirectPath(true, maxCommands, stride);
                //renderer.MultiDrawElementsIndirectCount(maxCommands, stride);
                renderer.UnbindParameterBuffer();
            }
            else
            {
                if (drawCount == 0)
                    drawCount = maxCommands;
                LogIndirectPath(false, drawCount, stride);
                //renderer.MultiDrawElementsIndirect(drawCount, stride);
            }

            renderer.UnbindDrawIndirectBuffer();
            renderer.BindVAOForRenderer(null);
        }

        private static void DispatchRenderIndirectRange(
            XRDataBuffer? indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            uint drawOffset,
            uint drawCount,
            XRDataBuffer? parameterBuffer = null)
        {
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.LogWarning("No active renderer found for indirect draw.");
                return;
            }

            if (indirectDrawBuffer is null || drawCount == 0)
                return;

            // Bind VAO
            var version = vaoRenderer?.GetDefaultVersion();
            renderer.BindVAOForRenderer(version);

            if (!renderer.ValidateIndexedVAO(version))
            {
                Debug.LogWarning("Indirect draw aborted: no element buffer bound to VAO.");
                renderer.BindVAOForRenderer(null);
                return;
            }

            renderer.BindDrawIndirectBuffer(indirectDrawBuffer);
            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            nuint byteOffset = (nuint)(drawOffset * stride);

            uint effectiveDrawCount = drawCount;
            if (TryReadDrawCount(parameterBuffer, out uint totalDraws))
            {
                if (drawOffset >= totalDraws)
                {
                    Debug.LogWarning($"Skipping indirect range: drawOffset={drawOffset} exceeds GPU draw count={totalDraws}.");
                    renderer.UnbindDrawIndirectBuffer();
                    renderer.BindVAOForRenderer(null);
                    return;
                }

                uint remaining = totalDraws - drawOffset;
                if (effectiveDrawCount > remaining)
                {
                    Debug.LogWarning($"Clamping indirect range count from {effectiveDrawCount} to remaining {remaining} (offset={drawOffset}, total={totalDraws}).");
                    effectiveDrawCount = remaining;
                }
            }

            if (effectiveDrawCount == 0)
            {
                Debug.Out($"Skipping indirect range: zero draws for offset {drawOffset}.");
                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
                return;
            }

            bool parameterReady = parameterBuffer is not null && EnsureParameterBufferReady(parameterBuffer);
            bool usingCountPath = parameterReady && !DebugSettings.DisableCountDrawPath && renderer.SupportsIndirectCountDraw();
            try
            {
                if (usingCountPath)
                    renderer.BindParameterBuffer(parameterBuffer!);

                LogIndirectPath(usingCountPath, effectiveDrawCount, stride, (uint)byteOffset);

                if (parameterBuffer is not null)
                    renderer.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);

                if (DebugSettings.ValidateBufferLayouts)
                    ValidateIndirectBufferState(indirectDrawBuffer, drawOffset + effectiveDrawCount, stride);

                if (usingCountPath)
                    renderer.MultiDrawElementsIndirectCount(effectiveDrawCount, stride, byteOffset);
                else
                    renderer.MultiDrawElementsIndirectWithOffset(effectiveDrawCount, stride, byteOffset);

                LogGlErrors(renderer, usingCountPath ? "MultiDrawElementsIndirectCount" : "MultiDrawElementsIndirectWithOffset");
            }
            finally
            {
                if (usingCountPath)
                    renderer.UnbindParameterBuffer();

                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
            }
        }

        private static void LogGlErrors(AbstractRenderer renderer, string context)
        {
            if (renderer is OpenGLRenderer glRenderer)
                glRenderer.LogGlErrors(context);
        }

        private static bool EnsureParameterBufferReady(XRDataBuffer parameterBuffer)
        {
            if (DebugSettings.ValidateLiveHandles && parameterBuffer.APIWrappers.Count == 0)
            {
                Debug.LogWarning("Parameter buffer has no active API wrappers; disabling count path.");
                return false;
            }

            if (parameterBuffer.ActivelyMapping.Count == 0)
            {
                parameterBuffer.MapBufferData();
                if (parameterBuffer.ActivelyMapping.Count == 0)
                {
                    Debug.LogWarning("Failed to map parameter buffer; falling back to non-count draw path.");
                    return false;
                }
            }

            return true;
        }

        private static void ValidateIndirectBufferState(XRDataBuffer buffer, uint requiredDraws, uint expectedStride)
        {
            if (buffer.ElementSize != expectedStride)
                Debug.LogWarning($"Indirect buffer stride mismatch. Expected {expectedStride} bytes per command but buffer reports {buffer.ElementSize}.");

            if (requiredDraws > buffer.ElementCount)
                Debug.LogWarning($"Indirect buffer does not have enough commands allocated (required={requiredDraws}, allocated={buffer.ElementCount}).");
        }

        // Traditional indirect path
        private void RenderTraditional(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            XRDataBuffer indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            int currentRenderPass,
            XRDataBuffer? parameterBuffer)
        {
            if (_indirectCompProgram is null)
            {
                Debug.LogWarning("Indirect compute program is not initialized for traditional rendering.");
                return;
            }

            var meshDataBuffer = scene.MeshDataBuffer;
            if (meshDataBuffer is null)
            {
                Debug.LogWarning("Mesh data buffer is not initialized for traditional rendering.");
                return;
            }

            // Ensure the program actually contains a compute shader stage
            var mask = _indirectCompProgram.GetShaderTypeMask();
            if ((mask & EProgramStageMask.ComputeShaderBit) == 0)
            {
                Debug.LogWarning("Traditional rendering program does not contain a compute shader. Cannot dispatch compute.");
                return;
            }

            // Use traditional compute shader program
            _indirectCompProgram.Use();

            // Input: culled commands
            _indirectCompProgram.BindBuffer(renderPasses.CulledSceneToRenderBuffer, 0);

            // Output: indirect draw commands
            _indirectCompProgram.BindBuffer(indirectDrawBuffer, 1);

            // Input: mesh data
            _indirectCompProgram.BindBuffer(meshDataBuffer, 2);

            // Input: culled draw count written during the culling stage (std430 binding = 3)
            var culledCountBuffer = renderPasses.CulledCountBuffer;
            if (culledCountBuffer is not null)
                _indirectCompProgram.BindBuffer(culledCountBuffer, 3);

            // Optional: GPU-visible draw count buffer consumed by glMultiDraw*Count (std430 binding = 4)
            if (parameterBuffer is not null)
                _indirectCompProgram.BindBuffer(parameterBuffer, 4);

            // Optional: overflow/truncation/stat buffers (std430 bindings = 5, 7, 8)
            var indirectOverflowFlagBuffer = renderPasses.IndirectOverflowFlagBuffer;
            if (indirectOverflowFlagBuffer is not null)
                _indirectCompProgram.BindBuffer(indirectOverflowFlagBuffer, 5);

            var truncationFlagBuffer = renderPasses.TruncationFlagBuffer;
            if (truncationFlagBuffer is not null)
                _indirectCompProgram.BindBuffer(truncationFlagBuffer, 7);

            var statsBuffer = renderPasses.StatsBuffer;
            if (statsBuffer is not null)
                _indirectCompProgram.BindBuffer(statsBuffer, 8);

            // Set uniforms
            _indirectCompProgram.Uniform("CurrentRenderPass", currentRenderPass);
            _indirectCompProgram.Uniform("MaxIndirectDraws", (int)indirectDrawBuffer.ElementCount);

            uint allocatedCommandCount = renderPasses.CulledSceneToRenderBuffer.ElementCount;

            // Dispatch compute shader
            uint groupSize = 32; // Should match local_size_x in shader
            uint numGroups = (allocatedCommandCount + groupSize - 1) / groupSize;
            if (numGroups == 0)
                numGroups = 1; // ensure at least one group to write zeroed commands

            _indirectCompProgram.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            // Conservative barrier before consuming indirect buffer
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            uint maxCommands = Math.Min(allocatedCommandCount, indirectDrawBuffer.ElementCount);
            ClearIndirectTail(indirectDrawBuffer, parameterBuffer, maxCommands);

            DispatchRenderIndirect(
                indirectDrawBuffer,
                vaoRenderer,
                0,
                allocatedCommandCount,
                parameterBuffer);
        }

        // Ensure or create a combined graphics program for the given material ID (MVP: combined program only)
        private XRRenderProgram? EnsureCombinedProgram(uint materialID, XRMaterial material, XRMeshRenderer? vaoRenderer)
        {
            if (_materialPrograms.TryGetValue(materialID, out var existing))
                return existing;

            bool hasVertex = material.VertexShaders.Count > 0;

            IEnumerable<XRShader> shaders = material.Shaders;

            // If the material lacks a vertex shader, generate a default one using the VAO's mesh
            if (!hasVertex)
            {
                string? vsSource = null;
                var mesh = vaoRenderer?.Mesh;
                if (mesh is not null)
                {
                    var gen = new DefaultVertexShaderGenerator(mesh);
                    vsSource = gen.Generate();
                }
                else
                {
                    // Minimal fallback if mesh is unavailable: provide FragPos and FragNorm
                    vsSource = "#version 460\n"
                        + "layout(location=0) in vec3 Position;\n"
                        + "layout(location=0) out vec3 FragPos;\n"
                        + "layout(location=1) out vec3 FragNorm;\n"
                        + "uniform mat4 ModelMatrix;\n"
                        + "uniform mat4 InverseViewMatrix_VTX;\n"
                        + "uniform mat4 ProjMatrix_VTX;\n"
                        + "void main(){\n"
                        + "    vec4 localPos = vec4(Position,1.0);\n"
                        + "    mat4 view = inverse(InverseViewMatrix_VTX);\n"
                        + "    mat4 mvp = ProjMatrix_VTX * view * ModelMatrix;\n"
                        + "    vec4 outPos = mvp * localPos;\n"
                        + "    FragPos = outPos.xyz / outPos.w;\n"
                        + "    FragNorm = vec3(0.0, 0.0, 1.0);\n"
                        + "    gl_Position = outPos;\n"
                        + "}";
                }

                var generatedVS = new XRShader(EShaderType.Vertex, vsSource!);
                shaders = shaders.Append(generatedVS);
            }

            var program = new XRRenderProgram(linkNow: false, separable: false, shaders);
            program.AllowLink();
            program.Link();

            _materialPrograms[materialID] = program;
            return program;
        }

        // Traditional indirect path but issuing separate MultiDraw calls per material batch
        private void RenderTraditionalBatched(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            XRDataBuffer indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            int currentRenderPass,
            XRDataBuffer? parameterBuffer,
            IReadOnlyList<DrawBatch> batches,
            IReadOnlyDictionary<uint, XRMaterial> materialMap)
        {
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.LogWarning("No active renderer for batched indirect path.");
                return;
            }

            ClearIndirectTail(indirectDrawBuffer, parameterBuffer, indirectDrawBuffer.ElementCount);

            foreach (var batch in batches)
            {
                if (batch.Count == 0)
                    continue;

                // Resolve material from MaterialID
                XRMaterial? material = materialMap.TryGetValue(batch.MaterialID, out var mat) ? mat : XRMaterial.InvalidMaterial;
                if (material is null)
                {
                    Debug.LogWarning($"No material for MaterialID={batch.MaterialID}. Skipping batch of {batch.Count} draws at offset {batch.Offset}.");
                    continue;
                }

                // Ensure/Use graphics program (combined MVP)
                var program = EnsureCombinedProgram(batch.MaterialID, material, vaoRenderer);
                if (program is null)
                    continue;
                program.Use();

                // Configure VAO attributes for this program (prevents missing locations when switching programs)
                if (vaoRenderer is not null)
                    renderer.ConfigureVAOAttributesForProgram(program, vaoRenderer.GetDefaultVersion());

                // Set common uniforms/state
                renderer.SetEngineUniforms(program, camera);
                renderer.SetMaterialUniforms(material, program);
                renderer.ApplyRenderParameters(material.RenderOptions);

                Debug.Out($"Batch draw: materialID={batch.MaterialID} offset={batch.Offset} count={batch.Count}");

                // Issue indirect multi-draw for the batch range
                DispatchRenderIndirectRange(indirectDrawBuffer, vaoRenderer, batch.Offset, batch.Count, parameterBuffer);
            }
        }

        public struct RenderingStats
        {
            public int MeshCount;
            public bool UsingMeshletPipeline;
            public bool MeshShaderSupported;
        }

        public void Dispose()
        {
            _indirectCompProgram?.Destroy();
            foreach (var kv in _materialPrograms)
                kv.Value.Destroy();
            _materialPrograms.Clear();
        }
    }
}