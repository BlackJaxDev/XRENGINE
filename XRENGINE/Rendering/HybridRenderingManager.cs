using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly Dictionary<uint, XRRenderProgram> _materialPrograms = [];

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
                RenderTraditional(
                    renderPasses,
                    camera,
                    scene,
                    indirectDrawBuffer,
                    vaoRenderer,
                    currentRenderPass,
                    parameterBuffer);
            else
                RenderTraditionalBatched(
                    renderPasses,
                    camera,
                    scene,
                    indirectDrawBuffer,
                    vaoRenderer,
                    currentRenderPass,
                    parameterBuffer,
                    batches,
                    materialMap);
        }

        private static void LogIndirectPath(bool useCount, uint drawCountOrMax, uint stride, uint? offset = null)
        {
            string path = useCount ? "IndirectCount" : (offset.HasValue ? "IndirectWithOffset" : "Indirect");
            string msg = offset.HasValue
                ? $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride} byteOffset={offset.Value}"
                : $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride}";
            Debug.Out(msg);
        }

        //private static bool TryReadDrawCount(XRDataBuffer? parameterBuffer, out uint drawCount)
        //{
        //    drawCount = 0;
        //    return false; // GPU pipeline operates without CPU readbacks.
        //}

        //private static void ClearIndirectTail(XRDataBuffer indirectDrawBuffer, XRDataBuffer? parameterBuffer, uint maxCommands)
        //{
        //    if (maxCommands == 0 || 
        //        indirectDrawBuffer is null || 
        //        DebugSettings.SkipIndirectTailClear || 
        //        !TryReadDrawCount(parameterBuffer, out uint drawCount) ||
        //        drawCount >= maxCommands)
        //        return;

        //    uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        //    uint staleCount = maxCommands - drawCount;
        //    ulong byteOffset = (ulong)drawCount * stride;
        //    ulong byteLength = (ulong)staleCount * stride;

        //    if (byteLength == 0 || byteOffset > int.MaxValue || byteLength > uint.MaxValue)
        //    {
        //        Debug.LogWarning($"Skipping indirect tail clear: offset={byteOffset} length={byteLength} exceeds CPU copy limits.");
        //        return;
        //    }

        //    var zeroCommand = default(DrawElementsIndirectCommand);
        //    for (uint i = 0; i < staleCount; ++i)
        //        indirectDrawBuffer.SetDataRawAtIndex(drawCount + i, zeroCommand);

        //    indirectDrawBuffer.PushSubData((int)byteOffset, (uint)byteLength);
        //}

        private static void DispatchRenderIndirect(
            XRDataBuffer? indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            uint drawCount,
            uint maxCommands,
            XRDataBuffer? parameterBuffer,
            XRRenderProgram? graphicsProgram,
            XRCamera? camera)
        {
            Debug.Out("=== DispatchRenderIndirect START ===");
            Debug.Out($"Parameters: drawCount={drawCount}, maxCommands={maxCommands}");
            Debug.Out($"graphicsProgram={(graphicsProgram != null ? "present" : "NULL")}");
            Debug.Out($"camera={(camera != null ? "present" : "null")}");
            
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.LogWarning("No active renderer found for indirect draw.");
                return;
            }

            if (indirectDrawBuffer is null || maxCommands == 0)
            {
                Debug.LogWarning($"Invalid dispatch state: buffer={(indirectDrawBuffer != null ? "present" : "null")}, maxCommands={maxCommands}");
                return;
            }

            // Bind graphics program for rendering (vertex/fragment shaders)
            if (graphicsProgram is not null)
            {
                Debug.Out("Binding graphics program...");
                graphicsProgram.Use();
                Debug.Out("Graphics program bound successfully");
                
                // Set camera/engine uniforms
                if (camera is not null)
                {
                    Debug.Out("Setting engine uniforms...");
                    renderer.SetEngineUniforms(graphicsProgram, camera);
                    Debug.Out("Engine uniforms set");
                }
                else
                {
                    Debug.LogWarning("No camera provided for uniforms!");
                }
            }
            else
            {
                Debug.LogWarning("No graphics program bound for indirect rendering. Rendering WILL FAIL.");
                return; // Don't proceed without a program
            }

            // Bind the provided VAO (if any)
            var version = vaoRenderer?.GetDefaultVersion();
            Debug.Out($"Binding VAO: version={(version != null ? "present" : "null")}");
            renderer.BindVAOForRenderer(version);

            // Configure VAO attributes for the bound program
            if (graphicsProgram is not null && vaoRenderer is not null)
            {
                Debug.Out("Configuring VAO attributes for program...");
                renderer.ConfigureVAOAttributesForProgram(graphicsProgram, version);
                Debug.Out("VAO attributes configured");
            }

            // Validate element buffer presence (required for *ElementsIndirect* variants)
            if (!renderer.ValidateIndexedVAO(version))
            {
                Debug.LogWarning("Indirect draw aborted: no index (element) buffer bound to VAO. Skipping MultiDrawElementsIndirect.");
                renderer.BindVAOForRenderer(null);
                return;
            }
            Debug.Out("VAO validation passed - index buffer present");

            Debug.Out("Binding indirect draw buffer...");
            renderer.BindDrawIndirectBuffer(indirectDrawBuffer);

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            bool parameterReady = parameterBuffer is not null && EnsureParameterBufferReady(parameterBuffer);
            bool useCount = parameterReady && !DebugSettings.DisableCountDrawPath && renderer.SupportsIndirectCountDraw();

            Debug.Out($"Draw mode: useCount={useCount}, stride={stride}");

            if (DebugSettings.ValidateBufferLayouts)
                ValidateIndirectBufferState(indirectDrawBuffer, maxCommands, stride);

            try
            {
                if (useCount)
                {
                    Debug.Out("Using MultiDrawElementsIndirectCount path");
                    renderer.BindParameterBuffer(parameterBuffer!);
                    renderer.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                    LogIndirectPath(true, maxCommands, stride);
                    renderer.MultiDrawElementsIndirectCount(maxCommands, stride);
                    Debug.Out("MultiDrawElementsIndirectCount issued");
                }
                else
                {
                    if (drawCount == 0)
                        drawCount = maxCommands;
                    Debug.Out($"Using MultiDrawElementsIndirect path with count={drawCount}");
                    LogIndirectPath(false, drawCount, stride);
                    renderer.MultiDrawElementsIndirect(drawCount, stride);
                    Debug.Out("MultiDrawElementsIndirect issued");
                }

                LogGLErrors(renderer, useCount ? "MultiDrawElementsIndirectCount" : "MultiDrawElementsIndirect");
            }
            finally
            {
                if (useCount)
                    renderer.UnbindParameterBuffer();

                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
                Debug.Out("Cleanup complete");
            }
            
            Debug.Out("=== DispatchRenderIndirect END ===");
        }

        private static void DispatchRenderIndirectRange(
            XRDataBuffer? indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            uint drawOffset,
            uint drawCount,
            XRDataBuffer? parameterBuffer,
            XRRenderProgram? graphicsProgram,
            XRCamera? camera)
        {
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.LogWarning("No active renderer found for indirect draw.");
                return;
            }

            if (indirectDrawBuffer is null || drawCount == 0)
                return;

            // Bind graphics program for rendering
            if (graphicsProgram is not null)
            {
                graphicsProgram.Use();
                
                // Set camera/engine uniforms
                if (camera is not null)
                    renderer.SetEngineUniforms(graphicsProgram, camera);
            }

            // Bind VAO
            var version = vaoRenderer?.GetDefaultVersion();
            renderer.BindVAOForRenderer(version);

            // Configure VAO attributes for the bound program
            if (graphicsProgram is not null && vaoRenderer is not null)
                renderer.ConfigureVAOAttributesForProgram(graphicsProgram, version);

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

                LogGLErrors(renderer, usingCountPath ? "MultiDrawElementsIndirectCount" : "MultiDrawElementsIndirectWithOffset");
            }
            finally
            {
                if (usingCountPath)
                    renderer.UnbindParameterBuffer();

                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
            }
        }

        private static void LogGLErrors(AbstractRenderer renderer, string context)
        {
            if (renderer is OpenGLRenderer glRenderer)
                glRenderer.LogGLErrors(context);
        }

        private static bool EnsureParameterBufferReady(XRDataBuffer parameterBuffer, bool requireMapped = false)
        {
            if (DebugSettings.ValidateLiveHandles && parameterBuffer.APIWrappers.Count == 0)
            {
                Debug.LogWarning("Parameter buffer has no active API wrappers; disabling count path.");
                return false;
            }

            if (requireMapped)
            {
                if (parameterBuffer.ActivelyMapping.Count == 0)
                {
                    parameterBuffer.MapBufferData();
                    if (parameterBuffer.ActivelyMapping.Count == 0)
                    {
                        Debug.LogWarning("Failed to map parameter buffer; falling back to non-count draw path.");
                        return false;
                    }
                }
            }
            else if (parameterBuffer.ActivelyMapping.Count > 0)
            {
                parameterBuffer.UnmapBufferData();
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
            Debug.Out("=== RenderTraditional START ===");
            
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

            Debug.Out($"Scene state: TotalCommands={scene.TotalCommandCount}, MaterialCount={scene.MaterialMap.Count}");
            Debug.Out($"VAO state: vaoRenderer={(vaoRenderer != null ? "present" : "null")}");
            if (vaoRenderer != null)
            {
                Debug.Out($"VAO buffers: {string.Join(", ", vaoRenderer.Buffers.Keys)}");
            }

            // Declare these once at the method start to avoid shadowing issues
            var matMap = renderPasses.GetMaterialMap(scene);
            Debug.Out($"Material map count: {matMap.Count}");
            
            XRMaterial? defaultMat = matMap.Values.FirstOrDefault() ?? XRMaterial.InvalidMaterial;
            Debug.Out($"Default material: {(defaultMat != null ? defaultMat.Name ?? "<unnamed>" : "null")}");
            
            XRRenderProgram? renderProgram = null;
            if (defaultMat is not null)
            {
                uint matKey = (uint)defaultMat.GetHashCode();
                Debug.Out($"Creating/getting program for material hash: {matKey}");
                
                renderProgram = EnsureCombinedProgram(matKey, defaultMat, vaoRenderer);
                
                if (renderProgram != null)
                {
                    Debug.Out($"Graphics program obtained: ShaderCount={defaultMat.Shaders.Count}, ProgramValid={renderProgram != null}");
                    
                    // Validate the program has required shaders
                    bool hasVertex = defaultMat.Shaders.Any(s => s?.Type == EShaderType.Vertex);
                    bool hasFragment = defaultMat.Shaders.Any(s => s?.Type == EShaderType.Fragment);
                    Debug.Out($"Program shader types: Vertex={hasVertex}, Fragment={hasFragment}");

                    // Set material uniforms
                    var renderer = AbstractRenderer.Current;
                    if (renderer != null)
                    {
                        renderer.SetMaterialUniforms(defaultMat, renderProgram);
                        renderer.ApplyRenderParameters(defaultMat.RenderOptions);
                        Debug.Out("Material uniforms and render parameters set");
                    }
                }
                else
                {
                    Debug.LogWarning("Failed to create graphics program!");
                }
            }
            else
            {
                Debug.LogWarning("No default material available!");
            }

            if (DebugSettings.ForceCpuIndirectBuild)
            {
                Debug.Out("Using CPU indirect build path");
                
                uint visibleCommands = renderPasses.VisibleCommandCount;
                if (visibleCommands == 0)
                {
                    uint culledCapacity = renderPasses.CulledSceneToRenderBuffer?.ElementCount ?? 0;
                    visibleCommands = Math.Min(scene.TotalCommandCount, culledCapacity);
                }

                if (visibleCommands == 0)
                    visibleCommands = Math.Min(scene.TotalCommandCount, indirectDrawBuffer.ElementCount);

                Debug.Out($"CPU build: visibleCommands={visibleCommands}");

                uint built = BuildIndirectCommandsCpu(renderPasses, scene, indirectDrawBuffer, visibleCommands, currentRenderPass, null);

                if (built == 0)
                {
                    Debug.LogWarning("CPU indirect build produced zero draw commands. Skipping indirect draw dispatch.");
                    return;
                }

                Debug.Out($"CPU indirect build generated {built} draw command(s) (requested {visibleCommands}).");

                DispatchRenderIndirect(
                    indirectDrawBuffer,
                    vaoRenderer,
                    built,
                    built,
                    null,
                    renderProgram,
                    camera);

                Debug.Out("=== RenderTraditional END (CPU path) ===");
                return;
            }

            Debug.Out("Using GPU indirect build path");

            // Ensure the program actually contains a compute shader stage
            var mask = _indirectCompProgram.GetShaderTypeMask();
            Debug.Out($"Compute program shader mask: {mask}");
            
            if ((mask & EProgramStageMask.ComputeShaderBit) == 0)
            {
                Debug.LogWarning("Traditional rendering program does not contain a compute shader. Cannot dispatch compute.");
                return;
            }

            // Use traditional compute shader program
            _indirectCompProgram.Use();
            Debug.Out("Compute program bound");

            // Input: culled commands
            _indirectCompProgram.BindBuffer(renderPasses.CulledSceneToRenderBuffer, 0);
            Debug.Out($"Bound culled commands buffer: elements={renderPasses.CulledSceneToRenderBuffer.ElementCount}");

            // Output: indirect draw commands
            _indirectCompProgram.BindBuffer(indirectDrawBuffer, 1);
            Debug.Out($"Bound indirect draw buffer: elements={indirectDrawBuffer.ElementCount}");

            // Input: mesh data
            _indirectCompProgram.BindBuffer(meshDataBuffer, 2);
            Debug.Out($"Bound mesh data buffer: elements={meshDataBuffer.ElementCount}");

            // Input: culled draw count written during the culling stage (std430 binding = 3)
            var culledCountBuffer = renderPasses.CulledCountBuffer;
            if (culledCountBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(culledCountBuffer, 3);
                Debug.Out("Bound culled count buffer");
            }

            // Optional: GPU-visible draw count buffer consumed by glMultiDraw*Count (std430 binding = 4)
            if (parameterBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(parameterBuffer, 4);
                Debug.Out("Bound parameter buffer");
            }

            // Optional: overflow/truncation/stat buffers (std430 bindings = 5, 7, 8)
            var indirectOverflowFlagBuffer = renderPasses.IndirectOverflowFlagBuffer;
            if (indirectOverflowFlagBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(indirectOverflowFlagBuffer, 5);
                Debug.Out("Bound overflow flag buffer");
            }

            var truncationFlagBuffer = renderPasses.TruncationFlagBuffer;
            if (truncationFlagBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(truncationFlagBuffer, 7);
                Debug.Out("Bound truncation flag buffer");
            }

            var statsBuffer = renderPasses.StatsBuffer;
            if (statsBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(statsBuffer, 8);
                Debug.Out("Bound stats buffer");
            }

            // Set uniforms
            _indirectCompProgram.Uniform("CurrentRenderPass", currentRenderPass);
            _indirectCompProgram.Uniform("MaxIndirectDraws", (int)indirectDrawBuffer.ElementCount);
            Debug.Out($"Set uniforms: CurrentRenderPass={currentRenderPass}, MaxIndirectDraws={indirectDrawBuffer.ElementCount}");

            uint allocatedCommandCount = renderPasses.CulledSceneToRenderBuffer.ElementCount;
            Debug.Out($"Allocated command count: {allocatedCommandCount}");

            // Dispatch compute shader
            uint groupSize = 32; // Should match local_size_x in shader
            uint numGroups = (allocatedCommandCount + groupSize - 1) / groupSize;
            if (numGroups == 0)
                numGroups = 1; // ensure at least one group to write zeroed commands

            Debug.Out($"Dispatching compute: numGroups={numGroups}, groupSize={groupSize}");
            _indirectCompProgram.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            //Debug.Out("Compute dispatch complete");

            // Conservative barrier before consuming indirect buffer
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            //Debug.Out("Memory barrier issued");

            //uint maxCommands = Math.Min(allocatedCommandCount, indirectDrawBuffer.ElementCount);
            //ClearIndirectTail(indirectDrawBuffer, parameterBuffer, maxCommands);

            Debug.Out($"Dispatching indirect render: program={(renderProgram != null ? "valid" : "NULL")}");
            
            // Use the graphics program obtained at the start of the method
            DispatchRenderIndirect(
                indirectDrawBuffer,
                vaoRenderer,
                0,
                allocatedCommandCount,
                parameterBuffer,
                renderProgram,
                camera);

            Debug.Out("=== RenderTraditional END (GPU path) ===");
        }

        private struct PassDebugStats
        {
            public uint Total;
            public uint Emitted;
        }

        private static uint BuildIndirectCommandsCpu(
            GPURenderPassCollection renderPasses,
            GPUScene scene,
            XRDataBuffer indirectDrawBuffer,
            uint requestedCount,
            int currentRenderPass,
            List<uint>? materialOrder)
        {
            // Read from culled buffer, not the raw scene input buffer
            var commandsBuffer = renderPasses.CulledSceneToRenderBuffer;
            if (commandsBuffer is null)
                return 0;

            materialOrder?.Clear();

            // Use visible command count from culling stage
            uint totalCommands = Math.Min(renderPasses.VisibleCommandCount, commandsBuffer.ElementCount);
            if (totalCommands == 0)
            {
                if (DebugSettings.DumpIndirectArguments)
                    Debug.Out("CPU indirect build found no commands in culled buffer.");
                return 0;
            }

            uint maxWritable = Math.Min(requestedCount == 0 ? uint.MaxValue : requestedCount, Math.Min(totalCommands, indirectDrawBuffer.ElementCount));
            uint written = 0;
            int diagnosticsRemaining = DebugSettings.DumpIndirectArguments ? 8 : 0;
            Dictionary<uint, PassDebugStats>? passStats = null;
            Dictionary<string, uint>? skipBuckets = null;
            List<string>? sampleLines = null;

            if (DebugSettings.DumpIndirectArguments)
            {
                passStats = [];
                skipBuckets = new(StringComparer.OrdinalIgnoreCase);
                sampleLines = [];
            }

            for (uint i = 0; i < totalCommands && written < maxWritable; ++i)
            {
                var gpuCommand = commandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                string? skipReason = null;

                if (gpuCommand.MeshID == 0)
                    skipReason = "meshID=0";
                else if (currentRenderPass >= 0 && gpuCommand.RenderPass != (uint)currentRenderPass)
                    skipReason = $"renderPass mismatch command={gpuCommand.RenderPass} current={currentRenderPass}";

                GPUScene.MeshDataEntry meshEntry = default;
                if (skipReason is null)
                {
                    if (!scene.TryGetMeshDataEntry(gpuCommand.MeshID, out meshEntry))
                        skipReason = $"unresolved mesh data meshID={gpuCommand.MeshID}";
                    else if (meshEntry.IndexCount == 0)
                        skipReason = $"meshID={gpuCommand.MeshID} indexCount=0";
                }

                if (passStats is not null)
                {
                    ref PassDebugStats stats = ref CollectionsMarshal.GetValueRefOrAddDefault(passStats, gpuCommand.RenderPass, out _);
                    stats.Total++;
                    if (skipReason is null)
                        stats.Emitted++;
                }

                if (skipReason is not null)
                {
                    if (diagnosticsRemaining-- > 0)
                        Debug.Out($"CPU indirect skip[{i}] reason={skipReason}");
                    if (skipBuckets is not null)
                    {
                        skipBuckets.TryGetValue(skipReason, out uint count);
                        skipBuckets[skipReason] = count + 1;
                    }
                    continue;
                }

                if (sampleLines is not null && sampleLines.Count < 8)
                {
                    sampleLines.Add($"CPU indirect emit[{written}] pass={gpuCommand.RenderPass} mesh={gpuCommand.MeshID} material={gpuCommand.MaterialID} submesh={gpuCommand.SubmeshID & 0xFFFF}");
                }

                var drawCmd = new DrawElementsIndirectCommand
                {
                    Count = meshEntry.IndexCount,
                    InstanceCount = gpuCommand.InstanceCount == 0 ? 1u : gpuCommand.InstanceCount,
                    FirstIndex = meshEntry.FirstIndex,
                    BaseVertex = (int)meshEntry.FirstVertex,
                    BaseInstance = 0
                };

                indirectDrawBuffer.SetDataRawAtIndex(written, drawCmd);
                materialOrder?.Add(gpuCommand.MaterialID);

                if (DebugSettings.DumpIndirectArguments && written < 8)
                {
                    Debug.Out($"CPU indirect[{written}] mesh={gpuCommand.MeshID} submesh={gpuCommand.SubmeshID & 0xFFFF} count={drawCmd.Count} firstIndex={drawCmd.FirstIndex} baseVertex={drawCmd.BaseVertex} material={gpuCommand.MaterialID}");
                }

                written++;
            }

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint byteLength = stride * written;
            if (byteLength > 0)
                indirectDrawBuffer.PushSubData(0, byteLength);

            if (DebugSettings.DumpIndirectArguments)
            {
                Debug.Out($"CPU indirect build final count={written} (requested {requestedCount}, buffer cap {indirectDrawBuffer.ElementCount}).");

                if (sampleLines is not null && sampleLines.Count > 0)
                {
                    foreach (string line in sampleLines)
                        Debug.Out(line);
                }

                if (passStats is not null && passStats.Count > 0)
                {
                    var histogram = passStats
                        .OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"pass={kvp.Key} seen={kvp.Value.Total} emitted={kvp.Value.Emitted}");
                    Debug.Out("CPU indirect pass histogram: " + string.Join(", ", histogram));
                }

                if (skipBuckets is not null && skipBuckets.Count > 0)
                {
                    var skipSummary = skipBuckets
                        .OrderByDescending(kvp => kvp.Value)
                        .Select(kvp => $"{kvp.Key}={kvp.Value}");
                    Debug.Out("CPU indirect skip reasons: " + string.Join(", ", skipSummary));
                }
            }

            Debug.Out($"HybridRenderingManager.BuildIndirectCommandsCpu: Built {written} indirect draw commands from {totalCommands} culled commands");

            return written;
        }

        // Ensure or create a combined graphics program for the given material ID (MVP: combined program only)
        private XRRenderProgram? EnsureCombinedProgram(uint materialID, XRMaterial material, XRMeshRenderer? vaoRenderer)
        {
            Debug.Out($"=== EnsureCombinedProgram: materialID={materialID} ===");
            
            if (_materialPrograms.TryGetValue(materialID, out var existing))
            {
                Debug.Out("Using cached program");
                return existing;
            }

            Debug.Out($"Creating new program for material: {material.Name ?? "<unnamed>"}");
            Debug.Out($"Material has {material.Shaders.Count} shaders");

            var shaderList = new List<XRShader>(material.Shaders.Where(shader => shader is not null));
            Debug.Out($"Non-null shaders: {shaderList.Count}");
            
            foreach (var shader in shaderList)
            {
                Debug.Out($"  Shader type: {shader.Type}");
            }

            // Ensure we only ever attach a single vertex shader to this combined program
            int existingVertexIndex = shaderList.FindIndex(shader => shader.Type == EShaderType.Vertex);
            Debug.Out($"Existing vertex shader index: {existingVertexIndex}");
            
            if (existingVertexIndex >= 0)
            {
                for (int i = shaderList.Count - 1; i >= 0; --i)
                {
                    if (shaderList[i].Type == EShaderType.Vertex && i != existingVertexIndex)
                    {
                        Debug.LogWarning($"Material {material.Name ?? "<unnamed>"} has multiple vertex shaders; keeping the first and discarding the rest for combined program.");
                        shaderList.RemoveAt(i);
                    }
                }
            }

            // If the material lacks a vertex shader, generate a default one using the VAO's mesh
            if (existingVertexIndex < 0)
            {
                Debug.Out("Material lacks vertex shader - generating default");
                XRShader? generatedVS = null;
                var mesh = vaoRenderer?.Mesh;
                if (mesh is not null)
                {
                    Debug.Out($"Generating vertex shader from mesh: {mesh.Name ?? "<unnamed>"}");
                    var gen = new DefaultVertexShaderGenerator(mesh)
                    {
                        // Emit a lean shader (no redundant gl_PerVertex struct when using separable programs)
                        WriteGLPerVertexOutStruct = false
                    };
                    string vertexShaderSource = gen.Generate();
                    Debug.Out($"Generated vertex shader ({vertexShaderSource.Length} chars)");
                    generatedVS = new XRShader(EShaderType.Vertex, vertexShaderSource);
                }
                else
                {
                    Debug.Out("No mesh available - using fallback vertex shader");
                    string fallbackSource = BuildFallbackVertexShader(vaoRenderer);
                    Debug.Out($"Generated fallback vertex shader ({fallbackSource.Length} chars)");
                    generatedVS = new XRShader(EShaderType.Vertex, fallbackSource);
                }

                if (generatedVS is not null)
                {
                    shaderList.Add(generatedVS);
                    Debug.Out("Vertex shader added to shader list");
                }
            }

            Debug.Out($"Final shader list count: {shaderList.Count}");
            Debug.Out("Creating and linking program...");
            
            var program = new XRRenderProgram(linkNow: false, separable: false, shaderList);
            program.AllowLink();
            program.Link();
            
            Debug.Out($"Program created and linked: {(program != null ? "SUCCESS" : "FAILED")}");

            _materialPrograms[materialID] = program;
            Debug.Out("Program cached");
            
            return program;
        }

        private static string BuildFallbackVertexShader(XRMeshRenderer? vaoRenderer)
        {
            // Assemble a minimal vertex shader that covers the available atlas attributes when no mesh metadata exists.
            var sb = new StringBuilder();
            sb.AppendLine("#version 460");

            uint location = 0;
            sb.AppendLine($"layout(location={location++}) in vec3 {ECommonBufferType.Position};");

            bool hasNormals = HasRendererBuffer(vaoRenderer, ECommonBufferType.Normal.ToString());
            bool hasTangents = HasRendererBuffer(vaoRenderer, ECommonBufferType.Tangent.ToString());

            if (hasNormals)
                sb.AppendLine($"layout(location={location++}) in vec3 {ECommonBufferType.Normal};");
            if (hasTangents)
                sb.AppendLine($"layout(location={location++}) in vec3 {ECommonBufferType.Tangent};");

            var texCoordBindings = GetRendererBuffersWithPrefix(vaoRenderer, ECommonBufferType.TexCoord.ToString());
            foreach (string binding in texCoordBindings)
            {
                sb.AppendLine($"layout(location={location++}) in vec2 {binding};");
            }

            var colorBindings = GetRendererBuffersWithPrefix(vaoRenderer, ECommonBufferType.Color.ToString());
            foreach (string binding in colorBindings)
            {
                sb.AppendLine($"layout(location={location++}) in vec4 {binding};");
            }

            sb.AppendLine("layout(location=0) out vec3 FragPos;");
            sb.AppendLine("layout(location=1) out vec3 FragNorm;");
            if (hasTangents)
            {
                sb.AppendLine("layout(location=2) out vec3 FragTan;");
                sb.AppendLine("layout(location=3) out vec3 FragBinorm;");
            }

            for (int i = 0; i < texCoordBindings.Count && i < 8; ++i)
                sb.AppendLine($"layout(location={4 + i}) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, i)};");

            for (int i = 0; i < colorBindings.Count && i < 8; ++i)
                sb.AppendLine($"layout(location={12 + i}) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, i)};");

            sb.AppendLine($"layout(location=20) out vec3 {DefaultVertexShaderGenerator.FragPosLocalName};");

            sb.AppendLine("uniform mat4 ModelMatrix;");
            sb.AppendLine($"uniform mat4 {EEngineUniform.InverseViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform bool {EEngineUniform.VRMode};");

            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine("    vec4 localPos = vec4(Position, 1.0);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragPosLocalName} = localPos.xyz;");
            sb.AppendLine($"    mat4 viewMatrix = inverse({EEngineUniform.InverseViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix});");
            sb.AppendLine("    vec4 worldPos = ModelMatrix * localPos;");
            sb.AppendLine($"    vec4 clipPos = {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix} * viewMatrix * worldPos;");
            sb.AppendLine($"    if ({EEngineUniform.VRMode})");
            sb.AppendLine("        FragPos = worldPos.xyz;");
            sb.AppendLine("    else");
            sb.AppendLine("        FragPos = clipPos.xyz / max(clipPos.w, 1e-6);");

            if (hasNormals || hasTangents)
                sb.AppendLine("    mat3 normalMatrix = transpose(inverse(mat3(ModelMatrix)));");

            if (hasNormals)
            {
                sb.AppendLine("    FragNorm = normalize(normalMatrix * Normal);");
                if (hasTangents)
                {
                    sb.AppendLine("    FragTan = normalize(normalMatrix * Tangent);");
                    sb.AppendLine("    FragBinorm = normalize(cross(FragNorm, FragTan));");
                }
            }
            else
            {
                sb.AppendLine("    FragNorm = vec3(0.0, 0.0, 1.0);");
                if (hasTangents)
                {
                    sb.AppendLine("    FragTan = vec3(1.0, 0.0, 0.0);");
                    sb.AppendLine("    FragBinorm = vec3(0.0, 1.0, 0.0);");
                }
            }

            for (int i = 0; i < texCoordBindings.Count && i < 8; ++i)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragUVName, i)} = {texCoordBindings[i]};");

            for (int i = 0; i < colorBindings.Count && i < 8; ++i)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragColorName, i)} = {colorBindings[i]};");

            sb.AppendLine("    gl_Position = clipPos;");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static bool HasRendererBuffer(XRMeshRenderer? renderer, string binding)
            => renderer?.Buffers is not null && renderer.Buffers.TryGetValue(binding, out _);

        private static List<string> GetRendererBuffersWithPrefix(XRMeshRenderer? renderer, string prefix)
        {
            if (renderer?.Buffers is null)
                return [];

            return [.. renderer.Buffers
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.Ordinal)];
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

            XRDataBuffer? dispatchParameterBuffer = parameterBuffer;
            uint cpuBuiltCount = 0;
            //bool usingCpuIndirect = DebugSettings.ForceCpuIndirectBuild;
            List<DrawBatch>? overrideBatches = null;
            List<uint>? cpuMaterialOrder = null;

            //if (usingCpuIndirect)
            //{
            //    uint requestedDraws = 0;
            //    foreach (var batch in batches)
            //    {
            //        uint batchEnd = batch.Offset + batch.Count;
            //        if (batchEnd > requestedDraws)
            //            requestedDraws = batchEnd;
            //    }

            //    if (requestedDraws == 0)
            //        requestedDraws = renderPasses.VisibleCommandCount;

            //    cpuMaterialOrder = new List<uint>((int)Math.Max(requestedDraws, 1u));
            //    cpuBuiltCount = BuildIndirectCommandsCpu(renderPasses, scene, indirectDrawBuffer, requestedDraws, currentRenderPass, cpuMaterialOrder);
            //    if (cpuBuiltCount == 0)
            //    {
            //        Debug.LogWarning("CPU indirect build produced zero draw commands for batched path. Skipping indirect draw dispatch.");
            //        return;
            //    }

            //    Debug.Out($"CPU indirect build generated {cpuBuiltCount} draw command(s) for batched path (requested {requestedDraws}).");

            //    // Disable the GPU-driven count path so we rely solely on the explicit batch counts.
            //    dispatchParameterBuffer = null;

            //    if (cpuMaterialOrder.Count > 0)
            //    {
            //        overrideBatches = [];
            //        uint offset = 0;
            //        while (offset < cpuBuiltCount && offset < (uint)cpuMaterialOrder.Count)
            //        {
            //            uint materialId = cpuMaterialOrder[(int)offset];
            //            uint runLength = 1;

            //            while (offset + runLength < cpuBuiltCount
            //                && (offset + runLength) < (uint)cpuMaterialOrder.Count
            //                && cpuMaterialOrder[(int)(offset + runLength)] == materialId)
            //            {
            //                runLength++;
            //            }

            //            overrideBatches.Add(new DrawBatch(offset, runLength, materialId == 0 ? uint.MaxValue : materialId));
            //            offset += runLength;
            //        }
            //    }
            //}
            //else
            //{
            //    ClearIndirectTail(indirectDrawBuffer, parameterBuffer, indirectDrawBuffer.ElementCount);
            //}

            var activeBatches = overrideBatches ?? batches;

            foreach (var batch in activeBatches)
            {
                if (batch.Count == 0)
                    continue;

                uint effectiveCount = batch.Count;
                //if (usingCpuIndirect)
                //{
                //    if (batch.Offset >= cpuBuiltCount)
                //    {
                //        Debug.Out($"Skipping batch at offset {batch.Offset} - beyond CPU-built draw count {cpuBuiltCount}.");
                //        continue;
                //    }

                //    uint maxAvailable = cpuBuiltCount - batch.Offset;
                //    if (effectiveCount > maxAvailable)
                //    {
                //        Debug.LogWarning($"Clamping CPU indirect batch at offset {batch.Offset} from {effectiveCount} to {maxAvailable} draw(s).");
                //        effectiveCount = maxAvailable;
                //    }
                //}

                // Resolve material from MaterialID (with CPU override fallback)
                uint lookupMaterialId = batch.MaterialID;
                if (lookupMaterialId == uint.MaxValue && cpuMaterialOrder is not null && batch.Offset < cpuMaterialOrder.Count)
                    lookupMaterialId = cpuMaterialOrder[(int)batch.Offset];

                XRMaterial? material = lookupMaterialId != 0 && materialMap.TryGetValue(lookupMaterialId, out var mat)
                    ? mat
                    : XRMaterial.InvalidMaterial;

                if (material is null)
                {
                    Debug.LogWarning($"No material for MaterialID={lookupMaterialId}. Skipping batch of {effectiveCount} draws at offset {batch.Offset}.");
                    continue;
                }

                // Ensure/Use graphics program (combined MVP)
                var program = EnsureCombinedProgram(lookupMaterialId, material, vaoRenderer);
                if (program is null)
                    continue;

                // Set material uniforms
                renderer.SetMaterialUniforms(material, program);
                renderer.ApplyRenderParameters(material.RenderOptions);

                Debug.Out($"Batch draw: materialID={lookupMaterialId} offset={batch.Offset} count={effectiveCount}");

                // Issue indirect multi-draw for the batch range
                DispatchRenderIndirectRange(indirectDrawBuffer, vaoRenderer, batch.Offset, effectiveCount, dispatchParameterBuffer, program, camera);
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
            GC.SuppressFinalize(this);
        }
    }
}