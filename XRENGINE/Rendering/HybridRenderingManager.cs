using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Data;
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
    private readonly struct MaterialProgramCache(XRRenderProgram program, XRShader? generatedVertexShader)
        {
        public readonly XRRenderProgram Program = program;
        public readonly XRShader? GeneratedVertexShader = generatedVertexShader;
        }

        private readonly Dictionary<(uint materialId, int rendererKey), MaterialProgramCache> _materialPrograms = [];

    private static GPURenderPassCollection.IndirectDebugSettings DebugSettings => GPURenderPassCollection.IndirectDebug;
    private static readonly HashSet<uint> _warnedMultiVertexMaterials = [];
    private static bool IsGpuIndirectLoggingEnabled()
        => Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false;

    private static void GpuDebug(string message, params object[] args)
    {
        if (!IsGpuIndirectLoggingEnabled())
            return;

        Debug.Out(message, args);
    }

    private static void GpuDebug(FormattableString message)
    {
        if (!IsGpuIndirectLoggingEnabled())
            return;

        Debug.Out(message.ToString());
    }

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
            if (!IsGpuIndirectLoggingEnabled())
                return;

            string path = useCount ? "IndirectCount" : (offset.HasValue ? "IndirectWithOffset" : "Indirect");
            string msg = offset.HasValue
                ? $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride} byteOffset={offset.Value}"
                : $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride}";
            GpuDebug("{0}", msg);
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
            XRCamera? camera,
            Matrix4x4 modelMatrix)
        {
            bool logGpu = IsGpuIndirectLoggingEnabled();
            if (logGpu)
            {
                GpuDebug("=== DispatchRenderIndirect START ===");
                GpuDebug("Parameters: drawCount={0}, maxCommands={1}", drawCount, maxCommands);
                GpuDebug("graphicsProgram={0}", graphicsProgram != null ? "present" : "NULL");
                GpuDebug("camera={0}", camera != null ? "present" : "null");
            }
            
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
                if (logGpu)
                    GpuDebug("Binding graphics program...");
                graphicsProgram.Use();
                if (logGpu)
                    GpuDebug("Graphics program bound successfully");
                
                if (camera is not null)
                {
                    if (logGpu)
                        GpuDebug("Setting engine uniforms...");
                    renderer.SetEngineUniforms(graphicsProgram, camera);
                    if (logGpu)
                        GpuDebug("Engine uniforms set");
                }
                else
                {
                    Debug.LogWarning("No camera provided for uniforms!");
                }

                bool isIdentity = Matrix4x4.Equals(modelMatrix, Matrix4x4.Identity);
                graphicsProgram.Uniform(EEngineUniform.ModelMatrix.ToString(), modelMatrix);
                if (logGpu)
                {
                    if (!isIdentity)
                        GpuDebug("Model matrix translation=({0:F3},{1:F3},{2:F3})", modelMatrix.M41, modelMatrix.M42, modelMatrix.M43);
                    else
                        GpuDebug("Model matrix uniform set to identity");
                }
            }
            else
            {
                Debug.LogWarning("No graphics program bound for indirect rendering. Rendering WILL FAIL.");
                return; // Don't proceed without a program
            }

            // Bind the provided VAO (if any)
            var version = vaoRenderer?.GetDefaultVersion();
            if (logGpu)
                GpuDebug("Binding VAO: version={0}", version != null ? "present" : "null");
            renderer.BindVAOForRenderer(version);

            // Configure VAO attributes for the bound program
            if (graphicsProgram is not null && vaoRenderer is not null)
            {
                if (logGpu)
                    GpuDebug("Configuring VAO attributes for program...");
                renderer.ConfigureVAOAttributesForProgram(graphicsProgram, version);
                if (logGpu)
                    GpuDebug("VAO attributes configured");
            }

            // Validate element buffer presence (required for *ElementsIndirect* variants)
            if (!renderer.ValidateIndexedVAO(version))
            {
                Debug.LogWarning("Indirect draw aborted: no index (element) buffer bound to VAO. Skipping MultiDrawElementsIndirect.");
                renderer.BindVAOForRenderer(null);
                return;
            }
            if (logGpu)
                GpuDebug("VAO validation passed - index buffer present");

            if (logGpu)
                GpuDebug("Binding indirect draw buffer...");
            renderer.BindDrawIndirectBuffer(indirectDrawBuffer);

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            bool parameterReady = parameterBuffer is not null && EnsureParameterBufferReady(parameterBuffer);
            bool useCount = parameterReady && !DebugSettings.DisableCountDrawPath && renderer.SupportsIndirectCountDraw();

            if (logGpu)
                GpuDebug("Draw mode: useCount={0}, stride={1}", useCount, stride);

            if (DebugSettings.ValidateBufferLayouts)
                ValidateIndirectBufferState(indirectDrawBuffer, maxCommands, stride);

            try
            {
                if (useCount)
                {
                    if (logGpu)
                        GpuDebug("Using MultiDrawElementsIndirectCount path");
                    renderer.BindParameterBuffer(parameterBuffer!);
                    renderer.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                    LogIndirectPath(true, maxCommands, stride);
                    renderer.MultiDrawElementsIndirectCount(maxCommands, stride);
                    if (logGpu)
                        GpuDebug("MultiDrawElementsIndirectCount issued");
                }
                else
                {
                    if (drawCount == 0)
                        drawCount = maxCommands;
                    if (logGpu)
                        GpuDebug("Using MultiDrawElementsIndirect path with count={0}", drawCount);
                    LogIndirectPath(false, drawCount, stride);
                    renderer.MultiDrawElementsIndirect(drawCount, stride);
                    if (logGpu)
                        GpuDebug("MultiDrawElementsIndirect issued");
                }

                LogGLErrors(renderer, useCount ? "MultiDrawElementsIndirectCount" : "MultiDrawElementsIndirect");
            }
            finally
            {
                if (useCount)
                    renderer.UnbindParameterBuffer();

                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
                if (logGpu)
                    GpuDebug("Cleanup complete");
            }
            
            if (logGpu)
                GpuDebug("=== DispatchRenderIndirect END ===");
        }

        private static void DumpGpuIndirectArguments(
            GPURenderPassCollection renderPasses,
            XRDataBuffer indirectDrawBuffer,
            uint maxDrawAllowed,
            XRDataBuffer? parameterBuffer,
            uint visibleCount)
        {
            if (!IsGpuIndirectLoggingEnabled())
                return;

            GpuDebug("[GPUIndirect] dump invoked tick={0}", Environment.TickCount64);
            XRDataBuffer? drawCountBuffer = renderPasses.DrawCountBuffer ?? parameterBuffer;
            XRDataBuffer? culledCountBuffer = renderPasses.CulledCountBuffer;
            XRDataBuffer culledCommandBuffer = renderPasses.CulledSceneToRenderBuffer;
            bool mappedIndirectHere = false;
            bool mappedDrawCountHere = false;
            bool mappedCulledCountHere = false;
            bool mappedCulledCommandsHere = false;
            try
            {
                if (indirectDrawBuffer.ActivelyMapping.Count == 0)
                {
                    indirectDrawBuffer.MapBufferData();
                    mappedIndirectHere = true;
                }

                if (drawCountBuffer is not null && drawCountBuffer.ActivelyMapping.Count == 0)
                {
                    drawCountBuffer.MapBufferData();
                    mappedDrawCountHere = true;
                }

                if (culledCountBuffer is not null && culledCountBuffer.ActivelyMapping.Count == 0)
                {
                    culledCountBuffer.MapBufferData();
                    mappedCulledCountHere = true;
                }

                VoidPtr indirectPtr = indirectDrawBuffer
                    .GetMappedAddresses()
                    .FirstOrDefault(ptr => ptr.IsValid);

                if (!indirectPtr.IsValid)
                {
                    Debug.LogWarning("Failed to map indirect draw buffer for argument dump.");
                    return;
                }

                uint gpuDrawCount = 0;
                if (drawCountBuffer is not null)
                {
                    VoidPtr countPtr = drawCountBuffer
                        .GetMappedAddresses()
                        .FirstOrDefault(ptr => ptr.IsValid);

                    if (countPtr.IsValid)
                    {
                        unsafe
                        {
                            gpuDrawCount = Unsafe.ReadUnaligned<uint>(countPtr.Pointer);
                        }
                    }
                }

                uint culledDrawCount = 0;
                if (culledCountBuffer is not null)
                {
                    VoidPtr culledPtr = culledCountBuffer
                        .GetMappedAddresses()
                        .FirstOrDefault(ptr => ptr.IsValid);

                    if (culledPtr.IsValid)
                    {
                        unsafe
                        {
                            culledDrawCount = Unsafe.ReadUnaligned<uint>(culledPtr.Pointer);
                        }
                    }
                }

                uint fallbackCount = Math.Min(visibleCount, indirectDrawBuffer.ElementCount);
                if (fallbackCount == 0 && maxDrawAllowed > 0)
                    fallbackCount = Math.Min(maxDrawAllowed, indirectDrawBuffer.ElementCount);

                uint sampleCount = gpuDrawCount != 0 ? gpuDrawCount : fallbackCount;
                sampleCount = Math.Min(sampleCount, indirectDrawBuffer.ElementCount);
                sampleCount = Math.Min(sampleCount, 8u);

                var sb = new StringBuilder();
                bool usingGpuCount = gpuDrawCount != 0;
                sb.Append($"[GPUIndirect] tick={Environment.TickCount64} drawCount={gpuDrawCount} culled={culledDrawCount} visible={visibleCount} maxAllowed={maxDrawAllowed} sample={sampleCount} source={(usingGpuCount ? "GPU" : "Fallback")}");

                if (sampleCount > 0)
                {
                    uint stride = indirectDrawBuffer.ElementSize;
                    if (stride == 0)
                        stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();

                    unsafe
                    {
                        byte* basePtr = (byte*)indirectPtr.Pointer;
                        for (uint i = 0; i < sampleCount; ++i)
                        {
                            var cmd = Unsafe.ReadUnaligned<DrawElementsIndirectCommand>(basePtr + (int)(i * stride));
                            sb.Append($" |[{i}] count={cmd.Count} firstIndex={cmd.FirstIndex} baseVertex={cmd.BaseVertex} instances={cmd.InstanceCount}");
                        }
                    }
                }

                GpuDebug(sb.ToString());

                bool culledSupportsReadback =
                    (culledCommandBuffer.StorageFlags & EBufferMapStorageFlags.Read) != 0 &&
                    (culledCommandBuffer.RangeFlags & EBufferMapRangeFlags.Read) != 0;

                if (!usingGpuCount && visibleCount > 0 && culledSupportsReadback)
                {
                    try
                    {
                        if (culledCommandBuffer.ActivelyMapping.Count == 0)
                        {
                            culledCommandBuffer.MapBufferData();
                            mappedCulledCommandsHere = true;
                        }

                        VoidPtr culledPtr = culledCommandBuffer
                            .GetMappedAddresses()
                            .FirstOrDefault(ptr => ptr.IsValid);

                        if (culledPtr.IsValid)
                        {
                            uint culledStride = culledCommandBuffer.ElementSize;
                            if (culledStride == 0)
                                culledStride = (uint)Marshal.SizeOf<GPUIndirectRenderCommand>();

                            uint inspectCount = Math.Min(visibleCount, 3u);
                            unsafe
                            {
                                byte* culledBase = (byte*)culledPtr.Pointer;
                                for (uint i = 0; i < inspectCount; ++i)
                                {
                                    var culledCmd = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(culledBase + (int)(i * culledStride));
                                    GpuDebug("[GPUIndirect] culled[{0}] mesh={1} submesh={2} material={3} instances={4} pass={5}",
                                        i,
                                        culledCmd.MeshID,
                                        culledCmd.SubmeshID,
                                        culledCmd.MaterialID,
                                        culledCmd.InstanceCount,
                                        culledCmd.RenderPass);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to inspect culled commands: {ex.Message}");
                    }
                    finally
                    {
                        if (mappedCulledCommandsHere)
                            culledCommandBuffer.UnmapBufferData();
                    }
                }
                else if (!usingGpuCount && visibleCount > 0 && !culledSupportsReadback)
                {
                    GpuDebug("[GPUIndirect] Culled command buffer lacks read-mapping flags; skipping culled dump.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to dump GPU indirect arguments: {ex.Message}");
            }
            finally
            {
                if (mappedDrawCountHere && drawCountBuffer is not null)
                    drawCountBuffer.UnmapBufferData();

                if (mappedCulledCountHere && culledCountBuffer is not null)
                    culledCountBuffer.UnmapBufferData();

                if (mappedIndirectHere)
                    indirectDrawBuffer.UnmapBufferData();
            }
        }

        private static void DumpCulledCommandData(
            GPURenderPassCollection renderPasses,
            GPUScene scene,
            uint visibleCount)
        {
            if (!IsGpuIndirectLoggingEnabled() || !DebugSettings.DumpIndirectArguments || visibleCount == 0)
                return;

            XRDataBuffer culledBuffer = renderPasses.CulledSceneToRenderBuffer;
            bool mappedHere = false;
            try
            {
                if (culledBuffer.ActivelyMapping.Count == 0)
                {
                    culledBuffer.MapBufferData();
                    mappedHere = true;
                }

                VoidPtr ptr = culledBuffer
                    .GetMappedAddresses()
                    .FirstOrDefault(p => p.IsValid);

                if (!ptr.IsValid)
                {
                    GpuDebug("[GPUIndirect] Failed to map culled buffer for inspection.");
                    return;
                }

                uint stride = culledBuffer.ElementSize;
                if (stride == 0)
                    stride = GPUScene.CommandFloatCount * sizeof(float);

                uint samples = Math.Min(visibleCount, 3u);
                unsafe
                {
                    byte* basePtr = (byte*)ptr.Pointer;
                    for (uint i = 0; i < samples; ++i)
                    {
                        var cmd = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(basePtr + (int)(i * stride));
                        var sb = new StringBuilder();
                        sb.Append($"[GPUIndirect] visible[{i}] mesh={cmd.MeshID} submesh={cmd.SubmeshID & 0xFFFF} material={cmd.MaterialID} instances={cmd.InstanceCount} pass={cmd.RenderPass}");
                        Vector3 translation = cmd.WorldMatrix.Translation;
                        sb.Append($" | worldPos=({translation.X:F3},{translation.Y:F3},{translation.Z:F3})");
                        if (scene.TryGetMeshDataEntry(cmd.MeshID, out GPUScene.MeshDataEntry meshEntry))
                        {
                            sb.Append($" | meshData indexCount={meshEntry.IndexCount} firstIndex={meshEntry.FirstIndex} firstVertex={meshEntry.FirstVertex}");
                        }
                        else
                        {
                            sb.Append(" | meshData=<missing>");
                        }

                        GpuDebug(sb.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GPUIndirect] Failed to dump culled command data: {ex.Message}");
            }
            finally
            {
                if (mappedHere)
                    culledBuffer.UnmapBufferData();
            }
        }

        private static bool TryReadWorldMatrix(
            XRDataBuffer culledBuffer,
            uint commandIndex,
            out Matrix4x4 worldMatrix)
        {
            worldMatrix = Matrix4x4.Identity;

            if (commandIndex >= culledBuffer.ElementCount)
                return false;

            bool mappedHere = false;
            try
            {
                if (culledBuffer.ActivelyMapping.Count == 0)
                {
                    culledBuffer.MapBufferData();
                    mappedHere = true;
                }

                VoidPtr ptr = culledBuffer
                    .GetMappedAddresses()
                    .FirstOrDefault(p => p.IsValid);

                if (!ptr.IsValid)
                    return false;

                uint stride = culledBuffer.ElementSize;
                if (stride == 0)
                    stride = GPUScene.CommandFloatCount * sizeof(float);

                unsafe
                {
                    byte* basePtr = (byte*)ptr.Pointer;
                    byte* commandPtr = basePtr + (commandIndex * stride);
                    var command = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(commandPtr);
                    worldMatrix = command.WorldMatrix;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GPUIndirect] Failed to read world matrix at index {commandIndex}: {ex.Message}");
            }
            finally
            {
                if (mappedHere)
                    culledBuffer.UnmapBufferData();
            }

            return false;
        }

        private static void DispatchRenderIndirectRange(
            XRDataBuffer? indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            XRDataBuffer? culledCommandsBuffer,
            uint drawOffset,
            uint drawCount,
            XRDataBuffer? parameterBuffer,
            XRRenderProgram? graphicsProgram,
            XRCamera? camera,
            Matrix4x4 modelMatrix)
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

                // Bind per-draw command data (world matrix, etc.) for GPU-indirect vertex shader
                culledCommandsBuffer?.BindTo(graphicsProgram, 0);
                
                // Set camera/engine uniforms
                if (camera is not null)
                    renderer.SetEngineUniforms(graphicsProgram, camera);
                // Legacy materials might still reference ModelMatrix; set a sensible default.
                graphicsProgram.Uniform(EEngineUniform.ModelMatrix.ToString(), modelMatrix);
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
                GpuDebug("Skipping indirect range: zero draws for offset {0}.", drawOffset);
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
            bool logGpu = IsGpuIndirectLoggingEnabled();
            if (logGpu)
                GpuDebug("=== RenderTraditional START ===");
            
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

            if (logGpu)
            {
                GpuDebug("Scene state: TotalCommands={0}, MaterialCount={1}", scene.TotalCommandCount, scene.MaterialMap.Count);
                GpuDebug("VAO state: vaoRenderer={0}", vaoRenderer != null ? "present" : "null");
                if (vaoRenderer != null)
                    GpuDebug("VAO buffers: {0}", string.Join(", ", vaoRenderer.Buffers.Keys));
            }

            XRDataBuffer culledBuffer = renderPasses.CulledSceneToRenderBuffer;
            uint culledCapacity = culledBuffer.ElementCount;
            uint visibleCount = renderPasses.VisibleCommandCount;
            if (culledCapacity > 0 && visibleCount > culledCapacity)
                visibleCount = culledCapacity;

            Matrix4x4 modelMatrix = Matrix4x4.Identity;
            if (visibleCount > 0 && TryReadWorldMatrix(culledBuffer, 0, out Matrix4x4 firstWorldMatrix))
                modelMatrix = firstWorldMatrix;

            uint indirectCapacity = indirectDrawBuffer.ElementCount;
            uint maxDrawAllowed = visibleCount > 0
                ? Math.Min(indirectCapacity, visibleCount)
                : Math.Min(indirectCapacity, culledCapacity);

            if (logGpu)
                GpuDebug("Visible commands={0} culledCapacity={1} indirectCapacity={2}", visibleCount, culledCapacity, indirectCapacity);

            // Declare these once at the method start to avoid shadowing issues
            var matMap = renderPasses.GetMaterialMap(scene);
            if (logGpu)
                GpuDebug("Material map count: {0}", matMap.Count);

            XRMaterial? overrideMaterial = Engine.Rendering.State.OverrideMaterial;
            if (overrideMaterial is not null && logGpu)
                GpuDebug("Override material active: {0}", overrideMaterial.Name ?? "<unnamed>");

            XRMaterial? defaultMat = overrideMaterial ?? matMap.Values.FirstOrDefault() ?? XRMaterial.InvalidMaterial;
            if (logGpu)
                GpuDebug("Default material: {0}", defaultMat != null ? defaultMat.Name ?? "<unnamed>" : "null");
            
            XRRenderProgram? renderProgram = null;
            if (defaultMat is not null)
            {
                uint matKey = (uint)defaultMat.GetHashCode();
                if (logGpu)
                    GpuDebug("Creating/getting program for material hash: {0}", matKey);
                
                renderProgram = EnsureCombinedProgram(matKey, defaultMat, vaoRenderer);
                
                if (renderProgram != null)
                {
                    if (logGpu)
                        GpuDebug("Graphics program obtained: ShaderCount={0}, ProgramValid={1}", defaultMat.Shaders.Count, renderProgram != null);
                    
                    // Validate the program has required shaders
                    bool hasVertex = defaultMat.Shaders.Any(s => s?.Type == EShaderType.Vertex);
                    bool hasFragment = defaultMat.Shaders.Any(s => s?.Type == EShaderType.Fragment);
                    if (logGpu)
                        GpuDebug("Program shader types: Vertex={0}, Fragment={1}", hasVertex, hasFragment);

                    // Set material uniforms
                    var renderer = AbstractRenderer.Current;
                    if (renderer != null)
                    {
                        if (renderProgram is not null)
                        {
                            renderer.SetMaterialUniforms(defaultMat, renderProgram);
                            renderer.ApplyRenderParameters(defaultMat.RenderOptions);
                            if (logGpu)
                                GpuDebug("Material uniforms and render parameters set");
                        }
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

            DumpCulledCommandData(renderPasses, scene, visibleCount);

            if (DebugSettings.ForceCpuIndirectBuild)
            {
                if (logGpu)
                    GpuDebug("Using CPU indirect build path");
                
                uint visibleCommands = visibleCount;
                if (visibleCommands == 0)
                {
                    visibleCommands = Math.Min(scene.TotalCommandCount, culledCapacity);
                }

                if (visibleCommands == 0)
                    visibleCommands = Math.Min(scene.TotalCommandCount, indirectDrawBuffer.ElementCount);

                if (logGpu)
                    GpuDebug("CPU build: visibleCommands={0}", visibleCommands);

                uint built = BuildIndirectCommandsCpu(renderPasses, scene, indirectDrawBuffer, visibleCommands, currentRenderPass, null);

                if (built == 0)
                {
                    Debug.LogWarning("CPU indirect build produced zero draw commands. Skipping indirect draw dispatch.");
                    return;
                }

                if (logGpu)
                    GpuDebug("CPU indirect build generated {0} draw command(s) (requested {1}).", built, visibleCommands);

                DispatchRenderIndirect(
                    indirectDrawBuffer,
                    vaoRenderer,
                    built,
                    built,
                    null,
                    renderProgram,
                    camera,
                    modelMatrix);

                if (logGpu)
                    GpuDebug("=== RenderTraditional END (CPU path) ===");
                return;
            }

            if (logGpu)
                GpuDebug("Using GPU indirect build path");

            // Ensure the program actually contains a compute shader stage
            var mask = _indirectCompProgram.GetShaderTypeMask();
            if (logGpu)
                GpuDebug("Compute program shader mask: {0}", mask);
            
            if ((mask & EProgramStageMask.ComputeShaderBit) == 0)
            {
                Debug.LogWarning("Traditional rendering program does not contain a compute shader. Cannot dispatch compute.");
                return;
            }

            // Use traditional compute shader program
            _indirectCompProgram.Use();
            if (logGpu)
                GpuDebug("Compute program bound");

            // Input: culled commands
            _indirectCompProgram.BindBuffer(culledBuffer, 0);
            if (logGpu)
                GpuDebug("Bound culled commands buffer: elements={0}", culledBuffer.ElementCount);

            // Output: indirect draw commands
            _indirectCompProgram.BindBuffer(indirectDrawBuffer, 1);
            if (logGpu)
                GpuDebug("Bound indirect draw buffer: elements={0}", indirectDrawBuffer.ElementCount);

            // Input: mesh data
            _indirectCompProgram.BindBuffer(meshDataBuffer, 2);
            if (logGpu)
                GpuDebug("Bound mesh data buffer: elements={0}", meshDataBuffer.ElementCount);

            // Input: culled draw count written during the culling stage (std430 binding = 3)
            var culledCountBuffer = renderPasses.CulledCountBuffer;
            if (culledCountBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(culledCountBuffer, 3);
                if (logGpu)
                    GpuDebug("Bound culled count buffer");
            }

            // Optional: GPU-visible draw count buffer consumed by glMultiDraw*Count (std430 binding = 4)
            if (parameterBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(parameterBuffer, 4);
                if (logGpu)
                    GpuDebug("Bound parameter buffer");
            }

            // Optional: overflow/truncation/stat buffers (std430 bindings = 5, 7, 8)
            var indirectOverflowFlagBuffer = renderPasses.IndirectOverflowFlagBuffer;
            if (indirectOverflowFlagBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(indirectOverflowFlagBuffer, 5);
                if (logGpu)
                    GpuDebug("Bound overflow flag buffer");
            }

            var truncationFlagBuffer = renderPasses.TruncationFlagBuffer;
            if (truncationFlagBuffer is not null)
            {
                _indirectCompProgram.BindBuffer(truncationFlagBuffer, 7);
                if (logGpu)
                    GpuDebug("Bound truncation flag buffer");
            }

            var statsBuffer = renderPasses.StatsBuffer;
            bool statsEnabled = statsBuffer is not null;
            if (statsEnabled)
            {
                _indirectCompProgram.BindBuffer(statsBuffer!, 8);
                if (logGpu)
                    GpuDebug("Bound stats buffer");
            }

            _indirectCompProgram.Uniform("StatsEnabled", statsEnabled ? 1u : 0u);

            if (visibleCount == 0)
            {
                if (logGpu)
                    GpuDebug("VisibleCommandCount == 0; skipping GPU indirect build path.");
                return;
            }

            // Set uniforms
            _indirectCompProgram.Uniform("CurrentRenderPass", currentRenderPass);
            _indirectCompProgram.Uniform("MaxIndirectDraws", (int)indirectDrawBuffer.ElementCount);
            if (logGpu)
                GpuDebug("Set uniforms: CurrentRenderPass={0}, MaxIndirectDraws={1}", currentRenderPass, indirectDrawBuffer.ElementCount);

            uint dispatchCount = visibleCount;
            if (logGpu)
                GpuDebug("Dispatch command count: {0}", dispatchCount);

            // Dispatch compute shader
            uint groupSize = 32; // Should match local_size_x in shader
            (uint groupsX, uint groupsY, uint groupsZ) = ComputeDispatch.ForCommands(dispatchCount, groupSize);

            if (logGpu)
                GpuDebug("Dispatching compute: groups=({0},{1},{2}) groupSize={3}", groupsX, groupsY, groupsZ, groupSize);
            _indirectCompProgram.DispatchCompute(groupsX, groupsY, groupsZ, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            //Debug.Out("Compute dispatch complete");

            // Conservative barrier before consuming indirect buffer
            AbstractRenderer.Current?.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.Command |
                EMemoryBarrierMask.ClientMappedBuffer |
                EMemoryBarrierMask.BufferUpdate);
            //Debug.Out("Memory barrier issued");

            //if (DebugSettings.DumpIndirectArguments)
                DumpGpuIndirectArguments(renderPasses, indirectDrawBuffer, maxDrawAllowed, parameterBuffer, visibleCount);

            //ClearIndirectTail(indirectDrawBuffer, parameterBuffer, maxDrawAllowed);

            if (logGpu)
                GpuDebug("Dispatching indirect render: program={0}", renderProgram != null ? "valid" : "NULL");
            
            // Use the graphics program obtained at the start of the method
            DispatchRenderIndirect(
                indirectDrawBuffer,
                vaoRenderer,
                visibleCount,
                maxDrawAllowed,
                parameterBuffer,
                renderProgram,
                camera,
                modelMatrix);

            if (logGpu)
                GpuDebug("=== RenderTraditional END (GPU path) ===");
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
                    GpuDebug("CPU indirect build found no commands in culled buffer.");
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
                        GpuDebug($"CPU indirect skip[{i}] reason={skipReason}");
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
                    // Match GPURenderIndirect.comp: baseInstance encodes the culled-command index.
                    BaseInstance = i
                };

                indirectDrawBuffer.SetDataRawAtIndex(written, drawCmd);
                materialOrder?.Add(gpuCommand.MaterialID);

                if (DebugSettings.DumpIndirectArguments && written < 8)
                {
                    GpuDebug($"CPU indirect[{written}] mesh={gpuCommand.MeshID} submesh={gpuCommand.SubmeshID & 0xFFFF} count={drawCmd.Count} firstIndex={drawCmd.FirstIndex} baseVertex={drawCmd.BaseVertex} material={gpuCommand.MaterialID}");
                }

                written++;
            }

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint byteLength = stride * written;
            if (byteLength > 0)
                indirectDrawBuffer.PushSubData(0, byteLength);

            if (DebugSettings.DumpIndirectArguments)
            {
                GpuDebug($"CPU indirect build final count={written} (requested {requestedCount}, buffer cap {indirectDrawBuffer.ElementCount}).");

                if (sampleLines is not null && sampleLines.Count > 0)
                {
                    foreach (string line in sampleLines)
                        GpuDebug(line);
                }

                if (passStats is not null && passStats.Count > 0)
                {
                    var histogram = passStats
                        .OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"pass={kvp.Key} seen={kvp.Value.Total} emitted={kvp.Value.Emitted}");
                    GpuDebug("CPU indirect pass histogram: " + string.Join(", ", histogram));
                }

                if (skipBuckets is not null && skipBuckets.Count > 0)
                {
                    var skipSummary = skipBuckets
                        .OrderByDescending(kvp => kvp.Value)
                        .Select(kvp => $"{kvp.Key}={kvp.Value}");
                    GpuDebug("CPU indirect skip reasons: " + string.Join(", ", skipSummary));
                }
            }

            GpuDebug($"HybridRenderingManager.BuildIndirectCommandsCpu: Built {written} indirect draw commands from {totalCommands} culled commands");

            return written;
        }

        // Ensure or create a combined graphics program for the given material ID (MVP: combined program only)
        private XRRenderProgram? EnsureCombinedProgram(uint materialID, XRMaterial material, XRMeshRenderer? vaoRenderer)
        {
            GpuDebug($"=== EnsureCombinedProgram: materialID={materialID} ===");
            
            int rendererKey = vaoRenderer is null ? 0 : RuntimeHelpers.GetHashCode(vaoRenderer);
            if (_materialPrograms.TryGetValue((materialID, rendererKey), out var existing))
            {
                //Debug.Out("Using cached program");
                return existing.Program;
            }

            GpuDebug($"Creating new program for material: {material.Name ?? "<unnamed>"}");
            GpuDebug($"Material has {material.Shaders.Count} shaders");

            var shaderList = new List<XRShader>(material.Shaders.Where(shader => shader is not null));
            GpuDebug($"Non-null shaders: {shaderList.Count}");
            
            foreach (var shader in shaderList)
            {
                GpuDebug($"  Shader type: {shader.Type}");
            }

            // For GPU-driven indirect rendering we need a vertex shader that fetches the per-draw world matrix
            // from the culled commands buffer (indexed by gl_BaseInstance). Material-provided vertex shaders
            // generally assume CPU-driven per-object uniforms.
            for (int i = shaderList.Count - 1; i >= 0; --i)
            {
                if (shaderList[i].Type == EShaderType.Vertex)
                    shaderList.RemoveAt(i);
            }

            XRShader? generatedVertexShader = CreateGpuIndirectVertexShader(vaoRenderer);
            if (generatedVertexShader is not null)
                shaderList.Add(generatedVertexShader);

            GpuDebug($"Final shader list count: {shaderList.Count}");
            //Debug.Out("Creating and linking program...");
            
            var program = new XRRenderProgram(linkNow: false, separable: false, shaderList);
            program.AllowLink();
            program.Link();
            
            if (program is null)
            {
                Debug.LogWarning("Failed to create render program for material; skipping cache.");
                return null;
            }

            _materialPrograms[(materialID, rendererKey)] = new MaterialProgramCache(program, generatedVertexShader);
            //Debug.Out("Program cached");
            
            return program;
        }

        private XRShader? CreateGpuIndirectVertexShader(XRMeshRenderer? vaoRenderer)
        {
            // Build a vertex shader compatible with the engine's default fragment shader expectations,
            // but sourcing ModelMatrix from the culled command buffer via gl_BaseInstance.
            var sb = new StringBuilder();
            sb.AppendLine("#version 460");
            sb.AppendLine();
            sb.AppendLine("// GPU indirect: per-draw command data (float[48]) bound at SSBO binding=0");
            sb.AppendLine("layout(std430, binding = 0) buffer CulledCommandsBuffer { float culled[]; };");
            sb.AppendLine("const int COMMAND_FLOATS = 48;");
            sb.AppendLine();

            uint location = 0;
            sb.AppendLine($"layout(location={location++}) in vec3 {ECommonBufferType.Position};");

            bool hasNormals = HasRendererBuffer(vaoRenderer, ECommonBufferType.Normal.ToString());
            bool hasTangents = HasRendererBuffer(vaoRenderer, ECommonBufferType.Tangent.ToString());

            if (hasNormals)
                sb.AppendLine($"layout(location={location++}) in vec3 {ECommonBufferType.Normal};");
            if (hasTangents)
                sb.AppendLine($"layout(location={location++}) in vec4 {ECommonBufferType.Tangent};");

            var texCoordBindings = GetRendererBuffersWithPrefix(vaoRenderer, ECommonBufferType.TexCoord.ToString());
            foreach (string binding in texCoordBindings)
                sb.AppendLine($"layout(location={location++}) in vec2 {binding};");

            var colorBindings = GetRendererBuffersWithPrefix(vaoRenderer, ECommonBufferType.Color.ToString());
            foreach (string binding in colorBindings)
                sb.AppendLine($"layout(location={location++}) in vec4 {binding};");

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
            sb.AppendLine();

            sb.AppendLine($"uniform mat4 {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.InverseViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform bool {EEngineUniform.VRMode};");
            sb.AppendLine();

            sb.AppendLine("mat4 LoadWorldMatrix(uint commandIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    int base = int(commandIndex) * COMMAND_FLOATS;");
            sb.AppendLine("    // Matrix4x4 is row-major in CPU memory; construct GLSL mat4 by columns.");
            sb.AppendLine("    vec4 c0 = vec4(culled[base+0], culled[base+4], culled[base+8],  culled[base+12]);");
            sb.AppendLine("    vec4 c1 = vec4(culled[base+1], culled[base+5], culled[base+9],  culled[base+13]);");
            sb.AppendLine("    vec4 c2 = vec4(culled[base+2], culled[base+6], culled[base+10], culled[base+14]);");
            sb.AppendLine("    vec4 c3 = vec4(culled[base+3], culled[base+7], culled[base+11], culled[base+15]);");
            sb.AppendLine("    return mat4(c0, c1, c2, c3);");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine("    mat4 ModelMatrix = LoadWorldMatrix(uint(gl_BaseInstance));");
            sb.AppendLine("    vec4 localPos = vec4(Position, 1.0);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragPosLocalName} = localPos.xyz;");
            sb.AppendLine($"    mat4 viewMatrix = {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine("    vec4 worldPos = ModelMatrix * localPos;");
            sb.AppendLine($"    vec4 clipPos = {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix} * viewMatrix * worldPos;");
            sb.AppendLine($"    if ({EEngineUniform.VRMode})");
            sb.AppendLine("        FragPos = worldPos.xyz;");
            sb.AppendLine("    else");
            sb.AppendLine("        FragPos = clipPos.xyz / max(clipPos.w, 1e-6);");
            sb.AppendLine();

            if (hasNormals || hasTangents)
                sb.AppendLine("    mat3 normalMatrix = transpose(inverse(mat3(ModelMatrix)));");

            if (hasNormals)
            {
                sb.AppendLine("    FragNorm = normalize(normalMatrix * Normal);");
                if (hasTangents)
                {
                    sb.AppendLine("    FragTan = normalize(normalMatrix * Tangent.xyz);");
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

            return new XRShader(EShaderType.Vertex, sb.ToString())
            {
                Name = "GPUIndirect_AutoVS"
            };
        }

        private XRShader? CreateDefaultVertexShader(XRMeshRenderer? vaoRenderer)
        {
            XRShader? generatedVS = null;
            var mesh = vaoRenderer?.Mesh;
            if (mesh is not null)
            {
                GpuDebug($"Generating vertex shader from mesh: {mesh.Name ?? "<unnamed>"}");
                var gen = new DefaultVertexShaderGenerator(mesh)
                {
                    WriteGLPerVertexOutStruct = false
                };
                string vertexShaderSource = gen.Generate();
                GpuDebug($"Generated vertex shader ({vertexShaderSource.Length} chars)");
                generatedVS = new XRShader(EShaderType.Vertex, vertexShaderSource)
                {
                    Name = (mesh.Name ?? "Generated") + "_AutoVS"
                };
            }
            else
            {
                GpuDebug("No mesh available - using fallback vertex shader");
                string fallbackSource = BuildFallbackVertexShader(vaoRenderer);
                GpuDebug($"Generated fallback vertex shader ({fallbackSource.Length} chars)");
                generatedVS = new XRShader(EShaderType.Vertex, fallbackSource)
                {
                    Name = "FallbackGeneratedVS"
                };
            }

            return generatedVS;
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
            // ViewMatrix is the actual view transform (camera.Transform.InverseRenderMatrix)
            // InverseViewMatrix is the camera's world transform (camera.Transform.RenderMatrix), kept for compatibility
            sb.AppendLine($"uniform mat4 {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.InverseViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform bool {EEngineUniform.VRMode};");

            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine("    vec4 localPos = vec4(Position, 1.0);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragPosLocalName} = localPos.xyz;");
            // Use ViewMatrix uniform directly instead of computing inverse() in shader
            // This ensures the same precision as the motion vectors fragment shader
            sb.AppendLine($"    mat4 viewMatrix = {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
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

            Matrix4x4 defaultModelMatrix = Matrix4x4.Identity;
            if (renderPasses.VisibleCommandCount > 0 &&
                TryReadWorldMatrix(renderPasses.CulledSceneToRenderBuffer, 0, out Matrix4x4 firstWorldMatrix))
            {
                defaultModelMatrix = firstWorldMatrix;
            }

            XRDataBuffer? dispatchParameterBuffer = parameterBuffer;
            //uint cpuBuiltCount = 0;
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

            if (materialMap.Count > 0)
            {
                string[] sample = materialMap
                    .Select(kvp => $"{kvp.Key}:{kvp.Value?.Name ?? "<null>"}")
                    .Take(16)
                    .ToArray();
                GpuDebug($"MaterialMap snapshot ({materialMap.Count} entries){(materialMap.Count > sample.Length ? " (truncated)" : string.Empty)}: {string.Join(", ", sample)}");
            }
            else
            {
                GpuDebug("MaterialMap snapshot: empty");
            }

            if (DebugSettings.DumpIndirectArguments)
            {
                AbstractRenderer.Current?.MemoryBarrier(
                    EMemoryBarrierMask.ShaderStorage |
                    EMemoryBarrierMask.Command |
                    EMemoryBarrierMask.ClientMappedBuffer |
                    EMemoryBarrierMask.BufferUpdate);

                uint visible = renderPasses.VisibleCommandCount;
                uint maxAllowed = indirectDrawBuffer.ElementCount;
                if (activeBatches.Count > 0)
                {
                    var lastBatch = activeBatches[^1];
                    uint batchEnd = lastBatch.Offset + lastBatch.Count;
                    if (batchEnd > 0)
                        maxAllowed = Math.Min(maxAllowed, batchEnd);
                }

                DumpCulledCommandData(renderPasses, scene, visible);
                DumpGpuIndirectArguments(renderPasses, indirectDrawBuffer, maxAllowed, dispatchParameterBuffer, visible);
            }

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

                XRMaterial? overrideMaterial = Engine.Rendering.State.OverrideMaterial;
                bool usingOverrideMaterial = overrideMaterial is not null;

                uint effectiveMaterialId = lookupMaterialId;
                XRMaterial? material = null;

                if (usingOverrideMaterial)
                {
                    material = overrideMaterial;
                    effectiveMaterialId = (uint)overrideMaterial!.GetHashCode();
                }
                else if (lookupMaterialId != 0)
                {
                    materialMap.TryGetValue(lookupMaterialId, out material);
                }

                if (!usingOverrideMaterial && material is null)
                {
                    string reason = lookupMaterialId == 0
                        ? "ID=0 (invalid)"
                        : "material not found in map";
                    GpuDebug($"Material lookup miss for ID={lookupMaterialId} (batch offset={batch.Offset}, count={effectiveCount}): {reason}");
                    material = XRMaterial.InvalidMaterial;
                }

                if (material is null)
                {
                    Debug.LogWarning($"No material for MaterialID={lookupMaterialId}. Skipping batch of {effectiveCount} draws at offset {batch.Offset}.");
                    continue;
                }

                // Ensure/Use graphics program (combined MVP)
                var program = EnsureCombinedProgram(effectiveMaterialId, material, vaoRenderer);
                if (program is null)
                    continue;

                // Set material uniforms
                renderer.SetMaterialUniforms(material, program);
                renderer.ApplyRenderParameters(material.RenderOptions);

                GpuDebug("Batch draw: materialID={0} offset={1} count={2}", effectiveMaterialId, batch.Offset, effectiveCount);

                if (batch.Offset >= renderPasses.VisibleCommandCount)
                {
                    Debug.LogWarning($"Batch offset {batch.Offset} out of range for visible count {renderPasses.VisibleCommandCount}; skipping batch.");
                    continue;
                }

                uint available = renderPasses.VisibleCommandCount - batch.Offset;
                if (effectiveCount > available)
                {
                    Debug.LogWarning($"Clamping batch at offset {batch.Offset} from {effectiveCount} to {available} draw(s) due to visible count bounds.");
                    effectiveCount = available;
                }

                if (effectiveCount == 0)
                    continue;

                Matrix4x4 batchModelMatrix = defaultModelMatrix;
                if (TryReadWorldMatrix(renderPasses.CulledSceneToRenderBuffer, batch.Offset, out Matrix4x4 firstDrawMatrix))
                    batchModelMatrix = firstDrawMatrix;

                DispatchRenderIndirectRange(
                    indirectDrawBuffer,
                    vaoRenderer,
                    renderPasses.CulledSceneToRenderBuffer,
                    batch.Offset,
                    effectiveCount,
                    dispatchParameterBuffer,
                    program,
                    camera,
                    batchModelMatrix);
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
            foreach (var cache in _materialPrograms.Values)
                cache.Program.Destroy();
            _materialPrograms.Clear();
            GC.SuppressFinalize(this);
        }
    }
}