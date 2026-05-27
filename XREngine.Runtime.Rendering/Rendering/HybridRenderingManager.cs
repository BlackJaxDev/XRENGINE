using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Shaders.Generator;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering
{
    /// <summary>
    /// Manages both traditional indirect rendering and modern meshlet-based rendering.
    /// </summary>
    public class HybridRenderingManager : XRBase, IDisposable
    {
        private const uint IndirectCommandSsboBinding = 7;
        private const uint InstanceTransformSsboBinding = GPUBatchingBindings.InstanceTransformBuffer;
        private const uint InstanceSourceIndexSsboBinding = GPUBatchingBindings.InstanceSourceIndexBuffer;
        private const uint MaterialTableSsboBinding = MaterialBindingLayouts.MaterialTableSsboBinding;
        private const uint DrawMetadataSsboBinding = 12;
        private const uint LodTransitionSsboBinding = 16;
        private const uint MaterialTextureHandleTableSsboBinding = MaterialBindingLayouts.MaterialTextureHandleTableSsboBinding;
        private const int IndirectCommandFloatCount = GPUScene.CommandFloatCount;
        private const uint IndirectTextGlyphOffsetSsboBinding = 3;
        private const uint MeshletMeshDataSsboBinding = 3;
        private const uint MeshletDescriptorSsboBinding = 5;
        private const uint MeshletVertexIndexSsboBinding = 6;
        private const uint MeshletTriangleIndexSsboBinding = 7;
        private const uint MeshletTaskRecordSsboBinding = 9;
        private const uint MeshletTaskCountSsboBinding = 10;
        private const uint MeshletAtlasPositionSsboBinding = 13;
        private const uint MeshletAtlasNormalSsboBinding = 14;
        private const uint MeshletAtlasTangentSsboBinding = 15;
        private const uint MeshletMaterialStateSsboBinding = 16;
        private const uint MeshletAtlasUv0SsboBinding = 18;
        private const uint MeshletTransformSsboBinding = 19;
        private const uint MeshletPrevTransformSsboBinding = 20;
        private const uint MeshletStatsSsboBinding = 21;
        private const uint IndirectLegacyBaseInstanceFlag = 0x80000000u;
        private const uint IndirectPreviousLodBaseInstanceFlag = 0x40000000u;
        private const uint IndirectBaseInstanceCommandIndexMask = 0x3FFFFFFFu;
        private const uint MeshletPassDepthPrepass = 1u << 0;
        private const uint MeshletPassShadowDepth = 1u << 1;
        private const uint MeshletPassOpaque = 1u << 2;
        private const uint MeshletPassMasked = 1u << 3;
        private const uint MeshletPassTransparent = 1u << 4;
        private const uint MeshletPassVelocity = 1u << 5;
        private const uint MeshletPassStereo = 1u << 6;
        private const int FragLodTransitionRoleLocation = 23;
        private const string FragLodTransitionRoleName = "XreFragLodTransitionRole";
        private const string GlyphTransformsBufferName = "GlyphTransformsBuffer";
        private const string GlyphTexCoordsBufferName = "GlyphTexCoordsBuffer";
        private const string GlyphRotationsBufferName = "GlyphRotationsBuffer";
        private const string GlyphOffsetsBufferName = "GlyphOffsetsBuffer";
        private const int ZeroReadbackPendingProgramSampleLimit = 6;
        private const int FragMaterialIdLocation = 24;
        private const string FragMaterialIdName = "XRE_FragMaterialId";
        private const int FragStateClassIdLocation = 26;
        private const string FragStateClassIdName = "XRE_FragStateClassId";
        private const int FragMeshletDebugColorLocation = 12;
        private const string FragMeshletDebugColorName = "FragMeshletDebugColor";
        private const string MeshletDebugDisplayUniformName = "EnableMeshletDebugDisplay";
        private static readonly string[] MeshletFrustumPlaneUniformNames =
        [
            "FrustumPlanes[0]",
            "FrustumPlanes[1]",
            "FrustumPlanes[2]",
            "FrustumPlanes[3]",
            "FrustumPlanes[4]",
            "FrustumPlanes[5]",
        ];
        private XRRenderProgram? _indirectCompProgram;

        private bool _useMeshletPipeline = false;
        public bool UseMeshletPipeline
        {
            get => _useMeshletPipeline;
            set => SetField(ref _useMeshletPipeline, value);
        }

    // Cache of graphics programs created per material (combined program MVP)
    private readonly struct MaterialProgramCache(XRRenderProgram program, XRShader? generatedVertexShader, long shaderStateRevision)
        {
        public readonly XRRenderProgram Program = program;
        public readonly XRShader? GeneratedVertexShader = generatedVertexShader;
        public readonly long ShaderStateRevision = shaderStateRevision;
        }

        private readonly struct MaterialTableProgramCache(XRRenderProgram program, XRShader? generatedVertexShader, XRShader fragmentShader)
        {
            public readonly XRRenderProgram Program = program;
            public readonly XRShader? GeneratedVertexShader = generatedVertexShader;
            public readonly XRShader FragmentShader = fragmentShader;
        }

        private readonly struct MeshletMaterialTableProgramCache(XRRenderProgram program, XRShader taskShader, XRShader meshShader, XRShader fragmentShader)
        {
            public readonly XRRenderProgram Program = program;
            public readonly XRShader TaskShader = taskShader;
            public readonly XRShader MeshShader = meshShader;
            public readonly XRShader FragmentShader = fragmentShader;
        }

        private readonly Dictionary<XRRenderProgramDescriptor, MaterialProgramCache> _materialPrograms = [];
        private readonly Dictionary<XRRenderProgramDescriptor, MaterialProgramCache> _pendingMaterialPrograms = [];
        private readonly Dictionary<(uint materialId, int rendererKey), XRRenderProgramDescriptor> _materialProgramUseDescriptors = [];
        private readonly Dictionary<(bool bindless, int rendererKey, string layoutHash), MaterialTableProgramCache> _materialTablePrograms = [];
        private readonly Dictionary<(bool bindless, EMeshShaderDialect dialect, bool skinned, string layoutHash), MeshletMaterialTableProgramCache> _meshletMaterialTablePrograms = [];
        private XRDataBuffer? _indirectTextTransformsBuffer;
        private XRDataBuffer? _indirectTextTexCoordsBuffer;
        private XRDataBuffer? _indirectTextRotationsBuffer;
        private XRDataBuffer? _indirectTextGlyphOffsetsBuffer;
        private bool _indirectTextBuffersNeedFullPush = true;
        private int _bindlessMaterialTableUnsupportedLogBudget = 4;

    private static GPURenderPassCollection.IndirectDebugSettings DebugSettings => GPURenderPassCollection.IndirectDebug;
    private static readonly HashSet<uint> _warnedMultiVertexMaterials = [];
    private static bool IsGpuIndirectLoggingEnabled()
        => RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;
    private static bool IsInstrumentedStrategy(GPURenderPassCollection renderPasses)
        => renderPasses.MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectInstrumented ||
           renderPasses.MeshSubmissionStrategy.IsInstrumentedMeshletStrategy();

    /// <summary>
    /// Logs a GPU indirect debugging message using the new structured logger.
    /// Falls back to legacy Debug.Meshes if needed.
    /// </summary>
    private static void GpuDebug(string message, params object[] args)
    {
        if (!IsEnabled(LogCategory.Draw, LogLevel.Debug))
            return;

        Log(LogCategory.Draw, LogLevel.Debug, string.Format(message, args));
    }

    /// <summary>
    /// Logs a GPU indirect debugging message with FormattableString support.
    /// </summary>
    private static void GpuDebug(FormattableString message)
    {
        if (!IsEnabled(LogCategory.Draw, LogLevel.Debug))
            return;

        Log(LogCategory.Draw, LogLevel.Debug, message.ToString());
    }

        private static XRMaterial? ResolveEffectiveGpuMaterial(XRMaterial? sourceMaterial, XRMaterial? overrideMaterial)
        {
            bool useDepthNormalMaterialVariants =
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState?.UseDepthNormalMaterialVariants ?? false;

            if (!useDepthNormalMaterialVariants)
                return overrideMaterial ?? sourceMaterial;

            XRMaterial? variant = sourceMaterial?.DepthNormalPrePassVariant;
            if (variant is not null)
                return variant;

            return overrideMaterial ?? sourceMaterial;
        }

    /// <summary>
    /// Logs a GPU indirect debugging message for a specific category.
    /// </summary>
    private static void GpuDebug(LogCategory category, string message, params object[] args)
    {
        if (!IsEnabled(category, LogLevel.Debug))
            return;

        Log(category, LogLevel.Debug, string.Format(message, args));
    }

    /// <summary>
    /// Logs a GPU indirect debugging warning for a specific category.
    /// </summary>
    private static void GpuWarn(LogCategory category, string message, params object[] args)
    {
        if (!IsEnabled(category, LogLevel.Warning))
            return;

        Log(category, LogLevel.Warning, string.Format(message, args));
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
                ShaderHelper.LoadEngineShader("Compute/Indirect/GPURenderIndirect.comp", EShaderType.Compute)
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
        /// Runtime snapshot of backend parity-critical indirect rendering decisions.
        /// Used by both dispatch code and tests to validate count/fallback behavior.
        /// </summary>
        public readonly struct IndirectParityChecklist(
            bool hasRenderer,
            bool hasIndirectDrawBuffer,
            bool hasParameterBuffer,
            bool parameterBufferReady,
            bool indexedVaoValid,
            bool supportsIndirectCountDraw,
            bool countDrawPathDisabled,
            string backendName)
        {
            public bool HasRenderer { get; } = hasRenderer;
            public bool HasIndirectDrawBuffer { get; } = hasIndirectDrawBuffer;
            public bool HasParameterBuffer { get; } = hasParameterBuffer;
            public bool ParameterBufferReady { get; } = parameterBufferReady;
            public bool IndexedVaoValid { get; } = indexedVaoValid;
            public bool SupportsIndirectCountDraw { get; } = supportsIndirectCountDraw;
            public bool CountDrawPathDisabled { get; } = countDrawPathDisabled;
            public string BackendName { get; } = backendName;

            public bool DrawIndirectBufferBindingReady
                => HasRenderer && HasIndirectDrawBuffer;

            public bool ParameterBufferBindingReady
                => HasRenderer && HasParameterBuffer && ParameterBufferReady;

            public bool UsesCountDrawPath
                => DrawIndirectBufferBindingReady
                && ParameterBufferBindingReady
                && IndexedVaoValid
                && SupportsIndirectCountDraw
                && !CountDrawPathDisabled;

            public bool UsesFallbackPath
                => DrawIndirectBufferBindingReady
                && IndexedVaoValid
                && !UsesCountDrawPath;

            public bool IsSubmissionReady
                => DrawIndirectBufferBindingReady && IndexedVaoValid;
        }

        public static IndirectParityChecklist BuildIndirectParityChecklist(
            bool hasRenderer,
            bool hasIndirectDrawBuffer,
            bool hasParameterBuffer,
            bool parameterBufferReady,
            bool indexedVaoValid,
            bool supportsIndirectCountDraw,
            bool countDrawPathDisabled,
            string backendName)
            => new(
                hasRenderer,
                hasIndirectDrawBuffer,
                hasParameterBuffer,
                parameterBufferReady,
                indexedVaoValid,
                supportsIndirectCountDraw,
                countDrawPathDisabled,
                backendName);

        private static IndirectParityChecklist BuildIndirectParityChecklist(
            AbstractRenderer? renderer,
            XRDataBuffer? indirectDrawBuffer,
            XRDataBuffer? parameterBuffer,
            XRMeshRenderer.BaseVersion? version)
        {
            bool hasRenderer = renderer is not null;
            bool hasIndirectDrawBuffer = indirectDrawBuffer is not null;
            bool hasParameterBuffer = parameterBuffer is not null;
            bool parameterBufferReady = hasParameterBuffer && EnsureParameterBufferReady(parameterBuffer!);
            bool indexedVaoValid = hasRenderer && renderer!.ValidateIndexedVAO(version);
            bool supportsIndirectCountDraw = hasRenderer && renderer!.SupportsIndirectCountDraw();
            bool countDrawPathDisabled = DebugSettings.DisableCountDrawPath;
            string backendName = renderer?.GetType().Name ?? "None";

            return BuildIndirectParityChecklist(
                hasRenderer,
                hasIndirectDrawBuffer,
                hasParameterBuffer,
                parameterBufferReady,
                indexedVaoValid,
                supportsIndirectCountDraw,
                countDrawPathDisabled,
                backendName);
        }

        private static void LogIndirectParityChecklist(in IndirectParityChecklist checklist)
        {
            if (!IsEnabled(LogCategory.Validation, LogLevel.Info))
                return;

            Log(
                LogCategory.Validation,
                LogLevel.Info,
                "Indirect parity backend={0} drawBufferReady={1} paramBufferReady={2} supportsCount={3} countDisabled={4} indexedVaoValid={5} path={6}",
                checklist.BackendName,
                checklist.DrawIndirectBufferBindingReady,
                checklist.ParameterBufferBindingReady,
                checklist.SupportsIndirectCountDraw,
                checklist.CountDrawPathDisabled,
                checklist.IndexedVaoValid,
                checklist.UsesCountDrawPath ? "CountDraw" : "Fallback");
        }

        // ===== C-GPU-2: Zero-readback production invariant assertions =====
        // Real flag chain (NOT _isDrawCountAllowedToBeFromGpu as the older notes said):
        //   IndirectParityChecklist.UsesCountDrawPath
        //     = DrawIndirectBufferBindingReady
        //     & ParameterBufferBindingReady
        //     & IndexedVaoValid
        //     & SupportsIndirectCountDraw
        //     & !DebugSettings.DisableCountDrawPath
        // Under zero-readback GPU strategies the GPU count-draw path MUST be
        // active. Any diagnostic flag (DisableCountDrawPath, ForceCpuFallbackCount,
        // ForceCpuIndirectBuild, !DisableCpuReadbackCount, EnableCpuBatching) that defeats it is a
        // shipping bug. These asserts compile out of Release.

        [System.Diagnostics.Conditional("DEBUG")]
        private static void AssertZeroReadbackUsesGpuCountPath(in IndirectParityChecklist parity, string callsite)
        {
            EMeshSubmissionStrategy strategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
            if (!strategy.IsGpuZeroReadbackStrategy())
                return;

            if (parity.UsesCountDrawPath)
                return;

            string reason = !parity.HasRenderer ? "no renderer"
                : !parity.HasIndirectDrawBuffer ? "no indirect-draw buffer"
                : !parity.HasParameterBuffer ? "no parameter buffer"
                : !parity.ParameterBufferReady ? "parameter buffer not ready (map state)"
                : !parity.IndexedVaoValid ? "indexed VAO invalid"
                : !parity.SupportsIndirectCountDraw ? $"renderer {parity.BackendName} reports SupportsIndirectCountDraw=false"
                : parity.CountDrawPathDisabled ? "DebugSettings.DisableCountDrawPath=true (diagnostic switch must be OFF in production)"
                : "unknown";

            System.Diagnostics.Debug.Assert(false,
                $"[C-GPU-2] {callsite}: {strategy} requires UsesCountDrawPath but parity={reason}. " +
                "Shipping path must consume the GPU-written count buffer via glMultiDrawElementsIndirectCount.");
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void AssertZeroReadbackProductionInvariants(string callsite)
        {
            EMeshSubmissionStrategy strategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
            if (!strategy.IsGpuZeroReadbackStrategy())
                return;

            var d = GPURenderPassCollection.IndirectDebug;

            System.Diagnostics.Debug.Assert(!d.DisableCountDrawPath,
                $"[C-GPU-2] {callsite}: IndirectDebug.DisableCountDrawPath=true while strategy={strategy}. " +
                "Diagnostic switch must be OFF in production — it forces a CPU-readback fallback that breaks zero-readback.");

            System.Diagnostics.Debug.Assert(!d.ForceCpuFallbackCount,
                $"[C-GPU-2] {callsite}: IndirectDebug.ForceCpuFallbackCount=true while strategy={strategy}. " +
                "Diagnostic switch must be OFF in production.");

            System.Diagnostics.Debug.Assert(!d.ForceCpuIndirectBuild,
                $"[C-GPU-2] {callsite}: IndirectDebug.ForceCpuIndirectBuild=true while strategy={strategy}. " +
                "Diagnostic switch must be OFF in production.");

            System.Diagnostics.Debug.Assert(d.DisableCpuReadbackCount,
                $"[C-GPU-2] {callsite}: IndirectDebug.DisableCpuReadbackCount=false while strategy={strategy}. " +
                "Production zero-readback must suppress map/unmap of the GPU count buffer.");

            System.Diagnostics.Debug.Assert(!d.EnableCpuBatching,
                $"[C-GPU-2] {callsite}: IndirectDebug.EnableCpuBatching=true while strategy={strategy}. " +
                "Diagnostic switch must be OFF in production — it forces a CPU map of the culled command buffer.");
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
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.HybridRender");

            if (camera is null || scene is null)
                return;

            bool meshletStrategy = renderPasses.MeshSubmissionStrategy.IsAnyMeshletStrategy();
            if (meshletStrategy)
            {
                if (_useMeshletPipeline &&
                    TryRenderMeshletMaterialTable(renderPasses, camera, scene, currentRenderPass))
                {
                    return;
                }

                if (!_useMeshletPipeline)
                {
                    WarnMeshletMaterialFallback(
                        currentRenderPass,
                        renderPasses.MeshSubmissionStrategy,
                        "Meshlet strategy reached the render manager without meshlet pipeline intent.");
                }

                return;
            }

            if (renderPasses.MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback &&
                !renderPasses.ZeroReadbackMaterialScatterPreparedThisFrame)
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordForbiddenGpuFallback(1);
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.ZeroReadbackScatterMissing.{currentRenderPass}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] {0} mesh submission for pass {1} could not prepare material-tier scatter. Skipping CPU fallback.",
                    renderPasses.MeshSubmissionStrategy,
                    currentRenderPass);
                return;
            }

            // Material map from scene (ID -> XRMaterial)
            var materialMap = renderPasses.GetMaterialMap(scene);

            if (renderPasses.ZeroReadbackMaterialScatterPreparedThisFrame)
            {
                GPURenderPassCollection.Crumb($"ZeroPath.BEGIN pass={currentRenderPass} path={renderPasses.ZeroReadbackMaterialDrawPath}");
                switch (renderPasses.ZeroReadbackMaterialDrawPath)
                {
                    case EZeroReadbackMaterialDrawPath.ActiveBucketList:
                        RenderZeroReadbackActiveMaterialBuckets(
                            renderPasses,
                            camera,
                            scene,
                            vaoRenderer,
                            currentRenderPass,
                            materialMap);
                        break;
                    case EZeroReadbackMaterialDrawPath.MaterialTable:
                        RenderZeroReadbackMaterialTableBuckets(
                            renderPasses,
                            camera,
                            scene,
                            vaoRenderer,
                            currentRenderPass,
                            bindless: false);
                        break;
                    case EZeroReadbackMaterialDrawPath.BindlessMaterialTable:
                        RenderZeroReadbackMaterialTableBuckets(
                            renderPasses,
                            camera,
                            scene,
                            vaoRenderer,
                            currentRenderPass,
                            bindless: true);
                        break;
                    default:
                        RenderZeroReadbackMaterialTiers(
                            renderPasses,
                            camera,
                            scene,
                            vaoRenderer,
                            currentRenderPass,
                            materialMap);
                        break;
                }
                GPURenderPassCollection.Crumb($"ZeroPath.END pass={currentRenderPass} path={renderPasses.ZeroReadbackMaterialDrawPath}");
                return;
            }

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
            if (!IsEnabled(LogCategory.Draw, LogLevel.Info))
                return;

            string path = useCount ? "IndirectCount" : (offset.HasValue ? "IndirectWithOffset" : "Indirect");
            string msg = offset.HasValue
                ? $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride} byteOffset={offset.Value}"
                : $"GPU-Indirect path={path} count/max={drawCountOrMax} stride={stride}";
            Log(LogCategory.Draw, LogLevel.Info, msg);
        }

        // Set by XRE_GL_DEBUG=1 — enables verbose dumps of indirect-draw parameters
        // immediately before each glMultiDrawElementsIndirect[Count] call. The dump
        // is the only way to localize an offending pass when the NVIDIA driver
        // FAST_FAIL_CORRUPT_LIST_ENTRYs without a debug callback message.
        private static bool s_glDebugTraceEnabled => RenderDiagnosticsFlags.GLDebug;

        private static void LogIndirectDrawSizes(
            string callsite,
            uint maxDrawCount,
            uint stride,
            XRDataBuffer indirectDrawBuffer,
            XRDataBuffer? parameterBuffer,
            nuint indirectByteOffset = 0,
            nuint countByteOffset = 0)
        {
            if (!s_glDebugTraceEnabled)
                return;

            ulong indirectBytes = indirectDrawBuffer.Length;
            ulong indirectCapacityDraws = stride > 0 ? indirectBytes / stride : 0UL;
            ulong requestedIndirectBytes = (ulong)indirectByteOffset + ((ulong)maxDrawCount * stride);
            ulong paramBytes = parameterBuffer?.Length ?? 0UL;
            ulong requestedCountBytes = (ulong)countByteOffset + 4UL; // 1 GLuint
            bool indirectOverflow = requestedIndirectBytes > indirectBytes;
            bool countOverflow = parameterBuffer is not null && requestedCountBytes > paramBytes;

            Debug.Rendering(
                "[GLDbg] {0}: maxDraw={1} stride={2} indirect(len={3} cap={4} off={5} need={6}{7}) param(len={8} off={9}{10})",
                callsite,
                maxDrawCount,
                stride,
                indirectBytes,
                indirectCapacityDraws,
                indirectByteOffset,
                requestedIndirectBytes,
                indirectOverflow ? " OVERFLOW" : string.Empty,
                paramBytes,
                countByteOffset,
                countOverflow ? " OVERFLOW" : string.Empty);
        }

        private static List<DrawBatch> CoalesceContiguousBatches(IReadOnlyList<DrawBatch> batches)
        {
            if (batches.Count <= 1)
                return [.. batches];

            List<DrawBatch> merged = new(batches.Count);
            DrawBatch current = batches[0];

            for (int index = 1; index < batches.Count; index++)
            {
                DrawBatch next = batches[index];
                bool contiguous = current.Offset + current.Count == next.Offset;
                bool sameMaterial = current.MaterialID == next.MaterialID;

                if (contiguous && sameMaterial)
                {
                    current = new DrawBatch(current.Offset, current.Count + next.Count, current.MaterialID);
                    continue;
                }

                merged.Add(current);
                current = next;
            }

            merged.Add(current);
            return merged;
        }

        private static bool TryReadDrawCount(XRDataBuffer? parameterBuffer, out uint drawCount)
        {
            drawCount = 0u;
            if (parameterBuffer is null)
                return false;

            try
            {
                drawCount = parameterBuffer.GetDataRawAtIndex<uint>(0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ClearIndirectTail(XRDataBuffer indirectDrawBuffer, uint drawCount, uint maxCommands)
        {
            if (maxCommands == 0 || drawCount >= maxCommands)
                return;

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint staleCount = maxCommands - drawCount;
            ulong byteOffset = (ulong)drawCount * stride;
            ulong byteLength = (ulong)staleCount * stride;

            if (byteLength == 0 || byteOffset > int.MaxValue || byteLength > uint.MaxValue)
            {
                Debug.MeshesWarning($"Skipping indirect tail clear: offset={byteOffset} length={byteLength} exceeds CPU copy limits.");
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
            XRDataBuffer? culledCommandsBuffer,
            XRDataBuffer? drawMetadataBuffer,
            XRDataBuffer? lodTransitionBuffer,
            XRDataBuffer? instanceTransformBuffer,
            XRDataBuffer? instanceSourceIndexBuffer,
            bool useInstanceTransformBuffer,
            uint drawCount,
            uint maxCommands,
            XRDataBuffer? parameterBuffer,
            XRRenderProgram? graphicsProgram,
            XRCamera? camera,
            bool allowDrawCountReadback,
            Matrix4x4 modelMatrix)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchRenderIndirect");
            using var timing = BeginTiming("DispatchRenderIndirect");
            bool logGpu = IsEnabled(LogCategory.Draw, LogLevel.Debug);
            
            LogDispatchStart("RenderIndirect", drawCount, maxCommands);
            GpuDebug(LogCategory.Draw, "graphicsProgram={0}, camera={1}", 
                graphicsProgram != null ? "present" : "NULL",
                camera != null ? "present" : "null");
            
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Warn(LogCategory.Draw, "No active renderer found for indirect draw.");
                return;
            }

            if (indirectDrawBuffer is null || maxCommands == 0)
            {
                Warn(LogCategory.Draw, "Invalid dispatch state: buffer={0}, maxCommands={1}",
                    indirectDrawBuffer != null ? "present" : "null", maxCommands);
                return;
            }

            // Bind graphics program for rendering (vertex/fragment shaders)
            if (graphicsProgram is not null)
            {
                LogShaderBind(graphicsProgram.GetType().Name);
                if (!TryUseIndirectGraphicsProgram(graphicsProgram, "RenderIndirect"))
                    return;

                // Bind compact per-draw command data plus SoA metadata/transform buffers.
                // Dedicated bindings avoid colliding with material SSBOs (for example text glyph buffers).
                culledCommandsBuffer?.BindTo(graphicsProgram, IndirectCommandSsboBinding);
                drawMetadataBuffer?.BindTo(graphicsProgram, DrawMetadataSsboBinding);
                lodTransitionBuffer?.BindTo(graphicsProgram, LodTransitionSsboBinding);
                instanceTransformBuffer?.BindTo(graphicsProgram, InstanceTransformSsboBinding);
                instanceSourceIndexBuffer?.BindTo(graphicsProgram, InstanceSourceIndexSsboBinding);
                graphicsProgram.Uniform("UseInstanceTransformBuffer", useInstanceTransformBuffer ? 1 : 0);
                
                if (camera is not null)
                {
                    GpuDebug(LogCategory.Uniforms, "Setting engine uniforms...");
                    renderer.SetEngineUniforms(graphicsProgram, camera);
                }
                else
                {
                    Warn(LogCategory.Draw, "No camera provided for uniforms!");
                }

                LogUniformMatrix(EEngineUniform.ModelMatrix.ToStringFast(), modelMatrix);
                graphicsProgram.Uniform(EEngineUniform.ModelMatrix.ToStringFast(), modelMatrix);
            }
            else
            {
                Error(LogCategory.Draw, "No graphics program bound for indirect rendering. Rendering WILL FAIL.");
                return; // Don't proceed without a program
            }

            // Bind the provided VAO (if any)
            var version = vaoRenderer?.GetDefaultVersion();
            LogVAOBind(vaoRenderer?.GetType().Name, version != null);
            renderer.BindVAOForRenderer(version);

            // Configure VAO attributes for the bound program
            if (graphicsProgram is not null && vaoRenderer is not null)
            {
                GpuDebug(LogCategory.VAO, "Configuring VAO attributes for program...");
                renderer.ConfigureVAOAttributesForProgram(graphicsProgram, version);
            }

            // Validate element buffer presence (required for *ElementsIndirect* variants)
            IndirectParityChecklist parity = BuildIndirectParityChecklist(renderer, indirectDrawBuffer, parameterBuffer, version);

            if (!parity.IndexedVaoValid)
            {
                Warn(LogCategory.VAO, "Indirect draw aborted: no index (element) buffer bound to VAO. Skipping MultiDrawElementsIndirect.");
                renderer.BindVAOForRenderer(null);
                return;
            }
            
            if (logGpu)
            {
                // Enhanced diagnostics: log VAO details and index buffer info
                if (renderer.TryGetIndexBufferInfo(version, out var indexSize, out var indexCount))
                {
                    LogVAOValidation(true, indexSize, indexCount);
                }
                else
                {
                    LogVAOValidation(true);
                }
            }

            LogBufferBind("IndirectDrawBuffer", "DrawIndirect");
            using (RuntimeEngine.Profiler.Start("GpuIndirect.BindDrawIndirectBuffer"))
                renderer.BindDrawIndirectBuffer(indirectDrawBuffer);

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            bool useCount = parity.UsesCountDrawPath;

            LogIndirectParityChecklist(parity);

            // C-GPU-2: production zero-readback must consume the GPU count buffer directly.
            AssertZeroReadbackProductionInvariants("DispatchRenderIndirect");
            AssertZeroReadbackUsesGpuCountPath(parity, "DispatchRenderIndirect");

            GpuDebug(LogCategory.Draw, "Draw mode: useCount={0}, stride={1}", useCount, stride);

            if (DebugSettings.ValidateBufferLayouts)
            {
                ValidateIndirectBufferState(indirectDrawBuffer, maxCommands, stride);
                LogIndirectBufferValidation(indirectDrawBuffer, maxCommands, stride);
            }

            if (!useCount && !allowDrawCountReadback && parameterBuffer is not null)
            {
                Warn(LogCategory.Draw, "Indirect draw skipped: zero-readback GPU submission requires a count-draw path; refusing to draw a stale indirect-buffer tail.");
                LogBufferUnbind("IndirectDrawBuffer", "DrawIndirect");
                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
                return;
            }

            try
            {
                if (useCount)
                {
                    LogBufferBind("ParameterBuffer", "Parameter");
                    using (RuntimeEngine.Profiler.Start("GpuIndirect.BindParameterBuffer"))
                        renderer.BindParameterBuffer(parameterBuffer!);
                    
                    GpuDebug(LogCategory.Sync, "Issuing memory barrier (ClientMappedBuffer | Command)");
                    using (RuntimeEngine.Profiler.Start("GpuIndirect.Draw.MemoryBarrier"))
                        renderer.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                    
                    LogMultiDrawIndirect(true, maxCommands, stride);
                    LogIndirectDrawSizes("DispatchRenderIndirect", maxCommands, stride, indirectDrawBuffer, parameterBuffer);
                    XREngine.Rendering.Commands.GPURenderPassCollection.Crumb(
                        $"MDIC.BEGIN maxCmd={maxCommands} stride={stride} indCap={indirectDrawBuffer.ElementCount} paramCap={(parameterBuffer?.ElementCount ?? 0u)}");
                    using (RuntimeEngine.Profiler.Start("GpuIndirect.MultiDrawElementsIndirectCount"))
                        renderer.MultiDrawElementsIndirectCount(maxCommands, stride);
                    XREngine.Rendering.Commands.GPURenderPassCollection.Crumb("MDIC.END");
                }
                else
                {
                    if (drawCount == 0 && allowDrawCountReadback && TryReadDrawCount(parameterBuffer, out uint gpuDrawCount))
                        drawCount = gpuDrawCount;

                    if (drawCount == 0)
                        drawCount = maxCommands;

                    if (!DebugSettings.SkipIndirectTailClear && drawCount < maxCommands)
                        ClearIndirectTail(indirectDrawBuffer, drawCount, maxCommands);
                    
                    LogMultiDrawIndirect(false, drawCount, stride);
                    using (RuntimeEngine.Profiler.Start("GpuIndirect.MultiDrawElementsIndirect"))
                        renderer.MultiDrawElementsIndirect(drawCount, stride);
                }

                LogGLErrors(renderer, useCount ? "MultiDrawElementsIndirectCount" : "MultiDrawElementsIndirect");
            }
            finally
            {
                if (useCount)
                {
                    LogBufferUnbind("ParameterBuffer", "Parameter");
                    renderer.UnbindParameterBuffer();
                }

                LogBufferUnbind("IndirectDrawBuffer", "DrawIndirect");
                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
            }
            
            LogDispatchEnd("RenderIndirect", true);
        }

        private static int _oneShotDumpBudget = 3; // dump for first 3 frames

        private static void DumpIndirectCommandsOneShot(
            XRDataBuffer indirectDrawBuffer,
            IReadOnlyList<DrawBatch> activeBatches,
            int currentRenderPass)
        {
            if (_oneShotDumpBudget <= 0)
                return;
            _oneShotDumpBudget--;

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
                return;

            renderer.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.Command |
                EMemoryBarrierMask.ClientMappedBuffer |
                EMemoryBarrierMask.BufferUpdate);

            var sb = new System.Text.StringBuilder();
            sb.Append($"[GPU-DIAG] OneShot pass={currentRenderPass} batches={activeBatches.Count} bufferCapacity={indirectDrawBuffer.ElementCount}");

            // Map the GPU buffer to read actual GPU-written data
            bool mappedHere = false;
            try
            {
                if (indirectDrawBuffer.ActivelyMapping.Count == 0)
                {
                    indirectDrawBuffer.MapBufferData();
                    mappedHere = true;
                }

                VoidPtr mappedPtr = indirectDrawBuffer
                    .GetMappedAddresses()
                    .FirstOrDefault(ptr => ptr.IsValid);

                if (!mappedPtr.IsValid)
                {
                    sb.Append(" MAPPING_FAILED");
                    Debug.Meshes(sb.ToString());
                    return;
                }

                uint stride = indirectDrawBuffer.ElementSize;
                if (stride == 0)
                    stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();

                int sampled = 0;
                int nonZeroCount = 0;
                unsafe
                {
                    byte* basePtr = (byte*)mappedPtr.Pointer;
                    foreach (var batch in activeBatches)
                    {
                        if (sampled >= 12 || batch.Count == 0)
                            break;

                        for (uint j = 0; j < batch.Count && sampled < 12; j++, sampled++)
                        {
                            uint idx = batch.Offset + j;
                            if (idx >= indirectDrawBuffer.ElementCount)
                                break;

                            var cmd = Unsafe.ReadUnaligned<DrawElementsIndirectCommand>(basePtr + (int)(idx * stride));
                            sb.Append($" |[{idx}] cnt={cmd.Count} inst={cmd.InstanceCount} 1st={cmd.FirstIndex} bVtx={cmd.BaseVertex} bInst={cmd.BaseInstance} mat={batch.MaterialID}");
                            if (cmd.Count > 0 && cmd.InstanceCount > 0)
                                nonZeroCount++;
                        }
                    }
                }
                sb.Append($" nonZero={nonZeroCount}/{sampled}");
            }
            catch (Exception ex)
            {
                sb.Append($" ERROR={ex.Message}");
            }
            finally
            {
                if (mappedHere)
                    indirectDrawBuffer.UnmapBufferData();
            }

            Debug.Meshes(sb.ToString());
        }

        private static void DumpGpuIndirectArguments(
            GPURenderPassCollection renderPasses,
            XRDataBuffer indirectDrawBuffer,
            uint maxDrawAllowed,
            XRDataBuffer? parameterBuffer,
            uint visibleCount)
        {
            if (!IsInstrumentedStrategy(renderPasses))
                return;

            if (!IsEnabled(LogCategory.Buffers, LogLevel.Debug))
                return;

            Log(LogCategory.Buffers, LogLevel.Debug, "Dump invoked tick={0}", Environment.TickCount64);
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
                    Debug.MeshesWarning("Failed to map indirect draw buffer for argument dump.");
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
                        Debug.MeshesWarning($"Failed to inspect culled commands: {ex.Message}");
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
                Debug.MeshesWarning($"Failed to dump GPU indirect arguments: {ex.Message}");
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
            if (!IsInstrumentedStrategy(renderPasses))
                return;

            if (!IsEnabled(LogCategory.Culling, LogLevel.Debug) || !DebugSettings.DumpIndirectArguments || visibleCount == 0)
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
                    Warn(LogCategory.Culling, "Failed to map culled buffer for inspection.");
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
                        sb.Append($"visible[{i}] mesh={cmd.MeshID} submesh={cmd.SubmeshID & 0xFFFF} material={cmd.MaterialID} instances={cmd.InstanceCount} pass={cmd.RenderPass}");
                        if (scene.TryGetTransform(cmd.TransformID, out Matrix4x4 transform))
                        {
                            Vector3 translation = transform.Translation;
                            sb.Append($" | worldPos=({translation.X:F3},{translation.Y:F3},{translation.Z:F3})");
                        }
                        else
                        {
                            sb.Append($" | transform=<missing:{cmd.TransformID}>");
                        }
                        if (scene.TryGetMeshDataEntry(cmd.MeshID, out GPUScene.MeshDataEntry meshEntry))
                        {
                            sb.Append($" | meshData indexCount={meshEntry.IndexCount} firstIndex={meshEntry.FirstIndex} firstVertex={meshEntry.FirstVertex}");
                        }
                        else
                        {
                            sb.Append(" | meshData=<missing>");
                        }

                        Log(LogCategory.Culling, LogLevel.Debug, sb.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Error(LogCategory.Culling, "Failed to dump culled command data", ex);
            }
            finally
            {
                if (mappedHere)
                    culledBuffer.UnmapBufferData();
            }
        }

        private static bool TryReadWorldMatrix(
            GPUScene scene,
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
                    return scene.TryGetTransform(command.TransformID, out worldMatrix);
                }
            }
            catch (Exception ex)
            {
                Debug.MeshesWarning($"[GPUIndirect] Failed to read world matrix at index {commandIndex}: {ex.Message}");
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
            XRDataBuffer? drawMetadataBuffer,
            XRDataBuffer? lodTransitionBuffer,
            XRDataBuffer? instanceTransformBuffer,
            XRDataBuffer? instanceSourceIndexBuffer,
            bool useInstanceTransformBuffer,
            uint drawOffset,
            uint drawCount,
            XRDataBuffer? parameterBuffer,
            XRRenderProgram? graphicsProgram,
            XRCamera? camera,
            Matrix4x4 modelMatrix,
            bool emitBarrier = true)
        {
            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.MeshesWarning("No active renderer found for indirect draw.");
                return;
            }

            if (indirectDrawBuffer is null || drawCount == 0)
                return;

            // Bind graphics program for rendering
            if (graphicsProgram is not null)
            {
                if (!TryUseIndirectGraphicsProgram(graphicsProgram, "RenderIndirectRange"))
                    return;

                // Bind compact per-draw command data plus SoA metadata/transform buffers.
                culledCommandsBuffer?.BindTo(graphicsProgram, IndirectCommandSsboBinding);
                drawMetadataBuffer?.BindTo(graphicsProgram, DrawMetadataSsboBinding);
                lodTransitionBuffer?.BindTo(graphicsProgram, LodTransitionSsboBinding);
                instanceTransformBuffer?.BindTo(graphicsProgram, InstanceTransformSsboBinding);
                instanceSourceIndexBuffer?.BindTo(graphicsProgram, InstanceSourceIndexSsboBinding);
                graphicsProgram.Uniform("UseInstanceTransformBuffer", useInstanceTransformBuffer ? 1 : 0);
                
                // Set camera/engine uniforms
                if (camera is not null)
                    renderer.SetEngineUniforms(graphicsProgram, camera);
                // Legacy materials might still reference ModelMatrix; set a sensible default.
                graphicsProgram.Uniform(EEngineUniform.ModelMatrix.ToStringFast(), modelMatrix);
            }
            else
            {
                Debug.MeshesWarning("Indirect range draw aborted: no graphics program was provided.");
                return;
            }

            // Bind VAO
            var version = vaoRenderer?.GetDefaultVersion();
            renderer.BindVAOForRenderer(version);

            // Configure VAO attributes for the bound program
            if (graphicsProgram is not null && vaoRenderer is not null)
                renderer.ConfigureVAOAttributesForProgram(graphicsProgram, version);

            IndirectParityChecklist parity = BuildIndirectParityChecklist(renderer, indirectDrawBuffer, parameterBuffer, version);

            if (!parity.IndexedVaoValid)
            {
                Debug.MeshesWarning("Indirect draw aborted: no element buffer bound to VAO.");
                renderer.BindVAOForRenderer(null);
                return;
            }
            
            // Enhanced diagnostics for batched path
            bool logGpu = IsGpuIndirectLoggingEnabled();
            if (logGpu && renderer.TryGetIndexBufferInfo(version, out var indexSize, out var indexCount))
            {
                GpuDebug("  Batch VAO: indexElementSize={0}, indexCount={1}, batchOffset={2}, batchDrawCount={3}", 
                    indexSize, indexCount, drawOffset, drawCount);
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

            bool usingCountPath = parity.UsesCountDrawPath;
            LogIndirectParityChecklist(parity);

            // C-GPU-2: production zero-readback must consume the GPU count buffer directly.
            AssertZeroReadbackProductionInvariants("DispatchRenderIndirectRange");
            AssertZeroReadbackUsesGpuCountPath(parity, "DispatchRenderIndirectRange");

            try
            {
                if (usingCountPath)
                    renderer.BindParameterBuffer(parameterBuffer!);

                LogIndirectPath(usingCountPath, effectiveDrawCount, stride, (uint)byteOffset);

                // O-7: per-batch MemoryBarrier coalescing.
                // The batch dispatch loop in RenderIndirectBatches issues a single coalesced
                // barrier covering the entire batch loop, so we skip the per-range barrier when
                // emitBarrier=false. Standalone callers still get the original semantics.
                if (emitBarrier && parameterBuffer is not null)
                    renderer.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);

                if (DebugSettings.ValidateBufferLayouts)
                    ValidateIndirectBufferState(indirectDrawBuffer, drawOffset + effectiveDrawCount, stride);

                if (usingCountPath)
                {
                    LogIndirectDrawSizes("DispatchRenderIndirectRange", effectiveDrawCount, stride, indirectDrawBuffer, parameterBuffer, byteOffset);
                    renderer.MultiDrawElementsIndirectCount(effectiveDrawCount, stride, byteOffset, 0);
                }
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
                Debug.MeshesWarning("Parameter buffer has no active API wrappers; disabling count path.");
                return false;
            }

            // Phase 2: do not map/unmap by default. Mapping can cause CPU stalls and isn't needed for
            // MultiDrawElementsIndirectCount / vkCmdDrawIndirectCount-style submission.
            requireMapped = requireMapped || DebugSettings.ForceParameterRemap;

            if (requireMapped)
            {
                if (parameterBuffer.ActivelyMapping.Count == 0)
                {
                    parameterBuffer.MapBufferData();
                    if (parameterBuffer.ActivelyMapping.Count == 0)
                    {
                        Debug.MeshesWarning("Failed to map parameter buffer; falling back to non-count draw path.");
                        return false;
                    }
                }
            }
            // If the buffer is already mapped (e.g. diagnostics), leave it mapped unless forced.

            return true;
        }

        private static void ValidateIndirectBufferState(XRDataBuffer buffer, uint requiredDraws, uint expectedStride)
        {
            if (buffer.ElementSize != expectedStride)
                Debug.MeshesWarning($"Indirect buffer stride mismatch. Expected {expectedStride} bytes per command but buffer reports {buffer.ElementSize}.");

            if (requiredDraws > buffer.ElementCount)
                Debug.MeshesWarning($"Indirect buffer does not have enough commands allocated (required={requiredDraws}, allocated={buffer.ElementCount}).");
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
                Debug.MeshesWarning("Indirect compute program is not initialized for traditional rendering.");
                return;
            }

            var meshDataBuffer = scene.MeshDataBuffer;
            if (meshDataBuffer is null)
            {
                Debug.MeshesWarning("Mesh data buffer is not initialized for traditional rendering.");
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
            if (!DebugSettings.DisableCpuReadbackCount)
            {
                if (visibleCount > 0 && TryReadWorldMatrix(scene, culledBuffer, 0, out Matrix4x4 firstWorldMatrix))
                    modelMatrix = firstWorldMatrix;
            }

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

            XRMaterial? overrideMaterial = RuntimeEngine.Rendering.State.OverrideMaterial;
            if (overrideMaterial is not null && logGpu)
                GpuDebug("Override material active: {0}", overrideMaterial.Name ?? "<unnamed>");

            XRMaterial? defaultSourceMaterial = matMap.Values.FirstOrDefault() ?? XRMaterial.InvalidMaterial;
            XRMaterial? defaultMat = ResolveEffectiveGpuMaterial(defaultSourceMaterial, overrideMaterial) ?? XRMaterial.InvalidMaterial;
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
                    Debug.MeshesWarning("Failed to create graphics program!");
                }
            }
            else
            {
                Debug.MeshesWarning("No default material available!");
            }

            if (IsInstrumentedStrategy(renderPasses))
                DumpCulledCommandData(renderPasses, scene, visibleCount);

            if (IsInstrumentedStrategy(renderPasses) && DebugSettings.ForceCpuIndirectBuild)
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
                    Debug.MeshesWarning("CPU indirect build produced zero draw commands. Skipping indirect draw dispatch.");
                    return;
                }

                if (logGpu)
                    GpuDebug("CPU indirect build generated {0} draw command(s) (requested {1}).", built, visibleCommands);

                DispatchRenderIndirect(
                    indirectDrawBuffer,
                    vaoRenderer,
                    culledBuffer,
                    scene.DrawMetadataBuffer,
                    scene.LodTransitionBuffer,
                    scene.TransformBuffer,
                    null,
                    true,
                    built,
                    built,
                    null,
                    renderProgram,
                    camera,
                    allowDrawCountReadback: false,
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
                Debug.MeshesWarning("Traditional rendering program does not contain a compute shader. Cannot dispatch compute.");
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

            var rendererForCount = AbstractRenderer.Current;
            bool supportsCountDraw = rendererForCount is not null && rendererForCount.SupportsIndirectCountDraw();
            bool canSubmitWithoutCpuCount = parameterBuffer is not null && supportsCountDraw && !DebugSettings.DisableCountDrawPath;

            // Phase 2: visibleCount may be intentionally not read back (to avoid CPU stalls).
            // Only require a CPU-visible count when we cannot use the GPU count-draw path.
            if (visibleCount == 0 && !canSubmitWithoutCpuCount)
            {
                if (logGpu)
                    GpuDebug("VisibleCommandCount == 0 and count-draw unavailable; skipping GPU indirect build path.");
                return;
            }

            // Set uniforms
            _indirectCompProgram.Uniform("CurrentRenderPass", currentRenderPass);
            _indirectCompProgram.Uniform("MaxIndirectDraws", (int)indirectDrawBuffer.ElementCount);
            _indirectCompProgram.Uniform("ActiveViewCount", (int)(renderPasses.ActiveViewCount == 0u ? 1u : renderPasses.ActiveViewCount));
            _indirectCompProgram.Uniform("SourceViewId", (int)renderPasses.IndirectSourceViewId);
            if (logGpu)
                GpuDebug("Set uniforms: CurrentRenderPass={0}, MaxIndirectDraws={1}", currentRenderPass, indirectDrawBuffer.ElementCount);

            uint dispatchCount = visibleCount;
            if (dispatchCount == 0)
                dispatchCount = maxDrawAllowed;

            if (dispatchCount == 0)
                dispatchCount = 1;
            if (logGpu)
                GpuDebug("Dispatch command count: {0}", dispatchCount);

            // Dispatch compute shader
            uint groupSize = 32; // Should match local_size_x in shader
            (uint groupsX, uint groupsY, uint groupsZ) = XRRenderProgram.ComputeDispatch.ForCommands(dispatchCount, groupSize);

            if (logGpu)
                GpuDebug("Dispatching compute: groups=({0},{1},{2}) groupSize={3}", groupsX, groupsY, groupsZ, groupSize);
            XREngine.Rendering.Commands.GPURenderPassCollection.Crumb(
                $"IndirectComp.Dispatch.BEGIN groups=({groupsX},{groupsY},{groupsZ}) gs={groupSize} dispCnt={dispatchCount} maxDraw={maxDrawAllowed}");
            _indirectCompProgram.DispatchCompute(groupsX, groupsY, groupsZ, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            XREngine.Rendering.Commands.GPURenderPassCollection.Crumb("IndirectComp.Dispatch.END");
            //Debug.Meshes("Compute dispatch complete");

            // Conservative barrier before consuming indirect buffer
            AbstractRenderer.Current?.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.Command |
                EMemoryBarrierMask.ClientMappedBuffer |
                EMemoryBarrierMask.BufferUpdate);
            //Debug.Meshes("Memory barrier issued");

            if (IsInstrumentedStrategy(renderPasses) && DebugSettings.DumpIndirectArguments && RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                DumpGpuIndirectArguments(renderPasses, indirectDrawBuffer, maxDrawAllowed, parameterBuffer, visibleCount);

            //ClearIndirectTail(indirectDrawBuffer, parameterBuffer, maxDrawAllowed);

            if (logGpu)
                GpuDebug("Dispatching indirect render: program={0}", renderProgram != null ? "valid" : "NULL");
            
            // Use the graphics program obtained at the start of the method
            DispatchRenderIndirect(
                indirectDrawBuffer,
                vaoRenderer,
                culledBuffer,
                scene.DrawMetadataBuffer,
                scene.LodTransitionBuffer,
                scene.TransformBuffer,
                null,
                true,
                visibleCount,
                maxDrawAllowed,
                parameterBuffer,
                renderProgram,
                camera,
                allowDrawCountReadback: IsInstrumentedStrategy(renderPasses),
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
                    // Match GPURenderIndirect.comp: baseInstance encodes DrawID, not the compacted visible index.
                    BaseInstance = gpuCommand.Reserved1 & IndirectBaseInstanceCommandIndexMask
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
            long shaderStateRevision = material.ShaderStateRevision;
            (uint materialId, int rendererKey) useKey = (materialID, rendererKey);

            GpuDebug($"Creating new program for material: {material.Name ?? "<unnamed>"}");
            GpuDebug($"Material has {material.Shaders.Count} shaders");

            var shaderList = new List<XRShader>(material.Shaders.Where(shader => shader is not null));
            GpuDebug($"Non-null shaders: {shaderList.Count}");
            
            foreach (var shader in shaderList)
            {
                GpuDebug($"  Shader type: {shader.Type}");
            }

            bool useTextVertexPath = TryDetectTextVertexShader(shaderList, out bool includeTextRotations);
            bool emitLodTransitionRole = false;
            if (!useTextVertexPath)
                AugmentIndirectFragmentShaders(shaderList, out emitLodTransitionRole);

            bool fragmentConsumesTransformId = emitLodTransitionRole || FragmentConsumesTransformId(shaderList);
            if (useTextVertexPath)
            {
                GpuDebug(
                    "Material '{0}' uses text glyph buffers; selecting indirect text vertex path (rotations={1}).",
                    material.Name ?? "<unnamed>",
                    includeTextRotations);
            }

            // For GPU-driven indirect rendering we need a vertex shader that fetches the per-draw world matrix
            // from the culled commands buffer (indexed by gl_BaseInstance). Material-provided vertex shaders
            // generally assume CPU-driven per-object uniforms.
            for (int i = shaderList.Count - 1; i >= 0; --i)
            {
                if (shaderList[i].Type == EShaderType.Vertex)
                    shaderList.RemoveAt(i);
            }

            XRShader? generatedVertexShader = useTextVertexPath
                ? CreateGpuIndirectTextVertexShader(includeTextRotations, fragmentConsumesTransformId)
                : CreateGpuIndirectVertexShader(vaoRenderer, fragmentConsumesTransformId, emitLodTransitionRole);
            if (generatedVertexShader is not null)
                shaderList.Add(generatedVertexShader);

            GpuDebug($"Final shader list count: {shaderList.Count}");

            XRRenderProgramDescriptor descriptor = BuildGpuDrivenCombinedProgramDescriptor(
                material,
                shaderList,
                generatedVertexShader,
                useTextVertexPath,
                fragmentConsumesTransformId,
                emitLodTransitionRole);

            XRRenderProgram? fallbackProgram = null;
            if (_materialProgramUseDescriptors.TryGetValue(useKey, out XRRenderProgramDescriptor previousDescriptor) &&
                !previousDescriptor.Equals(descriptor) &&
                _materialPrograms.TryGetValue(previousDescriptor, out MaterialProgramCache previousCache))
            {
                fallbackProgram = previousCache.Program;
            }

            if (_materialPrograms.TryGetValue(descriptor, out MaterialProgramCache existing))
            {
                ShaderProgramLifecycleDiagnostics.RecordGpuDrivenProgramPoolHit();
                existing.Program.Link();
                _materialProgramUseDescriptors[useKey] = descriptor;
                return existing.Program;
            }

            if (_pendingMaterialPrograms.TryGetValue(descriptor, out MaterialProgramCache pending))
            {
                pending.Program.Link();
                if (IsProgramReadyForCurrentRenderer(pending.Program))
                {
                    _pendingMaterialPrograms.Remove(descriptor);
                    _materialPrograms[descriptor] = pending;
                    _materialProgramUseDescriptors[useKey] = descriptor;
                    ShaderProgramLifecycleDiagnostics.RecordGpuDrivenProgramPoolHit();
                    return pending.Program;
                }

                ShaderProgramLifecycleDiagnostics.RecordGpuDrivenProgramPoolHit();
                return fallbackProgram ?? pending.Program;
            }

            ShaderProgramLifecycleDiagnostics.RecordGpuDrivenProgramPoolMiss();
            //Debug.Meshes("Creating and linking program...");
            
            var program = new XRRenderProgram(linkNow: false, separable: false, shaderList)
            {
                Name = $"GpuIndirectCombined:{material.Name ?? "unknown"}",
                UsageTag = $"GpuIndirectCombinedProgram | material={material.Name ?? "<unnamed>"} | rendererKey={rendererKey}",
                ProgramDescriptor = descriptor,
                Priority = material.ShaderProgramPriority,
            };
            material.ApplyShaderProgramMetadata(program);
            program.SetShaderProgramDiagnosticMetadata(new XRRenderProgram.ShaderProgramDiagnosticMetadata(
                material.Name,
                vaoRenderer?.Name,
                useTextVertexPath ? "GpuIndirectText" : "GpuIndirect",
                "GpuDrivenCombinedMesh",
                descriptor.StableKey,
                descriptor.VertexLayoutIdentity));
            program.AllowLink();
            program.Link();

            MaterialProgramCache cacheEntry = new(program, generatedVertexShader, shaderStateRevision);
            if (IsProgramReadyForCurrentRenderer(program) || fallbackProgram is null)
            {
                _materialPrograms[descriptor] = cacheEntry;
                _materialProgramUseDescriptors[useKey] = descriptor;
                return program;
            }

            _pendingMaterialPrograms[descriptor] = cacheEntry;
            //Debug.Meshes("Program cached");
            
            return fallbackProgram;
        }

        private static XRRenderProgramDescriptor BuildGpuDrivenCombinedProgramDescriptor(
            XRMaterial material,
            IReadOnlyList<XRShader> shaderList,
            XRShader? generatedVertexShader,
            bool useTextVertexPath,
            bool fragmentConsumesTransformId,
            bool emitLodTransitionRole)
        {
            string generatedVertexIdentity = BuildGpuGeneratedVertexIdentity(generatedVertexShader);
            string vertexLayoutIdentity = string.Concat(
                useTextVertexPath ? "text" : "mesh",
                "|transformId=",
                fragmentConsumesTransformId ? "1" : "0",
                "|lodRole=",
                emitLodTransitionRole ? "1" : "0",
                "|generated=",
                generatedVertexIdentity);

            return XRRenderProgramDescriptor.FromShaders(
                shaderList,
                separable: false,
                renderSettingsVersion: RuntimeEngine.Rendering.Settings.ShaderConfigVersion,
                generatedVertexIdentity: generatedVertexIdentity,
                materialVariantKind: material.ActiveUberVariant.IsEmpty ? null : "MaterialVariant",
                materialVariantHash: material.ActiveUberVariant.VariantHash,
                vertexLayoutIdentity: vertexLayoutIdentity,
                topologyKind: useTextVertexPath ? "GpuDrivenText" : "GpuDrivenMesh");
        }

        private static string BuildGpuGeneratedVertexIdentity(XRShader? generatedVertexShader)
        {
            if (generatedVertexShader is null)
                return string.Empty;

            if (generatedVertexShader.TryGetResolvedSource(out string resolvedSource, logFailures: false))
                return XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(resolvedSource);

            return XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(generatedVertexShader.Source?.Text);
        }

        private static bool IsProgramReadyForCurrentRenderer(XRRenderProgram program)
        {
            if (AbstractRenderer.Current is not OpenGLRenderer glRenderer)
                return true;

            OpenGLRenderer.GLRenderProgram? glProgram = FindProgramForCurrentOpenGLRenderer(program, glRenderer);

            return glProgram?.IsLinked == true;
        }

        private static OpenGLRenderer.GLRenderProgram? FindProgramForCurrentOpenGLRenderer(
            XRRenderProgram program,
            OpenGLRenderer glRenderer)
        {
            foreach (var wrapper in program.APIWrappers)
            {
                if (wrapper is OpenGLRenderer.GLRenderProgram glProgram &&
                    ReferenceEquals(glProgram.Owner, glRenderer))
                {
                    return glProgram;
                }
            }

            return null;
        }

        private bool EnsureZeroReadbackMaterialSlotProgramsReady(
            GPURenderPassCollection renderPasses,
            int currentRenderPass,
            IReadOnlyDictionary<uint, XRMaterial> materialMap,
            IReadOnlyList<uint> materialSlotIds,
            XRMeshRenderer? vaoRenderer,
            string context)
        {
            int pendingCount = 0;
            int sampleCount = 0;
            StringBuilder? pendingSamples = null;

            for (int slotIndex = 0; slotIndex < materialSlotIds.Count; ++slotIndex)
            {
                uint materialId = materialSlotIds[slotIndex];
                if (!TryResolveZeroReadbackMaterialForSlot(materialId, currentRenderPass, materialMap, out XRMaterial? material) ||
                    material is null ||
                    TryDetectTextVertexShader(material.Shaders, out _))
                {
                    continue;
                }

                uint effectiveMaterialId = (uint)material.GetHashCode();
                XRRenderProgram? program = EnsureCombinedProgram(effectiveMaterialId, material, vaoRenderer);
                if (program is null || IsProgramReadyForCurrentRenderer(program))
                    continue;

                pendingCount++;
                renderPasses.RecordZeroReadbackProgramPending();
                AppendZeroReadbackPendingProgramSample(
                    ref pendingSamples,
                    ref sampleCount,
                    slotIndex,
                    materialId,
                    bucketIndex: null,
                    tier: null,
                    material,
                    program);
            }

            if (pendingCount == 0)
                return true;

            WarnZeroReadbackProgramWarmup(context, currentRenderPass, pendingCount, pendingSamples?.ToString());
            return false;
        }

        private bool EnsureZeroReadbackActiveBucketProgramsReady(
            GPURenderPassCollection renderPasses,
            int currentRenderPass,
            IReadOnlyDictionary<uint, XRMaterial> materialMap,
            IReadOnlyList<uint> materialSlotIds,
            IReadOnlyList<uint> activeBuckets,
            XRMeshRenderer? vaoRenderer,
            string context)
        {
            int pendingCount = 0;
            int sampleCount = 0;
            StringBuilder? pendingSamples = null;

            for (int bucketListIndex = 0; bucketListIndex < activeBuckets.Count; ++bucketListIndex)
            {
                uint bucketIndex = activeBuckets[bucketListIndex];
                uint slotIndex = bucketIndex / GPUBatchingBindings.MaterialTierCount;
                if (slotIndex >= (uint)materialSlotIds.Count)
                    continue;

                uint materialId = materialSlotIds[(int)slotIndex];
                if (!TryResolveZeroReadbackMaterialForSlot(materialId, currentRenderPass, materialMap, out XRMaterial? material) ||
                    material is null ||
                    TryDetectTextVertexShader(material.Shaders, out _))
                {
                    continue;
                }

                uint effectiveMaterialId = (uint)material.GetHashCode();
                XRRenderProgram? program = EnsureCombinedProgram(effectiveMaterialId, material, vaoRenderer);
                if (program is null || IsProgramReadyForCurrentRenderer(program))
                    continue;

                pendingCount++;
                renderPasses.RecordZeroReadbackProgramPending();
                AppendZeroReadbackPendingProgramSample(
                    ref pendingSamples,
                    ref sampleCount,
                    (int)slotIndex,
                    materialId,
                    bucketIndex,
                    bucketIndex % GPUBatchingBindings.MaterialTierCount,
                    material,
                    program);
            }

            if (pendingCount == 0)
                return true;

            WarnZeroReadbackProgramWarmup(context, currentRenderPass, pendingCount, pendingSamples?.ToString());
            return false;
        }

        private static bool TryResolveZeroReadbackMaterialForSlot(
            uint materialId,
            int currentRenderPass,
            IReadOnlyDictionary<uint, XRMaterial> materialMap,
            out XRMaterial? material)
        {
            XRMaterial? sourceMaterial = null;
            if (materialId != 0)
                materialMap.TryGetValue(materialId, out sourceMaterial);

            if (sourceMaterial is not null && sourceMaterial.RenderPass != currentRenderPass)
            {
                material = null;
                return false;
            }

            material = ResolveEffectiveGpuMaterial(sourceMaterial, RuntimeEngine.Rendering.State.OverrideMaterial)
                ?? XRMaterial.InvalidMaterial;
            return material is not null;
        }

        private static void AppendZeroReadbackPendingProgramSample(
            ref StringBuilder? builder,
            ref int sampleCount,
            int slotIndex,
            uint materialId,
            uint? bucketIndex,
            uint? tier,
            XRMaterial material,
            XRRenderProgram program)
        {
            if (sampleCount >= ZeroReadbackPendingProgramSampleLimit)
                return;

            sampleCount++;
            builder ??= new StringBuilder(512);
            if (builder.Length > 0)
                builder.Append(" | ");

            var backend = program.ShaderMetadata.Backend;
            string materialName = TrimDiagnosticName(material.Name, 64);
            string programName = TrimDiagnosticName(program.Name, 64);
            string glLinkedText = "n/a";
            if (AbstractRenderer.Current is OpenGLRenderer glRenderer)
            {
                OpenGLRenderer.GLRenderProgram? glProgram = FindProgramForCurrentOpenGLRenderer(program, glRenderer);
                glLinkedText = glProgram?.IsLinked.ToString() ?? "missing-wrapper";
            }

            builder
                .Append('#').Append(sampleCount)
                .Append(" slot=").Append(slotIndex)
                .Append(" materialId=").Append(materialId);

            if (bucketIndex.HasValue)
                builder.Append(" bucket=").Append(bucketIndex.Value);
            if (tier.HasValue)
                builder.Append(" tier=").Append(tier.Value);

            builder
                .Append(" material='").Append(materialName).Append('\'')
                .Append(" program='").Append(programName).Append('\'')
                .Append(" programLinked=").Append(program.IsLinked)
                .Append(" glLinked=").Append(glLinkedText)
                .Append(" wrappers=").Append(program.APIWrappers.Count)
                .Append(" stage=").Append(backend.Stage)
                .Append(" backend=").Append(backend.Backend ?? "<none>")
                .Append(" detail='").Append(TrimDiagnosticName(backend.Detail, 96)).Append('\'')
                .Append(" failure='").Append(TrimDiagnosticName(backend.FailureReason, 96)).Append('\'');
        }

        private static string TrimDiagnosticName(string? text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "<none>";

            string sanitized = text.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
            return sanitized.Length <= maxLength
                ? sanitized
                : sanitized[..Math.Max(0, maxLength - 3)] + "...";
        }

        private static void WarnZeroReadbackProgramWarmup(
            string context,
            int currentRenderPass,
            int pendingCount,
            string? pendingSamples = null)
            => XREngine.Debug.RenderingWarningEvery(
                $"RenderDispatch.ZeroReadbackProgramWarmup.{context}.{currentRenderPass}",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] {0} deferred for pass {1}: {2} graphics program(s) are still warming asynchronously; CPU mesh safety-net will render this pass. Samples: {3}",
                context,
                currentRenderPass,
                pendingCount,
                string.IsNullOrWhiteSpace(pendingSamples) ? "<none>" : pendingSamples);

        private static void DestroyMaterialProgramCache(MaterialProgramCache cache)
        {
            cache.Program.Destroy();
            cache.GeneratedVertexShader?.Destroy();
        }

        private static void DestroyMaterialTableProgramCache(MaterialTableProgramCache cache)
        {
            cache.Program.Destroy();
            cache.GeneratedVertexShader?.Destroy();
            cache.FragmentShader.Destroy();
        }

        private static void DestroyMeshletMaterialTableProgramCache(MeshletMaterialTableProgramCache cache)
        {
            cache.Program.Destroy();
            cache.FragmentShader.Destroy();
        }

        private bool TryRenderMeshletMaterialTable(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            int currentRenderPass)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuMeshlet.RenderMaterialTable");
            EMeshSubmissionStrategy requestedStrategy = renderPasses.MeshSubmissionStrategy;
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletStrategyRequested(
                currentRenderPass,
                requestedStrategy,
                requestedStrategy,
                AbstractRenderer.Current?.MeshShaderDialect ?? EMeshShaderDialect.None,
                scene.TotalCommandCount,
                renderPasses.MaxVisibleMeshletTaskCapacity);

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                WarnMeshletMaterialFallback(currentRenderPass, requestedStrategy, "No active renderer is available for meshlet dispatch.");
                return false;
            }

            if (!renderer.SupportsMeshletDispatch())
            {
                WarnMeshletMaterialFallback(currentRenderPass, requestedStrategy, renderer.MeshletDispatchUnsupportedReason);
                return false;
            }

            if (!renderPasses.MeshletExpansionPreparedThisFrame)
            {
                WarnMeshletMaterialFallback(currentRenderPass, requestedStrategy, "Visible meshlet task expansion was not prepared for this pass.");
                return false;
            }

            if (!renderPasses.TryGetMeshletExpansionInputs(scene, out GpuMeshletExpansionInputs inputs))
            {
                WarnMeshletMaterialFallback(currentRenderPass, requestedStrategy, "Meshlet expansion inputs are incomplete.");
                return false;
            }

            if (!IsMeshletMaterialTableDirectPassSupported(currentRenderPass))
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "The current render pass is not implemented by the direct meshlet material-table shader.");
                return false;
            }

            var renderState = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState;
            bool overrideActive = RuntimeEngine.Rendering.State.OverrideMaterial is not null
                || (renderState is not null && renderState.UseDepthNormalMaterialVariants);
            if (overrideActive)
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "Override/depth-normal material variants require the traditional zero-readback material-tier path.");
                return false;
            }

            if (scene.SkinnedCommandCount != 0u)
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "Scene-owned skinned meshlet vertex-weight buffers are not wired yet; preserving skinned meshes through the traditional zero-readback path.");
                return false;
            }

            EZeroReadbackMaterialDrawPath drawPath = renderPasses.ZeroReadbackMaterialDrawPath;
            bool bindlessRequested = drawPath == EZeroReadbackMaterialDrawPath.BindlessMaterialTable;
            if (drawPath is not EZeroReadbackMaterialDrawPath.MaterialTable and
                not EZeroReadbackMaterialDrawPath.BindlessMaterialTable)
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    $"Meshlet production dispatch requires a material-table draw path; current path is {drawPath}.");
                return false;
            }

            if (!renderPasses.TryGetGeneratedMaterialTableDispatchLayout(currentRenderPass, out MaterialBindingLayout layout))
            {
                MaterialBindingResolverResult result = MaterialBindingResolverResult.PerMaterial(
                    $"Pass {currentRenderPass} does not expose a generated material-table layout.");
                renderPasses.RecordMaterialBindingResolverResult(result);
                WarnMeshletMaterialFallback(currentRenderPass, requestedStrategy, result.Reason);
                return false;
            }

            renderPasses.RecordMaterialBindingResolverResult(MaterialBindingResolverResult.Compatible(layout));

            XRDataBuffer? visibleTaskBuffer = renderPasses.VisibleMeshletTaskBuffer;
            XRDataBuffer? visibleTaskCountBuffer = renderPasses.VisibleMeshletTaskCountBuffer;
            XRDataBuffer? dispatchIndirectBuffer = renderPasses.MeshletDispatchIndirectBuffer;
            XRDataBuffer? dispatchCountBuffer = renderPasses.MeshletDispatchCountBuffer;
            XRDataBuffer? materialTableBuffer = renderPasses.MaterialTableBuffer;
            XRDataBuffer? positions = scene.AtlasPositions;
            XRDataBuffer? normals = scene.AtlasNormals;
            XRDataBuffer? tangents = scene.AtlasTangents;
            XRDataBuffer? uv0 = scene.AtlasUV0;

            if (visibleTaskBuffer is null ||
                visibleTaskCountBuffer is null ||
                dispatchIndirectBuffer is null ||
                dispatchCountBuffer is null ||
                materialTableBuffer is null ||
                positions is null ||
                normals is null ||
                tangents is null ||
                uv0 is null)
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "One or more meshlet material-table buffers are missing.");
                return false;
            }

            bool useBindless = bindlessRequested && SupportsOpenGLBindlessMaterialTable();
            XRDataBuffer? materialTextureHandleBuffer = useBindless ? renderPasses.MaterialTextureHandleBuffer : null;
            if (useBindless && materialTextureHandleBuffer is null)
                useBindless = false;

            if (bindlessRequested && !useBindless && _bindlessMaterialTableUnsupportedLogBudget > 0)
            {
                _bindlessMaterialTableUnsupportedLogBudget--;
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "Bindless/descriptor-indexed material-table meshlets were requested, but the active backend cannot bind the material texture handle table.");
            }

            XRRenderProgram? program = EnsureMeshletMaterialTableProgram(useBindless, layout, renderer.MeshShaderDialect, skinned: false);
            if (program is null)
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "Meshlet material-table program could not be created.");
                return false;
            }

            if (!IsProgramReadyForCurrentRenderer(program))
            {
                renderPasses.RecordZeroReadbackProgramPending();
                WarnZeroReadbackProgramWarmup(
                    useBindless ? "GpuMeshletBindlessMaterialTable" : "GpuMeshletMaterialTable",
                    currentRenderPass,
                    pendingCount: 1);
                return false;
            }

            if (!TryUseIndirectGraphicsProgram(program, useBindless ? "GpuMeshletBindlessMaterialTable" : "GpuMeshletMaterialTable"))
            {
                WarnMeshletMaterialFallback(
                    currentRenderPass,
                    requestedStrategy,
                    "Meshlet material-table program is not usable by the active renderer.");
                return false;
            }

            inputs.MeshDataBuffer.BindTo(program, MeshletMeshDataSsboBinding);
            inputs.MeshletDescriptorBuffer.BindTo(program, MeshletDescriptorSsboBinding);
            inputs.MeshletVertexIndexBuffer.BindTo(program, MeshletVertexIndexSsboBinding);
            inputs.MeshletTriangleIndexBuffer.BindTo(program, MeshletTriangleIndexSsboBinding);
            visibleTaskBuffer.BindTo(program, MeshletTaskRecordSsboBinding);
            visibleTaskCountBuffer.BindTo(program, MeshletTaskCountSsboBinding);
            inputs.DrawMetadataBuffer.BindTo(program, DrawMetadataSsboBinding);
            positions.BindTo(program, MeshletAtlasPositionSsboBinding);
            normals.BindTo(program, MeshletAtlasNormalSsboBinding);
            tangents.BindTo(program, MeshletAtlasTangentSsboBinding);
            uv0.BindTo(program, MeshletAtlasUv0SsboBinding);
            scene.TransformBuffer.BindTo(program, MeshletTransformSsboBinding);
            scene.PrevTransformBuffer.BindTo(program, MeshletPrevTransformSsboBinding);
            scene.MaterialStateBuffer.BindTo(program, MeshletMaterialStateSsboBinding);
            materialTableBuffer.BindTo(program, MaterialTableSsboBinding);
            materialTextureHandleBuffer?.BindTo(program, MaterialTextureHandleTableSsboBinding);
            XRDataBuffer? statsBuffer = renderPasses.StatsBuffer;
            statsBuffer?.BindTo(program, MeshletStatsSsboBinding);

            SetMeshletMaterialTableUniforms(program, renderPasses, camera, currentRenderPass);
            program.Uniform("StatsEnabled", statsBuffer is not null ? 1u : 0u);
            renderer.SetEngineUniforms(program, camera);

            bool submitted = false;
            TimeSpan dispatchElapsed = TimeSpan.Zero;
            try
            {
                renderer.MemoryBarrier(
                    EMemoryBarrierMask.ShaderStorage |
                    EMemoryBarrierMask.Command |
                    EMemoryBarrierMask.TextureFetch);

                System.Diagnostics.Stopwatch dispatchStopwatch = System.Diagnostics.Stopwatch.StartNew();
                submitted = renderer.TryDrawMeshTasksIndirectCount(
                    dispatchIndirectBuffer,
                    dispatchCountBuffer,
                    GPUMeshletLayout.MeshTaskIndirectCommandMaxDrawCount,
                    GPUMeshletLayout.MeshTaskIndirectCommandStride,
                    out string failureReason);
                dispatchStopwatch.Stop();
                dispatchElapsed = dispatchStopwatch.Elapsed;

                if (!submitted)
                    WarnMeshletMaterialFallback(currentRenderPass, requestedStrategy, failureReason);
            }
            finally
            {
                renderer.UnbindParameterBuffer();
                renderer.UnbindDrawIndirectBuffer();
            }

            if (submitted)
            {
                RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletProductionFrame(1);
                RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletBufferBytesResident(scene.MeshletBufferBytesResident);
                renderPasses.CaptureMeshletInstrumentationAfterDispatch(dispatchElapsed);
                XREngine.Debug.Meshes(
                    $"Meshlet.BackendSelected pass={currentRenderPass} requested={requestedStrategy} selected={requestedStrategy} dialect={renderer.MeshShaderDialect} commandCount={scene.TotalCommandCount} meshletCount={scene.MeshletDescriptorCount} capacity={renderPasses.MaxVisibleMeshletTaskCapacity}");
            }

            return submitted;
        }

        private XRRenderProgram? EnsureMeshletMaterialTableProgram(
            bool bindless,
            MaterialBindingLayout layout,
            EMeshShaderDialect dialect,
            bool skinned)
        {
            (bool bindless, EMeshShaderDialect dialect, bool skinned, string layoutHash) cacheKey =
                (bindless, dialect, skinned, layout.LayoutHash);

            if (_meshletMaterialTablePrograms.TryGetValue(cacheKey, out MeshletMaterialTableProgramCache existing))
            {
                existing.Program.Link();
                return existing.Program;
            }

            if (!TryGetMeshletShaderPaths(dialect, skinned, out string taskShaderPath, out string meshShaderPath, out string failureReason))
            {
                Debug.MeshesWarning($"[RenderDispatch] Meshlet material-table program unavailable: {failureReason}");
                return null;
            }

            XRShader taskShader = ShaderHelper.LoadEngineShader(taskShaderPath, EShaderType.Task);
            XRShader meshShader = ShaderHelper.LoadEngineShader(meshShaderPath, EShaderType.Mesh);
            XRShader fragmentShader = CreateMeshletMaterialTableFragmentShader(bindless, layout);
            var shaderList = new List<XRShader> { taskShader, meshShader, fragmentShader };
            var program = new XRRenderProgram(false, false, shaderList);
            program.AllowLink();
            program.Link();

            _meshletMaterialTablePrograms[cacheKey] =
                new MeshletMaterialTableProgramCache(program, taskShader, meshShader, fragmentShader);
            return program;
        }

        private static bool TryGetMeshletShaderPaths(
            EMeshShaderDialect dialect,
            bool skinned,
            out string taskShaderPath,
            out string meshShaderPath,
            out string failureReason)
        {
            taskShaderPath = string.Empty;
            meshShaderPath = string.Empty;
            failureReason = string.Empty;

            switch (dialect)
            {
                case EMeshShaderDialect.OpenGLNV:
                    taskShaderPath = "Meshlets/MeshletCulling.task";
                    meshShaderPath = skinned
                        ? "Meshlets/MeshletRenderSkinned.mesh"
                        : "Meshlets/MeshletRender.mesh";
                    return true;
                case EMeshShaderDialect.OpenGLEXT:
                case EMeshShaderDialect.VulkanEXT:
                    taskShaderPath = "Meshlets/MeshletCullingExt.task";
                    meshShaderPath = skinned
                        ? "Meshlets/MeshletRenderSkinnedExt.mesh"
                        : "Meshlets/MeshletRenderExt.mesh";
                    return true;
                default:
                    failureReason = "No production mesh shader dialect is active.";
                    return false;
            }
        }

        private static bool IsMeshletMaterialTableDirectPassSupported(int renderPass)
            => renderPass == (int)EDefaultRenderPass.OpaqueDeferred ||
               renderPass == (int)EDefaultRenderPass.OpaqueForward ||
               renderPass == (int)EDefaultRenderPass.MaskedForward ||
               renderPass == (int)EDefaultRenderPass.TransparentForward ||
               renderPass == (int)EDefaultRenderPass.WeightedBlendedOitForward ||
               renderPass == (int)EDefaultRenderPass.PerPixelLinkedListForward ||
               renderPass == (int)EDefaultRenderPass.DepthPeelingForward;

        private static uint GetMeshletPassFlags(GPURenderPassCollection renderPasses, int renderPass)
        {
            uint flags = renderPass switch
            {
                (int)EDefaultRenderPass.OpaqueDeferred or
                (int)EDefaultRenderPass.OpaqueForward => MeshletPassOpaque,
                (int)EDefaultRenderPass.MaskedForward => MeshletPassMasked,
                (int)EDefaultRenderPass.TransparentForward or
                (int)EDefaultRenderPass.WeightedBlendedOitForward or
                (int)EDefaultRenderPass.PerPixelLinkedListForward or
                (int)EDefaultRenderPass.DepthPeelingForward => MeshletPassTransparent,
                _ => 0u,
            };

            if (renderPasses.ActiveViewCount > 1u)
                flags |= MeshletPassStereo;

            return flags;
        }

        private static uint GetMeshletAllowedStateClassMask(int renderPass)
            => renderPass switch
            {
                (int)EDefaultRenderPass.OpaqueDeferred => MeshletStateClassMask(EGpuMaterialStateClass.OpaqueDeferred),
                (int)EDefaultRenderPass.OpaqueForward => MeshletStateClassMask(EGpuMaterialStateClass.OpaqueForward),
                (int)EDefaultRenderPass.MaskedForward => MeshletStateClassMask(EGpuMaterialStateClass.AlphaTested),
                (int)EDefaultRenderPass.TransparentForward or
                (int)EDefaultRenderPass.WeightedBlendedOitForward or
                (int)EDefaultRenderPass.PerPixelLinkedListForward or
                (int)EDefaultRenderPass.DepthPeelingForward => MeshletStateClassMask(EGpuMaterialStateClass.Transparent),
                _ => 0u,
            };

        private static uint MeshletStateClassMask(EGpuMaterialStateClass stateClass)
        {
            uint stateClassId = (uint)stateClass;
            return stateClassId < 32u ? 1u << (int)stateClassId : 0u;
        }

        private static void SetMeshletMaterialTableUniforms(
            XRRenderProgram program,
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            int currentRenderPass)
        {
            Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
            Matrix4x4 viewProjectionMatrix = viewMatrix * camera.ProjectionMatrix;
            XRTexture2D? hiZDepthPyramid = null;
            int hiZMaxMip = 0;
            Matrix4x4 hiZViewProjectionMatrix = viewProjectionMatrix;
            bool hiZUsesReversedZ = false;
            bool hiZAvailable = renderPasses.ActiveViewCount <= 1u &&
                renderPasses.ActiveOcclusionMode == EOcclusionCullingMode.GpuHiZ &&
                renderPasses.TryGetHiZDepthPyramidForMeshlets(
                    out hiZDepthPyramid,
                    out hiZMaxMip,
                    out hiZViewProjectionMatrix,
                    out hiZUsesReversedZ);

            program.Uniform("ViewProjectionMatrix", viewProjectionMatrix);
            program.Uniform("PreviousViewProjectionMatrix", viewProjectionMatrix);
            program.Uniform("CameraPosition", camera.Transform.RenderTranslation);
            program.Uniform("HiZViewProjectionMatrix", hiZAvailable ? hiZViewProjectionMatrix : viewProjectionMatrix);
            program.Uniform("HiZSize", hiZAvailable
                ? new Vector2((float)hiZDepthPyramid!.Mipmaps[0].Width, (float)hiZDepthPyramid.Mipmaps[0].Height)
                : Vector2.Zero);
            program.Uniform("HiZMipCount", hiZAvailable ? hiZMaxMip + 1.0f : 0.0f);
            program.Uniform("HiZDepthBias", 0.0f);
            program.Uniform("HiZUsesReversedZ", hiZAvailable && hiZUsesReversedZ ? 1u : 0u);
            program.Uniform("HiZValid", hiZAvailable ? 1u : 0u);
            program.Uniform("EnableHiZOcclusion", hiZAvailable ? 1u : 0u);
            program.Uniform("EnableFrustumCulling", 1u);
            program.Uniform("EnableConeCulling", 1u);
            program.Uniform("EnableMaskedConeCulling", 0u);
            program.Uniform("EnableTransparentConeCulling", 0u);
            program.Uniform("EnableVelocityConeCulling", 0u);
            program.Uniform("EnableStereoHiZ", 0u);
            program.Uniform("ActiveViewCount", renderPasses.ActiveViewCount == 0u ? 1u : renderPasses.ActiveViewCount);
            program.Uniform("ActiveViewIndex", renderPasses.IndirectSourceViewId);
            program.Uniform("CurrentRenderPass", currentRenderPass >= 0 ? (uint)currentRenderPass : uint.MaxValue);
            program.Uniform("RequiredRenderPassMask", 0u);
            program.Uniform("RequiredLayerMask", 0u);
            program.Uniform("AllowedStateClassMask", GetMeshletAllowedStateClassMask(currentRenderPass));
            program.Uniform("PassFlags", GetMeshletPassFlags(renderPasses, currentRenderPass));
            program.Uniform("MeshletAlphaCutoff", 0.5f);
            program.Uniform("EnableSkinning", 0u);
            program.Uniform(MeshletDebugDisplayUniformName, GpuBvhDebugSettings.IsMeshletDebugDisplayEnabled(camera) ? 1u : 0u);
            if (hiZAvailable)
                program.Sampler("HiZDepth", hiZDepthPyramid!, 0);

            IReadOnlyList<Plane> planes = camera.WorldFrustum().Planes;
            int planeCount = Math.Min(planes.Count, MeshletFrustumPlaneUniformNames.Length);
            for (int i = 0; i < planeCount; ++i)
                program.Uniform(MeshletFrustumPlaneUniformNames[i], planes[i].AsVector4());
        }

        private static void WarnMeshletMaterialFallback(
            int currentRenderPass,
            EMeshSubmissionStrategy requestedStrategy,
            string reason)
        {
            RuntimeEngine.Rendering.Stats.GpuFallback.RecordForbiddenGpuFallback(1);
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletFallback(1);
            XREngine.Debug.RenderingWarningEvery(
                $"RenderDispatch.GpuMeshletMaterialFallback.{currentRenderPass}.{reason.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] Meshlet.BackendUnsupported pass={0} requested={2} selected={3} reason='{1}' skipping traditional mesh fallback",
                currentRenderPass,
                reason,
                requestedStrategy,
                requestedStrategy);
        }

        private XRRenderProgram? EnsureMaterialTableDrawProgram(XRMeshRenderer? vaoRenderer, bool bindless, MaterialBindingLayout layout)
        {
            int rendererKey = vaoRenderer is null ? 0 : RuntimeHelpers.GetHashCode(vaoRenderer);
            (bool bindless, int rendererKey, string layoutHash) cacheKey = (bindless, rendererKey, layout.LayoutHash);

            if (_materialTablePrograms.TryGetValue(cacheKey, out var existing))
            {
                GpuDebug(
                    LogCategory.Validation,
                    "Material-table program cache hit bindless={0} rendererKey={1} layout={2} hash={3}",
                    bindless,
                    rendererKey,
                    layout.Name,
                    layout.LayoutHash);
                existing.Program.Link();
                return existing.Program;
            }

            XRShader? generatedVertexShader = CreateGpuIndirectVertexShader(
                vaoRenderer,
                emitTransformId: true,
                emitLodTransitionRole: false,
                emitMaterialId: true);
            if (generatedVertexShader is null)
                return null;

            XRShader fragmentShader = CreateMaterialTableFragmentShader(bindless, layout);
            var shaderList = new List<XRShader> { fragmentShader, generatedVertexShader };
            var program = new XRRenderProgram(false, false, shaderList);
            program.AllowLink();
            program.Link();

            var cacheEntry = new MaterialTableProgramCache(program, generatedVertexShader, fragmentShader);
            _materialTablePrograms[cacheKey] = cacheEntry;
            GpuDebug(
                LogCategory.Validation,
                "Material-table program cache miss bindless={0} rendererKey={1} layout={2} hash={3} rowBytes={4}",
                bindless,
                rendererKey,
                layout.Name,
                layout.LayoutHash,
                layout.RowByteCount);
            return program;
        }

        private static bool SupportsOpenGLBindlessMaterialTable()
        {
            if (AbstractRenderer.Current is not OpenGLRenderer)
                return false;

            string[]? extensions = RuntimeEngine.Rendering.State.OpenGLExtensions;
            if (extensions is null || extensions.Length == 0)
                return false;

            return extensions.Contains("GL_ARB_bindless_texture", StringComparer.Ordinal) &&
                extensions.Contains("GL_ARB_gpu_shader_int64", StringComparer.Ordinal) &&
                AbstractRenderer.Current is OpenGLRenderer { SupportsBindlessTextureHandles: true };
        }

        private static void AppendDrawMetadataGlsl(StringBuilder sb)
        {
            sb.AppendLine("struct DrawMetadata");
            sb.AppendLine("{");
            sb.AppendLine("    uint DrawID;");
            sb.AppendLine("    uint MeshID;");
            sb.AppendLine("    uint SubmeshID;");
            sb.AppendLine("    uint MaterialID;");
            sb.AppendLine("    uint TransformID;");
            sb.AppendLine("    uint SkinID;");
            sb.AppendLine("    uint RenderPassMask;");
            sb.AppendLine("    uint LayerMask;");
            sb.AppendLine("    uint Flags;");
            sb.AppendLine("    uint LodPolicy;");
            sb.AppendLine("    uint StateClassID;");
            sb.AppendLine("    uint InstanceCount;");
            sb.AppendLine("    uint RenderPass;");
            sb.AppendLine("    uint ShaderProgramID;");
            sb.AppendLine("    uint LogicalMeshID;");
            sb.AppendLine("    uint BoundsID;");
            sb.AppendLine("};");
            sb.AppendLine($"layout(std430, binding = {DrawMetadataSsboBinding}) readonly buffer DrawMetadataBuffer {{ DrawMetadata Draws[]; }};");
        }

        private static XRShader CreateMaterialTableFragmentShader(bool bindless, MaterialBindingLayout layout)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#version 460 core");
            if (bindless)
            {
                sb.AppendLine("#extension GL_ARB_gpu_shader_int64 : require");
                sb.AppendLine("#extension GL_ARB_bindless_texture : require");
            }
            sb.AppendLine();
            sb.AppendLine("layout(location=1) in vec3 FragNorm;");
            sb.AppendLine("layout(location=4) in vec2 FragUV0;");
            sb.AppendLine($"layout(location=21) in float {DefaultVertexShaderGenerator.FragTransformIdName};");
            sb.AppendLine($"layout(location={FragMaterialIdLocation}) flat in uint {FragMaterialIdName};");
            sb.AppendLine("layout(location=0) out vec4 AlbedoOpacity;");
            sb.AppendLine("layout(location=1) out vec2 Normal;");
            sb.AppendLine("layout(location=2) out vec4 RMSE;");
            sb.AppendLine("layout(location=3) out uint TransformId;");
            sb.AppendLine();
            MaterialBindingGlslGenerator.AppendMaterialTableDefinitions(
                sb,
                layout,
                bindless,
                MaterialTableSsboBinding,
                MaterialTextureHandleTableSsboBinding);
            sb.AppendLine();
            sb.AppendLine("vec2 XRENGINE_EncodeNormal(vec3 normal)");
            sb.AppendLine("{");
            sb.AppendLine("    normal = normalize(normal);");
            sb.AppendLine("    float invL1Norm = 1.0 / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);");
            sb.AppendLine("    vec3 n = normal * invL1Norm;");
            sb.AppendLine("    vec2 oct = n.xy;");
            sb.AppendLine("    if (n.z < 0.0)");
            sb.AppendLine("    {");
            sb.AppendLine("        vec2 signDir = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);");
            sb.AppendLine("        oct = (1.0 - abs(oct.yx)) * signDir;");
            sb.AppendLine("    }");
            sb.AppendLine("    return oct * 0.5 + 0.5;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine($"    uint drawID = floatBitsToUint({DefaultVertexShaderGenerator.FragTransformIdName});");
            sb.AppendLine($"    uint materialId = {FragMaterialIdName};");
            sb.AppendLine("    MaterialEntry material;");
            sb.AppendLine("    XR_LoadMaterial(materialId, material);");
            sb.AppendLine("    uint flags = material.Flags;");
            sb.AppendLine("    vec4 baseColorOpacity = material.BaseColorOpacity;");
            sb.AppendLine("    vec4 rmse = material.RMSE;");
            sb.AppendLine("    vec3 baseColor = baseColorOpacity.rgb;");
            sb.AppendLine("    float opacity = baseColorOpacity.a;");
            if (bindless)
            {
                sb.AppendLine("    if ((flags & 1u) != 0u)");
                sb.AppendLine("    {");
                sb.AppendLine("        vec4 albedo = SampleBindlessTexture(material.AlbedoHandleIndex, FragUV0, vec4(1.0));");
                sb.AppendLine("        baseColor *= albedo.rgb;");
                sb.AppendLine("        opacity *= albedo.a;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("    if ((flags & (1u << 31u)) == 0u)");
            sb.AppendLine("        baseColor = mix(baseColor, vec3(1.0, 0.0, 1.0), 0.65);");
            sb.AppendLine("    TransformId = drawID;");
            sb.AppendLine("    Normal = XRENGINE_EncodeNormal(FragNorm);");
            sb.AppendLine("    AlbedoOpacity = vec4(baseColor, opacity);");
            sb.AppendLine("    RMSE = rmse;");
            sb.AppendLine("}");

            return new XRShader(EShaderType.Fragment, sb.ToString())
            {
                Name = bindless ? "GPUIndirect_BindlessMaterialTableFS" : "GPUIndirect_MaterialTableFS"
            };
        }

        private static XRShader CreateMeshletMaterialTableFragmentShader(bool bindless, MaterialBindingLayout layout)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#version 460 core");
            if (bindless)
            {
                sb.AppendLine("#extension GL_ARB_gpu_shader_int64 : require");
                sb.AppendLine("#extension GL_ARB_bindless_texture : require");
            }
            sb.AppendLine();
            sb.AppendLine("const uint XRE_TRANSPARENCY_MASKED = 1u;");
            sb.AppendLine("const uint XRE_TRANSPARENCY_ALPHA_TO_COVERAGE = 9u;");
            sb.AppendLine();
            sb.AppendLine("struct MaterialStateGpu");
            sb.AppendLine("{");
            sb.AppendLine("    uint StateClassID;");
            sb.AppendLine("    uint MaterialID;");
            sb.AppendLine("    uint PipelineKey;");
            sb.AppendLine("    uint OptionsBits;");
            sb.AppendLine("    uint TransparencyMode;");
            sb.AppendLine("    uint DescriptorStart;");
            sb.AppendLine("    uint DescriptorCount;");
            sb.AppendLine("    uint Flags;");
            sb.AppendLine("};");
            sb.AppendLine($"layout(std430, binding = {MeshletMaterialStateSsboBinding}) readonly buffer MaterialStateBuffer {{ MaterialStateGpu MaterialStates[]; }};");
            sb.AppendLine();
            sb.AppendLine("layout(location=1) in vec3 FragNorm;");
            sb.AppendLine("layout(location=4) in vec2 FragUV0;");
            sb.AppendLine($"layout(location={FragMeshletDebugColorLocation}) in vec4 {FragMeshletDebugColorName};");
            sb.AppendLine($"layout(location=21) in float {DefaultVertexShaderGenerator.FragTransformIdName};");
            sb.AppendLine($"layout(location={FragMaterialIdLocation}) flat in uint {FragMaterialIdName};");
            sb.AppendLine($"layout(location={FragStateClassIdLocation}) flat in uint {FragStateClassIdName};");
            sb.AppendLine("layout(location=0) out vec4 AlbedoOpacity;");
            sb.AppendLine("layout(location=1) out vec2 Normal;");
            sb.AppendLine("layout(location=2) out vec4 RMSE;");
            sb.AppendLine("layout(location=3) out uint TransformId;");
            sb.AppendLine();
            sb.AppendLine("uniform float MeshletAlphaCutoff;");
            sb.AppendLine($"uniform uint {MeshletDebugDisplayUniformName};");
            sb.AppendLine();
            MaterialBindingGlslGenerator.AppendMaterialTableDefinitions(
                sb,
                layout,
                bindless,
                MaterialTableSsboBinding,
                MaterialTextureHandleTableSsboBinding);
            sb.AppendLine();
            sb.AppendLine("MaterialStateGpu XRE_LoadMaterialState(uint stateClassId)");
            sb.AppendLine("{");
            sb.AppendLine("    if (stateClassId < uint(MaterialStates.length()))");
            sb.AppendLine("        return MaterialStates[stateClassId];");
            sb.AppendLine("    return MaterialStateGpu(0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("vec2 XRENGINE_EncodeNormal(vec3 normal)");
            sb.AppendLine("{");
            sb.AppendLine("    normal = normalize(normal);");
            sb.AppendLine("    float invL1Norm = 1.0 / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);");
            sb.AppendLine("    vec3 n = normal * invL1Norm;");
            sb.AppendLine("    vec2 oct = n.xy;");
            sb.AppendLine("    if (n.z < 0.0)");
            sb.AppendLine("    {");
            sb.AppendLine("        vec2 signDir = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);");
            sb.AppendLine("        oct = (1.0 - abs(oct.yx)) * signDir;");
            sb.AppendLine("    }");
            sb.AppendLine("    return oct * 0.5 + 0.5;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine($"    uint drawID = floatBitsToUint({DefaultVertexShaderGenerator.FragTransformIdName});");
            sb.AppendLine($"    if ({MeshletDebugDisplayUniformName} != 0u)");
            sb.AppendLine("    {");
            sb.AppendLine("        TransformId = drawID;");
            sb.AppendLine("        Normal = XRENGINE_EncodeNormal(FragNorm);");
            sb.AppendLine($"        AlbedoOpacity = vec4({FragMeshletDebugColorName}.rgb, 1.0);");
            sb.AppendLine("        RMSE = vec4(1.0, 0.0, 0.0, 1.0);");
            sb.AppendLine("        return;");
            sb.AppendLine("    }");
            sb.AppendLine($"    MaterialStateGpu state = XRE_LoadMaterialState({FragStateClassIdName});");
            sb.AppendLine($"    uint materialId = {FragMaterialIdName} != 0u ? {FragMaterialIdName} : state.MaterialID;");
            sb.AppendLine("    MaterialEntry material;");
            sb.AppendLine("    XR_LoadMaterial(materialId, material);");
            sb.AppendLine("    uint flags = material.Flags;");
            sb.AppendLine("    vec4 baseColorOpacity = material.BaseColorOpacity;");
            sb.AppendLine("    vec4 rmse = material.RMSE;");
            sb.AppendLine("    vec3 baseColor = baseColorOpacity.rgb;");
            sb.AppendLine("    float opacity = baseColorOpacity.a;");
            if (bindless)
            {
                sb.AppendLine("    if ((flags & 1u) != 0u)");
                sb.AppendLine("    {");
                sb.AppendLine("        vec4 albedo = SampleBindlessTexture(material.AlbedoHandleIndex, FragUV0, vec4(1.0));");
                sb.AppendLine("        baseColor *= albedo.rgb;");
                sb.AppendLine("        opacity *= albedo.a;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("    if ((state.TransparencyMode == XRE_TRANSPARENCY_MASKED || state.TransparencyMode == XRE_TRANSPARENCY_ALPHA_TO_COVERAGE) && opacity < MeshletAlphaCutoff)");
            sb.AppendLine("        discard;");
            sb.AppendLine("    if ((flags & (1u << 31u)) == 0u)");
            sb.AppendLine("        baseColor = mix(baseColor, vec3(1.0, 0.0, 1.0), 0.65);");
            sb.AppendLine("    TransformId = drawID;");
            sb.AppendLine("    Normal = XRENGINE_EncodeNormal(FragNorm);");
            sb.AppendLine("    AlbedoOpacity = vec4(baseColor, opacity);");
            sb.AppendLine("    RMSE = rmse;");
            sb.AppendLine("}");

            return new XRShader(EShaderType.Fragment, sb.ToString())
            {
                Name = bindless ? "GpuMeshlet_BindlessMaterialTableFS" : "GpuMeshlet_MaterialTableFS"
            };
        }

        private XRShader? CreateGpuIndirectVertexShader(
            XRMeshRenderer? vaoRenderer,
            bool emitTransformId,
            bool emitLodTransitionRole,
            bool emitMaterialId = false)
        {
            // Build a vertex shader compatible with the engine's default fragment shader expectations,
            // but sourcing ModelMatrix from the culled command buffer via gl_BaseInstance.
            var sb = new StringBuilder();
            sb.AppendLine("#version 460");
            sb.AppendLine();
            sb.AppendLine($"// GPU indirect: per-draw command data (float[{IndirectCommandFloatCount}])");
            sb.AppendLine($"layout(std430, binding = {IndirectCommandSsboBinding}) readonly buffer CulledCommandsBuffer {{ float culled[]; }};");
            sb.AppendLine($"layout(std430, binding = {InstanceTransformSsboBinding}) readonly buffer TransformBuffer {{ float instanceWorld[]; }};");
            sb.AppendLine($"layout(std430, binding = {InstanceSourceIndexSsboBinding}) readonly buffer InstanceSourceIndexBuffer {{ uint instanceSourceIndex[]; }};");
            AppendDrawMetadataGlsl(sb);
            sb.AppendLine($"const int COMMAND_FLOATS = {IndirectCommandFloatCount};");
            sb.AppendLine("const int INSTANCE_MATRIX_FLOATS = 16;");
            sb.AppendLine($"const uint XRE_LEGACY_BASEINSTANCE_FLAG = 0x{IndirectLegacyBaseInstanceFlag:X8}u;");
            sb.AppendLine($"const uint XRE_PREVIOUS_LOD_BASEINSTANCE_FLAG = 0x{IndirectPreviousLodBaseInstanceFlag:X8}u;");
            sb.AppendLine($"const uint XRE_BASEINSTANCE_COMMAND_INDEX_MASK = 0x{IndirectBaseInstanceCommandIndexMask:X8}u;");
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
            sb.AppendLine("layout(location=2) out vec3 FragTan;");
            sb.AppendLine("layout(location=3) out vec3 FragBinorm;");

            for (int i = 0; i < texCoordBindings.Count && i < 8; ++i)
                sb.AppendLine($"layout(location={4 + i}) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, i)};");
            if (texCoordBindings.Count == 0)
                sb.AppendLine($"layout(location=4) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, 0)};");

            if (colorBindings.Count == 0)
                sb.AppendLine($"layout(location=12) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, 0)};");
            else
                for (int i = 0; i < colorBindings.Count && i < 8; ++i)
                    sb.AppendLine($"layout(location={12 + i}) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, i)};");

            sb.AppendLine($"layout(location=20) out vec3 {DefaultVertexShaderGenerator.FragPosLocalName};");
            if (emitTransformId)
                sb.AppendLine($"layout(location=21) out float {DefaultVertexShaderGenerator.FragTransformIdName};");
            sb.AppendLine($"layout(location=22) out float {DefaultVertexShaderGenerator.FragViewIndexName};");
            if (emitLodTransitionRole)
                sb.AppendLine($"layout(location={FragLodTransitionRoleLocation}) flat out uint {FragLodTransitionRoleName};");
            if (emitMaterialId)
                sb.AppendLine($"layout(location={FragMaterialIdLocation}) flat out uint {FragMaterialIdName};");
            sb.AppendLine();

            sb.AppendLine($"uniform mat4 {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.InverseViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform bool {EEngineUniform.VRMode};");
            sb.AppendLine("uniform int UseInstanceTransformBuffer;");
            sb.AppendLine();

            sb.AppendLine("uint LoadDrawIdFromCommand(uint commandIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    int base = int(commandIndex) * COMMAND_FLOATS;");
            sb.AppendLine("    if (base + 19 < culled.length())");
            sb.AppendLine("        return floatBitsToUint(culled[base + 19]);");
            sb.AppendLine("    return commandIndex;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint LoadTransformId(uint drawID)");
            sb.AppendLine("{");
            sb.AppendLine("    if (drawID < uint(Draws.length()))");
            sb.AppendLine("        return Draws[drawID].TransformID;");
            sb.AppendLine("    return 0u;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("mat4 LoadWorldMatrixFromTransforms(uint transformID)");
            sb.AppendLine("{");
            sb.AppendLine("    int base = int(transformID) * INSTANCE_MATRIX_FLOATS;");
            sb.AppendLine("    if (base + 15 >= instanceWorld.length())");
            sb.AppendLine("        return mat4(1.0);");
            sb.AppendLine("    // CPU Matrix4x4 rows are intentionally reinterpreted as GLSL columns, matching uniform upload.");
            sb.AppendLine("    vec4 c0 = vec4(instanceWorld[base+0],  instanceWorld[base+1],  instanceWorld[base+2],  instanceWorld[base+3]);");
            sb.AppendLine("    vec4 c1 = vec4(instanceWorld[base+4],  instanceWorld[base+5],  instanceWorld[base+6],  instanceWorld[base+7]);");
            sb.AppendLine("    vec4 c2 = vec4(instanceWorld[base+8],  instanceWorld[base+9],  instanceWorld[base+10], instanceWorld[base+11]);");
            sb.AppendLine("    vec4 c3 = vec4(instanceWorld[base+12], instanceWorld[base+13], instanceWorld[base+14], instanceWorld[base+15]);");
            sb.AppendLine("    return mat4(c0, c1, c2, c3);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint ResolveCommandIndex(uint rawBaseInstance, uint instanceLinearIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    uint baseIndex = rawBaseInstance & XRE_BASEINSTANCE_COMMAND_INDEX_MASK;");
            sb.AppendLine("    bool useLegacyBaseInstance = (rawBaseInstance & XRE_LEGACY_BASEINSTANCE_FLAG) != 0u;");
            sb.AppendLine("    if (useLegacyBaseInstance)");
            sb.AppendLine("        return LoadDrawIdFromCommand(baseIndex);");
            sb.AppendLine("    return baseIndex;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("mat4 ResolveModelMatrix(uint rawBaseInstance, uint instanceLinearIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    if (UseInstanceTransformBuffer == 0)");
            sb.AppendLine("        return mat4(1.0);");
            sb.AppendLine("    uint drawID = ResolveCommandIndex(rawBaseInstance, instanceLinearIndex);");
            sb.AppendLine("    return LoadWorldMatrixFromTransforms(LoadTransformId(drawID));");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine("    uint rawBaseInstance = uint(gl_BaseInstance);");
            sb.AppendLine("    uint baseIndex = rawBaseInstance & XRE_BASEINSTANCE_COMMAND_INDEX_MASK;");
            sb.AppendLine("    uint instanceLinearIndex = baseIndex + uint(gl_InstanceID);");
            sb.AppendLine("    mat4 ModelMatrix = ResolveModelMatrix(rawBaseInstance, instanceLinearIndex);");
            sb.AppendLine("    uint commandIndex = ResolveCommandIndex(rawBaseInstance, instanceLinearIndex);");
            if (emitTransformId)
                sb.AppendLine($"    {DefaultVertexShaderGenerator.FragTransformIdName} = uintBitsToFloat(commandIndex);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragViewIndexName} = 0.0;");
            if (emitLodTransitionRole)
                sb.AppendLine($"    {FragLodTransitionRoleName} = (rawBaseInstance & XRE_PREVIOUS_LOD_BASEINSTANCE_FLAG) != 0u ? 1u : 0u;");
            if (emitMaterialId)
                sb.AppendLine($"    {FragMaterialIdName} = commandIndex < uint(Draws.length()) ? Draws[commandIndex].MaterialID : 0u;");
            sb.AppendLine("    vec4 localPos = vec4(Position, 1.0);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragPosLocalName} = localPos.xyz;");
            sb.AppendLine($"    mat4 viewMatrix = {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"    mat4 projMatrix = {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            // CRITICAL: compose MVP in the same order as DefaultVertexShaderGenerator.DeclareMVP:
            //   mvMatrix  = viewMatrix * ModelMatrix
            //   mvpMatrix = projMatrix * mvMatrix
            //   clipPos   = mvpMatrix * localPos
            // FP matrix multiplication is non-associative, so the bracketing must match the CPU
            // path exactly to produce identical depth values. Otherwise the depth pre-pass and
            // the lit pass disagree at sub-ULP precision -> z-fight striping in the lit output.
            sb.AppendLine("    mat4 mvMatrix = viewMatrix * ModelMatrix;");
            sb.AppendLine("    mat4 mvpMatrix = projMatrix * mvMatrix;");
            sb.AppendLine("    vec4 worldPos = ModelMatrix * localPos;");
            sb.AppendLine("    vec4 clipPos = mvpMatrix * localPos;");
            sb.AppendLine("    FragPos = worldPos.xyz;");
            sb.AppendLine();

            if (hasNormals)
                sb.AppendLine("    mat3 normalMatrix = transpose(inverse(mat3(ModelMatrix)));");

            if (hasNormals)
            {
                sb.AppendLine("    FragNorm = normalize(normalMatrix * Normal);");
                if (hasTangents)
                {
                    sb.AppendLine("    FragTan = normalize(normalMatrix * Tangent.xyz);");
                    sb.AppendLine("    FragBinorm = normalize(cross(FragNorm, FragTan) * Tangent.w);");
                }
                else
                {
                    sb.AppendLine("    FragTan = vec3(1.0, 0.0, 0.0);");
                    sb.AppendLine("    FragBinorm = vec3(0.0, 1.0, 0.0);");
                }
            }
            else
            {
                sb.AppendLine("    FragNorm = vec3(0.0, 0.0, 1.0);");
                sb.AppendLine("    FragTan = vec3(1.0, 0.0, 0.0);");
                sb.AppendLine("    FragBinorm = vec3(0.0, 1.0, 0.0);");
            }

            for (int i = 0; i < texCoordBindings.Count && i < 8; ++i)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragUVName, i)} = {texCoordBindings[i]};");
            if (texCoordBindings.Count == 0)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragUVName, 0)} = vec2(0.0);");

            for (int i = 0; i < colorBindings.Count && i < 8; ++i)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragColorName, i)} = {colorBindings[i]};");
            if (colorBindings.Count == 0)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragColorName, 0)} = vec4(1.0);");

            sb.AppendLine("    gl_Position = clipPos;");
            sb.AppendLine("}");

            return new XRShader(EShaderType.Vertex, sb.ToString())
            {
                Name = "GPUIndirect_AutoVS"
            };
        }

        private XRShader CreateGpuIndirectTextVertexShader(bool includeRotations, bool emitTransformId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#version 460");
            sb.AppendLine();
            sb.AppendLine("layout(location = 0) in vec3 Position;");
            sb.AppendLine("layout(location = 1) in vec3 Normal;");
            sb.AppendLine("layout(location = 2) in vec2 TexCoord0;");
            sb.AppendLine();
            sb.AppendLine("layout(std430, binding = 0) buffer GlyphTransformsBuffer { vec4 GlyphTransforms[]; };");
            sb.AppendLine("layout(std430, binding = 1) buffer GlyphTexCoordsBuffer { vec4 GlyphTexCoords[]; };");
            if (includeRotations)
                sb.AppendLine("layout(std430, binding = 2) buffer GlyphRotationsBuffer { float GlyphRotations[]; };");
            sb.AppendLine($"layout(std430, binding = {IndirectTextGlyphOffsetSsboBinding}) readonly buffer GlyphOffsetsBuffer {{ uint GlyphOffsets[]; }};");
            sb.AppendLine($"layout(std430, binding = {IndirectCommandSsboBinding}) readonly buffer CulledCommandsBuffer {{ float culled[]; }};");
            sb.AppendLine($"layout(std430, binding = {InstanceTransformSsboBinding}) readonly buffer TransformBuffer {{ float instanceWorld[]; }};");
            AppendDrawMetadataGlsl(sb);
            sb.AppendLine($"const int COMMAND_FLOATS = {IndirectCommandFloatCount};");
            sb.AppendLine("const int INSTANCE_MATRIX_FLOATS = 16;");
            sb.AppendLine($"const uint XRE_LEGACY_BASEINSTANCE_FLAG = 0x{IndirectLegacyBaseInstanceFlag:X8}u;");
            sb.AppendLine($"const uint XRE_BASEINSTANCE_COMMAND_INDEX_MASK = 0x{IndirectBaseInstanceCommandIndexMask:X8}u;");
            sb.AppendLine();

            sb.AppendLine("layout(location = 0) out vec3 FragPos;");
            sb.AppendLine("layout(location = 1) out vec3 FragNorm;");
            sb.AppendLine("layout(location = 4) out vec2 FragUV0;");
            sb.AppendLine("layout(location = 5) flat out vec4 GlyphUVBounds;");
            sb.AppendLine($"layout(location = 20) out vec3 {DefaultVertexShaderGenerator.FragPosLocalName};");
            if (emitTransformId)
                sb.AppendLine($"layout(location = 21) out float {DefaultVertexShaderGenerator.FragTransformIdName};");
            sb.AppendLine($"layout(location = 22) out float {DefaultVertexShaderGenerator.FragViewIndexName};");
            sb.AppendLine();

            sb.AppendLine($"uniform mat4 {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform mat4 {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine($"uniform bool {EEngineUniform.VRMode};");
            sb.AppendLine();

            sb.AppendLine("uint LoadDrawIdFromCommand(uint commandIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    int base = int(commandIndex) * COMMAND_FLOATS;");
            sb.AppendLine("    if (base + 19 < culled.length())");
            sb.AppendLine("        return floatBitsToUint(culled[base + 19]);");
            sb.AppendLine("    return commandIndex;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint LoadTransformId(uint drawID)");
            sb.AppendLine("{");
            sb.AppendLine("    if (drawID < uint(Draws.length()))");
            sb.AppendLine("        return Draws[drawID].TransformID;");
            sb.AppendLine("    return 0u;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("mat4 LoadWorldMatrix(uint transformID)");
            sb.AppendLine("{");
            sb.AppendLine("    int base = int(transformID) * INSTANCE_MATRIX_FLOATS;");
            sb.AppendLine("    if (base + 15 >= instanceWorld.length())");
            sb.AppendLine("        return mat4(1.0);");
            sb.AppendLine("    vec4 c0 = vec4(instanceWorld[base+0],  instanceWorld[base+1],  instanceWorld[base+2],  instanceWorld[base+3]);");
            sb.AppendLine("    vec4 c1 = vec4(instanceWorld[base+4],  instanceWorld[base+5],  instanceWorld[base+6],  instanceWorld[base+7]);");
            sb.AppendLine("    vec4 c2 = vec4(instanceWorld[base+8],  instanceWorld[base+9],  instanceWorld[base+10], instanceWorld[base+11]);");
            sb.AppendLine("    vec4 c3 = vec4(instanceWorld[base+12], instanceWorld[base+13], instanceWorld[base+14], instanceWorld[base+15]);");
            sb.AppendLine("    return mat4(c0, c1, c2, c3);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint ResolveDrawID(uint rawBaseInstance)");
            sb.AppendLine("{");
            sb.AppendLine("    uint baseIndex = rawBaseInstance & XRE_BASEINSTANCE_COMMAND_INDEX_MASK;");
            sb.AppendLine("    if ((rawBaseInstance & XRE_LEGACY_BASEINSTANCE_FLAG) != 0u)");
            sb.AppendLine("        return LoadDrawIdFromCommand(baseIndex);");
            sb.AppendLine("    return baseIndex;");
            sb.AppendLine("}");
            sb.AppendLine();

            if (includeRotations)
            {
                sb.AppendLine("const float PI = 3.14159265359;");
                sb.AppendLine("mat2 RotationMatrix(float angle)");
                sb.AppendLine("{");
                sb.AppendLine("    float radiansAngle = angle * PI / 180.0;");
                sb.AppendLine("    float s = sin(radiansAngle);");
                sb.AppendLine("    float c = cos(radiansAngle);");
                sb.AppendLine("    return mat2(c, -s, s, c);");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine("    uint drawID = ResolveDrawID(uint(gl_BaseInstance));");
            sb.AppendLine("    mat4 ModelMatrix = LoadWorldMatrix(LoadTransformId(drawID));");
            if (emitTransformId)
                sb.AppendLine($"    {DefaultVertexShaderGenerator.FragTransformIdName} = uintBitsToFloat(drawID);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragViewIndexName} = 0.0;");
            sb.AppendLine("    uint glyphBase = drawID < uint(GlyphOffsets.length()) ? GlyphOffsets[drawID] : 0u;");
            sb.AppendLine("    uint glyphIndex = glyphBase + uint(gl_InstanceID);");
            sb.AppendLine("    vec4 tfm = GlyphTransforms[glyphIndex];");
            sb.AppendLine("    vec4 uv = GlyphTexCoords[glyphIndex];");
            if (includeRotations)
            {
                sb.AppendLine("    float rot = GlyphRotations[glyphIndex];");
                sb.AppendLine("    vec2 glyphPos = (tfm.xy + (TexCoord0.xy * tfm.zw)) * RotationMatrix(rot);");
            }
            else
            {
                sb.AppendLine("    vec2 glyphPos = tfm.xy + (TexCoord0.xy * tfm.zw);");
            }
            sb.AppendLine("    vec4 localPos = vec4(glyphPos, 0.0, 1.0);");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragPosLocalName} = localPos.xyz;");
            sb.AppendLine($"    mat4 viewMatrix = {EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix};");
            sb.AppendLine("    vec4 worldPos = ModelMatrix * localPos;");
            sb.AppendLine($"    vec4 clipPos = {EEngineUniform.ProjMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix} * viewMatrix * worldPos;");
            sb.AppendLine("    FragPos = worldPos.xyz;");
            sb.AppendLine("    mat3 normalMatrix = transpose(inverse(mat3(ModelMatrix)));");
            sb.AppendLine("    FragNorm = normalize(normalMatrix * Normal);");
            sb.AppendLine("    GlyphUVBounds = uv;");
            sb.AppendLine("    FragUV0 = mix(uv.xy, uv.zw, Position.xy);");
            sb.AppendLine("    gl_Position = clipPos;");
            sb.AppendLine("}");

            return new XRShader(EShaderType.Vertex, sb.ToString())
            {
                Name = includeRotations ? "GPUIndirect_TextRot_AutoVS" : "GPUIndirect_Text_AutoVS"
            };
        }

        private static bool TryDetectTextVertexShader(IEnumerable<XRShader?> shaders, out bool includeRotations)
        {
            includeRotations = false;

            foreach (XRShader? shader in shaders)
            {
                if (shader is null || shader.Type != EShaderType.Vertex)
                    continue;

                string? source = shader.Source?.Text;
                if (string.IsNullOrEmpty(source))
                    continue;

                bool hasGlyphTransforms = source.Contains("GlyphTransformsBuffer", StringComparison.Ordinal);
                bool hasGlyphTexCoords = source.Contains("GlyphTexCoordsBuffer", StringComparison.Ordinal);
                if (!hasGlyphTransforms || !hasGlyphTexCoords)
                    continue;

                // Skip batched text shaders (UITextBatched) that handle their own per-instance
                // data via TextInstanceBuffer. The GPU-indirect replacement VS doesn't support that path.
                if (source.Contains("TextInstanceBuffer", StringComparison.Ordinal))
                    continue;

                includeRotations = source.Contains("GlyphRotationsBuffer", StringComparison.Ordinal);
                return true;
            }

            return false;
        }

        private static bool FragmentConsumesTransformId(IEnumerable<XRShader?> shaders)
        {
            foreach (XRShader? shader in shaders)
            {
                if (shader is null || shader.Type != EShaderType.Fragment)
                    continue;

                string? source = shader.Source?.Text;
                if (string.IsNullOrEmpty(source))
                    continue;

                if (SourceContainsGlslIdentifier(source, DefaultVertexShaderGenerator.FragTransformIdName))
                    return true;
            }

            return false;
        }

        private static void AugmentIndirectFragmentShaders(List<XRShader> shaders, out bool emitLodTransitionRole)
        {
            emitLodTransitionRole = false;

            for (int i = 0; i < shaders.Count; ++i)
            {
                XRShader shader = shaders[i];
                if (shader.Type != EShaderType.Fragment)
                    continue;

                string? source = shader.Source?.Text;
                if (string.IsNullOrWhiteSpace(source) || !TryAugmentIndirectFragmentShader(source, out string augmentedSource))
                    continue;

                shaders[i] = new XRShader(EShaderType.Fragment, augmentedSource)
                {
                    Name = shader.Name
                };
                emitLodTransitionRole = true;
            }
        }

        private static bool TryAugmentIndirectFragmentShader(string source, out string augmentedSource)
        {
            const string mainSignature = "void main";
            augmentedSource = source;

            int mainIndex = source.IndexOf(mainSignature, StringComparison.Ordinal);
            if (mainIndex < 0)
                return false;

            var helper = new StringBuilder();
            helper.AppendLine();
            AppendGlslVariableDeclarationIfNeeded(
                helper,
                source,
                DefaultVertexShaderGenerator.FragTransformIdName,
                $"layout(location = 21) in float {DefaultVertexShaderGenerator.FragTransformIdName};");
            AppendGlslVariableDeclarationIfNeeded(
                helper,
                source,
                FragLodTransitionRoleName,
                $"layout(location = {FragLodTransitionRoleLocation}) flat in uint {FragLodTransitionRoleName};");
            helper.AppendLine($"layout(std430, binding = {LodTransitionSsboBinding}) readonly buffer XreLodTransitionBuffer {{ uint xreLodTransitions[]; }};");
            helper.AppendLine("const uint XRE_LOD_TRANSITION_ACTIVE = 1u;");
            helper.AppendLine("const uint XRE_LOD_TRANSITION_UINTS = 4u;");
            helper.AppendLine();
            helper.AppendLine("float XRE_BayerDither4x4(vec2 fragCoord)");
            helper.AppendLine("{");
            helper.AppendLine("    ivec2 p = ivec2(mod(floor(fragCoord), 4.0));");
            helper.AppendLine("    const float bayer[16] = float[16](");
            helper.AppendLine("        0.0, 8.0, 2.0, 10.0,");
            helper.AppendLine("        12.0, 4.0, 14.0, 6.0,");
            helper.AppendLine("        3.0, 11.0, 1.0, 9.0,");
            helper.AppendLine("        15.0, 7.0, 13.0, 5.0);");
            helper.AppendLine("    return (bayer[p.y * 4 + p.x] + 0.5) / 16.0;");
            helper.AppendLine("}");
            helper.AppendLine();
            helper.AppendLine("void XRE_ApplyLodTransitionDither()");
            helper.AppendLine("{");
            helper.AppendLine($"    uint commandIndex = floatBitsToUint({DefaultVertexShaderGenerator.FragTransformIdName});");
            helper.AppendLine("    uint base = commandIndex * XRE_LOD_TRANSITION_UINTS;");
            helper.AppendLine("    if (base + 3u >= uint(xreLodTransitions.length()))");
            helper.AppendLine("        return;");
            helper.AppendLine("    uint flags = xreLodTransitions[base + 2u];");
            helper.AppendLine("    if ((flags & XRE_LOD_TRANSITION_ACTIVE) == 0u)");
            helper.AppendLine("        return;");
            helper.AppendLine("    float progress = clamp(uintBitsToFloat(xreLodTransitions[base + 3u]), 0.0, 1.0);");
            helper.AppendLine($"    float coverage = {FragLodTransitionRoleName} != 0u ? (1.0 - progress) : progress;");
            helper.AppendLine("    if (coverage <= 0.0)");
            helper.AppendLine("        discard;");
            helper.AppendLine("    if (coverage >= 1.0)");
            helper.AppendLine("        return;");
            helper.AppendLine("    if (coverage < XRE_BayerDither4x4(gl_FragCoord.xy))");
            helper.AppendLine("        discard;");
            helper.AppendLine("}");

            string helperSource = helper.ToString();
            augmentedSource = source.Insert(mainIndex, helperSource);
            mainIndex += helperSource.Length;

            int braceIndex = augmentedSource.IndexOf('{', mainIndex);
            if (braceIndex < 0)
                return false;

            augmentedSource = augmentedSource.Insert(braceIndex + 1, "\n    XRE_ApplyLodTransitionDither();");
            return true;
        }

        private static bool SourceContainsGlslIdentifier(string source, string identifier)
            => Regex.IsMatch(
                source,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant);

        private static bool SourceDeclaresGlslVariable(string source, string identifier)
            => Regex.IsMatch(
                source,
                $@"(?:^|[;\r\n])\s*(?:layout\s*\([^)]*\)\s*)?(?:(?:flat|smooth|noperspective|centroid|sample|patch|invariant|precise|highp|mediump|lowp)\s+)*(?:in|out|uniform|varying)\s+[A-Za-z_][A-Za-z0-9_]*\s+{Regex.Escape(identifier)}\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static void AppendGlslVariableDeclarationIfNeeded(
            StringBuilder helper,
            string source,
            string identifier,
            string declaration)
        {
            if (SourceDeclaresUnconditionalGlslVariable(source, identifier, out string conditionalDeclarationExpression))
                return;

            if (conditionalDeclarationExpression.Length == 0)
            {
                helper.AppendLine(declaration);
                return;
            }

            helper.AppendLine($"#if !({conditionalDeclarationExpression})");
            helper.AppendLine(declaration);
            helper.AppendLine("#endif");
        }

        private static bool SourceDeclaresUnconditionalGlslVariable(
            string source,
            string identifier,
            out string conditionalDeclarationExpression)
        {
            conditionalDeclarationExpression = string.Empty;
            var conditionalDeclarations = new List<string>();
            var conditionStack = new List<GlslPreprocessorCondition>();

            int index = 0;
            while (index < source.Length)
            {
                int lineBreak = source.IndexOf('\n', index);
                int lineEnd = lineBreak >= 0 ? lineBreak : source.Length;
                int lineEndExclusive = lineEnd > index && source[lineEnd - 1] == '\r'
                    ? lineEnd - 1
                    : lineEnd;
                string line = source[index..lineEndExclusive].TrimStart();

                if (TryReadGlslPreprocessorDirective(line, out string directive, out string argument))
                {
                    UpdateGlslConditionStack(conditionStack, directive, argument);
                }
                else if (SourceDeclaresGlslVariable(line, identifier))
                {
                    if (conditionStack.Count == 0)
                        return true;

                    conditionalDeclarations.Add(BuildEffectiveGlslCondition(conditionStack));
                }

                if (lineBreak < 0)
                    break;

                index = lineBreak + 1;
            }

            conditionalDeclarationExpression = CombineGlslConditions(conditionalDeclarations, "||");
            return false;
        }

        private static bool TryReadGlslPreprocessorDirective(string trimmedLine, out string directive, out string argument)
        {
            directive = string.Empty;
            argument = string.Empty;

            if (trimmedLine.Length == 0 || trimmedLine[0] != '#')
                return false;

            int index = 1;
            while (index < trimmedLine.Length && char.IsWhiteSpace(trimmedLine[index]))
                ++index;

            int directiveStart = index;
            while (index < trimmedLine.Length && char.IsLetter(trimmedLine[index]))
                ++index;

            if (index == directiveStart)
                return false;

            directive = trimmedLine[directiveStart..index];
            argument = TrimGlslLineComment(trimmedLine[index..].Trim());
            return true;
        }

        private static string TrimGlslLineComment(string text)
        {
            int commentIndex = text.IndexOf("//", StringComparison.Ordinal);
            return commentIndex < 0 ? text : text[..commentIndex].TrimEnd();
        }

        private static void UpdateGlslConditionStack(
            List<GlslPreprocessorCondition> conditionStack,
            string directive,
            string argument)
        {
            switch (directive)
            {
                case "if":
                    conditionStack.Add(new GlslPreprocessorCondition(NormalizeGlslCondition(argument), NormalizeGlslCondition(argument)));
                    break;
                case "ifdef":
                    {
                        string condition = $"defined({ReadGlslMacroIdentifier(argument)})";
                        conditionStack.Add(new GlslPreprocessorCondition(condition, condition));
                        break;
                    }
                case "ifndef":
                    {
                        string condition = $"!defined({ReadGlslMacroIdentifier(argument)})";
                        conditionStack.Add(new GlslPreprocessorCondition(condition, condition));
                        break;
                    }
                case "elif":
                    if (conditionStack.Count == 0)
                        break;

                    GlslPreprocessorCondition elifCondition = conditionStack[^1];
                    string elifArgument = NormalizeGlslCondition(argument);
                    string elifBranchCondition = CombineGlslConditions(
                        [NegateGlslCondition(elifCondition.CoveredCondition), elifArgument],
                        "&&");
                    conditionStack[^1] = new GlslPreprocessorCondition(
                        elifBranchCondition,
                        CombineGlslConditions([elifCondition.CoveredCondition, elifArgument], "||"));
                    break;
                case "else":
                    if (conditionStack.Count == 0)
                        break;

                    GlslPreprocessorCondition elseCondition = conditionStack[^1];
                    conditionStack[^1] = new GlslPreprocessorCondition(
                        NegateGlslCondition(elseCondition.CoveredCondition),
                        "1");
                    break;
                case "endif":
                    if (conditionStack.Count > 0)
                        conditionStack.RemoveAt(conditionStack.Count - 1);
                    break;
            }
        }

        private static string NormalizeGlslCondition(string condition)
            => condition.Length == 0 ? "0" : condition;

        private static string ReadGlslMacroIdentifier(string argument)
        {
            int index = 0;
            while (index < argument.Length && char.IsWhiteSpace(argument[index]))
                ++index;

            int start = index;
            while (index < argument.Length && (char.IsLetterOrDigit(argument[index]) || argument[index] == '_'))
                ++index;

            return index == start ? "0" : argument[start..index];
        }

        private static string BuildEffectiveGlslCondition(List<GlslPreprocessorCondition> conditionStack)
        {
            var activeConditions = new List<string>(conditionStack.Count);
            for (int i = 0; i < conditionStack.Count; ++i)
                activeConditions.Add(conditionStack[i].CurrentCondition);

            return CombineGlslConditions(activeConditions, "&&");
        }

        private static string CombineGlslConditions(IReadOnlyList<string> conditions, string op)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < conditions.Count; ++i)
            {
                string condition = conditions[i];
                if (condition.Length == 0)
                    continue;

                if (sb.Length > 0)
                    sb.Append(' ').Append(op).Append(' ');

                sb.Append('(').Append(condition).Append(')');
            }

            return sb.Length == 0 ? "0" : sb.ToString();
        }

        private static string NegateGlslCondition(string condition)
            => $"!({NormalizeGlslCondition(condition)})";

        private readonly struct GlslPreprocessorCondition(string currentCondition, string coveredCondition)
        {
            public readonly string CurrentCondition = currentCondition;
            public readonly string CoveredCondition = coveredCondition;
        }

        private void DetachIndirectTextBatchBuffers(XRMeshRenderer? vaoRenderer)
        {
            if (vaoRenderer?.Buffers is null)
                return;

            vaoRenderer.Buffers.Remove(GlyphTransformsBufferName);
            vaoRenderer.Buffers.Remove(GlyphTexCoordsBufferName);
            vaoRenderer.Buffers.Remove(GlyphRotationsBufferName);
            vaoRenderer.Buffers.Remove(GlyphOffsetsBufferName);
        }

        private bool EnsureIndirectTextBatchBuffers(XRMeshRenderer vaoRenderer, uint requiredGlyphCount, uint requiredDrawId, bool includeRotations)
        {
            uint requiredCapacity = requiredGlyphCount == 0 ? 1u : XRMath.NextPowerOfTwo(requiredGlyphCount);
            if (requiredCapacity == 0)
                requiredCapacity = requiredGlyphCount == 0 ? 1u : requiredGlyphCount;

            uint requiredOffsetCapacity = XRMath.NextPowerOfTwo(requiredDrawId + 1u);
            if (requiredOffsetCapacity == 0u)
                requiredOffsetCapacity = requiredDrawId + 1u;

            if (_indirectTextTransformsBuffer is null)
            {
                _indirectTextTransformsBuffer = new XRDataBuffer(
                    GlyphTransformsBufferName,
                    EBufferTarget.ShaderStorageBuffer,
                    requiredCapacity,
                    EComponentType.Float,
                    4,
                    false,
                    false)
                {
                    Usage = EBufferUsage.StreamDraw,
                    BindingIndexOverride = 0,
                    DisposeOnPush = false
                };
                _indirectTextBuffersNeedFullPush = true;
            }
            else if (_indirectTextTransformsBuffer.ElementCount < requiredCapacity)
            {
                _indirectTextTransformsBuffer.Resize(requiredCapacity);
                _indirectTextBuffersNeedFullPush = true;
            }

            if (_indirectTextTexCoordsBuffer is null)
            {
                _indirectTextTexCoordsBuffer = new XRDataBuffer(
                    GlyphTexCoordsBufferName,
                    EBufferTarget.ShaderStorageBuffer,
                    requiredCapacity,
                    EComponentType.Float,
                    4,
                    false,
                    false)
                {
                    Usage = EBufferUsage.StreamDraw,
                    BindingIndexOverride = 1,
                    DisposeOnPush = false
                };
                _indirectTextBuffersNeedFullPush = true;
            }
            else if (_indirectTextTexCoordsBuffer.ElementCount < requiredCapacity)
            {
                _indirectTextTexCoordsBuffer.Resize(requiredCapacity);
                _indirectTextBuffersNeedFullPush = true;
            }

            if (includeRotations)
            {
                if (_indirectTextRotationsBuffer is null)
                {
                    _indirectTextRotationsBuffer = new XRDataBuffer(
                        GlyphRotationsBufferName,
                        EBufferTarget.ShaderStorageBuffer,
                        requiredCapacity,
                        EComponentType.Float,
                        1,
                        false,
                        false)
                    {
                        Usage = EBufferUsage.StreamDraw,
                        BindingIndexOverride = 2,
                        DisposeOnPush = false
                    };
                    _indirectTextBuffersNeedFullPush = true;
                }
                else if (_indirectTextRotationsBuffer.ElementCount < requiredCapacity)
                {
                    _indirectTextRotationsBuffer.Resize(requiredCapacity);
                    _indirectTextBuffersNeedFullPush = true;
                }
            }

            if (_indirectTextTransformsBuffer is null || _indirectTextTexCoordsBuffer is null)
                return false;

            vaoRenderer.Buffers[GlyphTransformsBufferName] = _indirectTextTransformsBuffer;
            vaoRenderer.Buffers[GlyphTexCoordsBufferName] = _indirectTextTexCoordsBuffer;

            if (_indirectTextGlyphOffsetsBuffer is null)
            {
                _indirectTextGlyphOffsetsBuffer = new XRDataBuffer(
                    GlyphOffsetsBufferName,
                    EBufferTarget.ShaderStorageBuffer,
                    requiredOffsetCapacity,
                    EComponentType.UInt,
                    1,
                    false,
                    true)
                {
                    Usage = EBufferUsage.StreamDraw,
                    BindingIndexOverride = IndirectTextGlyphOffsetSsboBinding,
                    DisposeOnPush = false
                };
                _indirectTextBuffersNeedFullPush = true;
            }
            else if (_indirectTextGlyphOffsetsBuffer.ElementCount < requiredOffsetCapacity)
            {
                _indirectTextGlyphOffsetsBuffer.Resize(requiredOffsetCapacity);
                _indirectTextBuffersNeedFullPush = true;
            }

            vaoRenderer.Buffers[GlyphOffsetsBufferName] = _indirectTextGlyphOffsetsBuffer;

            if (includeRotations && _indirectTextRotationsBuffer is not null)
                vaoRenderer.Buffers[GlyphRotationsBufferName] = _indirectTextRotationsBuffer;
            else
                vaoRenderer.Buffers.Remove(GlyphRotationsBufferName);

            return true;
        }

        private bool PrepareIndirectTextBatchData(
            GPURenderPassCollection renderPasses,
            GPUScene scene,
            XRMeshRenderer? vaoRenderer,
            uint batchOffset,
            uint batchCount,
            bool includeRotations)
        {
            if (vaoRenderer is null || batchCount == 0)
                return false;

            XRDataBuffer? indirectBuffer = renderPasses.IndirectDrawBuffer;
            if (indirectBuffer is null)
                return false;

            var sources = new List<(uint drawID, uint glyphCount, XRDataBuffer transforms, XRDataBuffer texCoords, XRDataBuffer? rotations)>((int)batchCount);
            ulong totalGlyphs64 = 0;
            uint maxDrawId = 0u;

            for (uint local = 0; local < batchCount; ++local)
            {
                uint drawIndex = batchOffset + local;
                if (drawIndex >= indirectBuffer.ElementCount)
                    break;

                DrawElementsIndirectCommand drawCommand = indirectBuffer.GetDataRawAtIndex<DrawElementsIndirectCommand>(drawIndex);
                uint glyphCount = drawCommand.InstanceCount;
                if (glyphCount == 0)
                    continue;

                uint drawID = drawCommand.BaseInstance & IndirectBaseInstanceCommandIndexMask;
                if ((drawCommand.BaseInstance & IndirectLegacyBaseInstanceFlag) != 0u)
                {
                    if (drawID >= renderPasses.CulledSceneToRenderBuffer.ElementCount)
                    {
                        GpuWarn(LogCategory.Draw,
                            "Indirect text batch preparation failed: legacy culled index {0} out of range for drawIndex={1}.",
                            drawID,
                            drawIndex);
                        return false;
                    }

                    GPUIndirectRenderCommand legacyCulledCommand = renderPasses.CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(drawID);
                    drawID = legacyCulledCommand.Reserved1;
                }

                if (!scene.TryGetSourceCommand(drawID, out IRenderCommandMesh? sourceCommand) || sourceCommand?.Mesh is null)
                {
                    if (drawIndex < renderPasses.CulledSceneToRenderBuffer.ElementCount)
                    {
                        GPUIndirectRenderCommand culledCommand = renderPasses.CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(drawIndex);
                        uint sourceCommandIndex = culledCommand.Reserved1;
                        scene.TryGetSourceCommand(sourceCommandIndex, out sourceCommand);
                    }

                    if (sourceCommand?.Mesh is null)
                    {
                        GpuWarn(LogCategory.Draw,
                            "Indirect text batch preparation failed: unable to resolve source command for drawID={0}.",
                            drawID);
                        return false;
                    }
                }

                XRMeshRenderer sourceRenderer = sourceCommand.Mesh;
                if (!sourceRenderer.Buffers.TryGetValue(GlyphTransformsBufferName, out XRDataBuffer? sourceTransforms)
                    || !sourceRenderer.Buffers.TryGetValue(GlyphTexCoordsBufferName, out XRDataBuffer? sourceTexCoords))
                {
                    GpuWarn(LogCategory.Draw,
                        "Indirect text batch preparation failed: command {0} is missing glyph SSBOs.",
                        drawID);
                    return false;
                }

                XRDataBuffer? sourceRotations = null;
                if (includeRotations && !sourceRenderer.Buffers.TryGetValue(GlyphRotationsBufferName, out sourceRotations))
                {
                    GpuWarn(LogCategory.Draw,
                        "Indirect text batch preparation failed: command {0} is missing '{1}'.",
                        drawID,
                        GlyphRotationsBufferName);
                    return false;
                }

                totalGlyphs64 += glyphCount;
                if (totalGlyphs64 > uint.MaxValue)
                {
                    GpuWarn(LogCategory.Draw, "Indirect text batch preparation failed: glyph count overflow ({0}).", totalGlyphs64);
                    return false;
                }

                sources.Add((drawID, glyphCount, sourceTransforms, sourceTexCoords, sourceRotations));
                maxDrawId = Math.Max(maxDrawId, drawID);
            }

            uint totalGlyphs = (uint)totalGlyphs64;
            if (totalGlyphs == 0)
                return false;

            if (!EnsureIndirectTextBatchBuffers(vaoRenderer, totalGlyphs, maxDrawId, includeRotations))
                return false;

            uint writeOffset = 0;
            foreach (var source in sources)
            {
                _indirectTextGlyphOffsetsBuffer!.SetDataRawAtIndex(source.drawID, writeOffset);

                uint copyCount = source.glyphCount;
                copyCount = Math.Min(copyCount, source.transforms.ElementCount);
                copyCount = Math.Min(copyCount, source.texCoords.ElementCount);
                if (includeRotations && source.rotations is not null)
                    copyCount = Math.Min(copyCount, source.rotations.ElementCount);

                if (copyCount < source.glyphCount)
                {
                    GpuWarn(LogCategory.Draw,
                        "Indirect text glyph data truncated for drawID={0}: requested={1}, copied={2}.",
                        source.drawID,
                        source.glyphCount,
                        copyCount);
                }

                for (uint glyph = 0; glyph < copyCount; ++glyph)
                {
                    _indirectTextTransformsBuffer!.SetVector4(writeOffset + glyph, source.transforms.GetVector4(glyph));
                    _indirectTextTexCoordsBuffer!.SetVector4(writeOffset + glyph, source.texCoords.GetVector4(glyph));
                    if (includeRotations && source.rotations is not null && _indirectTextRotationsBuffer is not null)
                        _indirectTextRotationsBuffer.SetFloat(writeOffset + glyph, source.rotations.GetFloat(glyph));
                }

                for (uint glyph = copyCount; glyph < source.glyphCount; ++glyph)
                {
                    _indirectTextTransformsBuffer!.SetVector4(writeOffset + glyph, Vector4.Zero);
                    _indirectTextTexCoordsBuffer!.SetVector4(writeOffset + glyph, Vector4.Zero);
                    if (includeRotations && _indirectTextRotationsBuffer is not null)
                        _indirectTextRotationsBuffer.SetFloat(writeOffset + glyph, 0.0f);
                }

                writeOffset += source.glyphCount;
            }

            if (_indirectTextBuffersNeedFullPush)
            {
                _indirectTextTransformsBuffer!.PushData();
                _indirectTextTexCoordsBuffer!.PushData();
                _indirectTextGlyphOffsetsBuffer!.PushData();
                if (includeRotations && _indirectTextRotationsBuffer is not null)
                    _indirectTextRotationsBuffer.PushData();
                _indirectTextBuffersNeedFullPush = false;
            }
            else
            {
                _indirectTextTransformsBuffer!.PushSubData();
                _indirectTextTexCoordsBuffer!.PushSubData();
                _indirectTextGlyphOffsetsBuffer!.PushSubData();
                if (includeRotations && _indirectTextRotationsBuffer is not null)
                    _indirectTextRotationsBuffer.PushSubData();
            }

            return true;
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
                sb.AppendLine($"layout(location={location++}) in vec4 {ECommonBufferType.Tangent};");

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
            sb.AppendLine("layout(location=2) out vec3 FragTan;");
            sb.AppendLine("layout(location=3) out vec3 FragBinorm;");

            for (int i = 0; i < texCoordBindings.Count && i < 8; ++i)
                sb.AppendLine($"layout(location={4 + i}) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, i)};");
            if (texCoordBindings.Count == 0)
                sb.AppendLine($"layout(location=4) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, 0)};");

            if (colorBindings.Count == 0)
                sb.AppendLine($"layout(location=12) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, 0)};");
            else
                for (int i = 0; i < colorBindings.Count && i < 8; ++i)
                    sb.AppendLine($"layout(location={12 + i}) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, i)};");

            sb.AppendLine($"layout(location=20) out vec3 {DefaultVertexShaderGenerator.FragPosLocalName};");
            sb.AppendLine($"layout(location=22) out float {DefaultVertexShaderGenerator.FragViewIndexName};");

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
            sb.AppendLine("    FragPos = worldPos.xyz;");
            sb.AppendLine($"    {DefaultVertexShaderGenerator.FragViewIndexName} = 0.0;");

            if (hasNormals)
                sb.AppendLine("    mat3 normalMatrix = transpose(inverse(mat3(ModelMatrix)));");

            if (hasNormals)
            {
                sb.AppendLine("    FragNorm = normalize(normalMatrix * Normal);");
                if (hasTangents)
                {
                    sb.AppendLine("    FragTan = normalize(normalMatrix * Tangent.xyz);");
                    sb.AppendLine("    FragBinorm = normalize(cross(FragNorm, FragTan) * Tangent.w);");
                }
                else
                {
                    sb.AppendLine("    FragTan = vec3(1.0, 0.0, 0.0);");
                    sb.AppendLine("    FragBinorm = vec3(0.0, 1.0, 0.0);");
                }
            }
            else
            {
                sb.AppendLine("    FragNorm = vec3(0.0, 0.0, 1.0);");
                sb.AppendLine("    FragTan = vec3(1.0, 0.0, 0.0);");
                sb.AppendLine("    FragBinorm = vec3(0.0, 1.0, 0.0);");
            }

            for (int i = 0; i < texCoordBindings.Count && i < 8; ++i)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragUVName, i)} = {texCoordBindings[i]};");
            if (texCoordBindings.Count == 0)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragUVName, 0)} = vec2(0.0);");

            for (int i = 0; i < colorBindings.Count && i < 8; ++i)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragColorName, i)} = {colorBindings[i]};");
            if (colorBindings.Count == 0)
                sb.AppendLine($"    {string.Format(DefaultVertexShaderGenerator.FragColorName, 0)} = vec4(1.0);");

            sb.AppendLine("    gl_Position = clipPos;");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static bool HasRendererBuffer(XRMeshRenderer? renderer, string binding)
        {
            if (renderer is null)
                return false;

            if (renderer.Mesh?.Buffers is not null && renderer.Mesh.Buffers.TryGetValue(binding, out _))
                return true;

            return renderer.Buffers is not null && renderer.Buffers.TryGetValue(binding, out _);
        }

        private static List<string> GetRendererBuffersWithPrefix(XRMeshRenderer? renderer, string prefix)
        {
            if (renderer is null)
                return [];

            HashSet<string> bindings = new(StringComparer.Ordinal);
            if (renderer.Mesh?.Buffers is IEventDictionary<string, XRDataBuffer> meshBuffers)
            {
                foreach (var kvp in meshBuffers)
                {
                    if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                        bindings.Add(kvp.Key);
                }
            }

            if (renderer.Buffers is IEventDictionary<string, XRDataBuffer> rendererBuffers)
            {
                foreach (var kvp in rendererBuffers)
                {
                    if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                        bindings.Add(kvp.Key);
                }
            }

            return [.. bindings.OrderBy(name => name, StringComparer.Ordinal)];
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
                Debug.MeshesWarning("No active renderer for batched indirect path.");
                return;
            }

            // GPU-indirect shaders fetch per-draw transforms from SSBOs, so there is no single
            // CPU-side model matrix that correctly describes an entire indirect batch.
            // Keep the legacy ModelMatrix uniform at identity and avoid GPU readbacks here.
            Matrix4x4 defaultModelMatrix = Matrix4x4.Identity;

            // Batched range draws already provide explicit offset/count, so avoid
            // global count-buffer semantics here.
            XRDataBuffer? dispatchParameterBuffer = null;
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
            //        Debug.MeshesWarning("CPU indirect build produced zero draw commands for batched path. Skipping indirect draw dispatch.");
            //        return;
            //    }

            //    Debug.Meshes($"CPU indirect build generated {cpuBuiltCount} draw command(s) for batched path (requested {requestedDraws}).");

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

            var activeBatches = CoalesceContiguousBatches(overrideBatches ?? batches);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectBatchMerge(batches.Count, activeBatches.Count);

            if (activeBatches.Count != batches.Count)
            {
                GpuDebug(
                    LogCategory.Draw,
                    "Coalesced draw batches for indirect dispatch: requested={0}, merged={1}",
                    batches.Count,
                    activeBatches.Count);
            }
            XRDataBuffer? instanceTransformBuffer = scene.TransformBuffer;
            XRDataBuffer? instanceSourceIndexBuffer = renderPasses.InstanceSourceIndexBuffer;
            bool useGpuInstanceTransforms = instanceTransformBuffer is not null;

            bool instrumentedStrategy = IsInstrumentedStrategy(renderPasses);
            if (instrumentedStrategy && DebugSettings.DumpIndirectArguments && materialMap.Count > 0)
            {
                string[] sample = materialMap
                    .Select(kvp => $"{kvp.Key}:{kvp.Value?.Name ?? "<null>"}")
                    .Take(16)
                    .ToArray();
                GpuDebug($"MaterialMap snapshot ({materialMap.Count} entries){(materialMap.Count > sample.Length ? " (truncated)" : string.Empty)}: {string.Join(", ", sample)}");
            }
            else if (instrumentedStrategy && DebugSettings.DumpIndirectArguments)
            {
                GpuDebug("MaterialMap snapshot: empty");
            }

            if (instrumentedStrategy && DebugSettings.DumpIndirectArguments)
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

            // === One-shot diagnostic: dump first N indirect draw commands to verify GPU data ===
            if (instrumentedStrategy)
                DumpIndirectCommandsOneShot(indirectDrawBuffer, activeBatches, currentRenderPass);

            // O-7: issue ONE coalesced MemoryBarrier covering the entire indirect batch loop
            // instead of one per DispatchRenderIndirectRange call. The compute pass that builds
            // the indirect/parameter buffers already completed earlier this frame, so a single
            // barrier ahead of the draw loop is sufficient for correctness. Per-batch
            // emitBarrier=false suppresses the redundant per-range barriers.
            if (dispatchParameterBuffer is not null && activeBatches.Count > 0)
            {
                var batchLoopRenderer = AbstractRenderer.Current;
                batchLoopRenderer?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
            }

            bool textBatchBuffersAttached = false;
            foreach (var batch in activeBatches)
            {
                if (batch.Count == 0)
                    continue;

                uint effectiveCount = batch.Count;
                //if (usingCpuIndirect)
                //{
                //    if (batch.Offset >= cpuBuiltCount)
                //    {
                //        Debug.Meshes($"Skipping batch at offset {batch.Offset} - beyond CPU-built draw count {cpuBuiltCount}.");
                //        continue;
                //    }

                //    uint maxAvailable = cpuBuiltCount - batch.Offset;
                //    if (effectiveCount > maxAvailable)
                //    {
                //        Debug.MeshesWarning($"Clamping CPU indirect batch at offset {batch.Offset} from {effectiveCount} to {maxAvailable} draw(s).");
                //        effectiveCount = maxAvailable;
                //    }
                //}

                // Resolve material from MaterialID (with CPU override fallback)
                uint lookupMaterialId = batch.MaterialID;
                if (lookupMaterialId == uint.MaxValue && cpuMaterialOrder is not null && batch.Offset < cpuMaterialOrder.Count)
                    lookupMaterialId = cpuMaterialOrder[(int)batch.Offset];

                XRMaterial? overrideMaterial = RuntimeEngine.Rendering.State.OverrideMaterial;

                uint effectiveMaterialId = lookupMaterialId;
                XRMaterial? sourceMaterial = null;
                if (lookupMaterialId != 0)
                    materialMap.TryGetValue(lookupMaterialId, out sourceMaterial);

                XRMaterial? material = ResolveEffectiveGpuMaterial(sourceMaterial, overrideMaterial);
                if (material is not null)
                    effectiveMaterialId = (uint)material.GetHashCode();

                if (material is null)
                {
                    string reason = lookupMaterialId == 0
                        ? "ID=0 (invalid)"
                        : "material not found in map";
                    GpuDebug($"Material lookup miss for ID={lookupMaterialId} (batch offset={batch.Offset}, count={effectiveCount}): {reason}");

                    XRMaterial? invalidMaterial = XRMaterial.InvalidMaterial;
                    if (invalidMaterial is null)
                    {
                        GpuWarn(
                            LogCategory.Draw,
                            "Skipping batch at offset={0}, count={1}: no invalid material fallback is available.",
                            batch.Offset,
                            effectiveCount);
                        continue;
                    }

                    material = invalidMaterial;
                    effectiveMaterialId = (uint)invalidMaterial.GetHashCode();
                }

                if (batch.Offset >= renderPasses.VisibleCommandCount)
                {
                    Debug.MeshesWarning($"Batch offset {batch.Offset} out of range for visible count {renderPasses.VisibleCommandCount}; skipping batch.");
                    continue;
                }

                uint available = renderPasses.VisibleCommandCount - batch.Offset;
                if (effectiveCount > available)
                {
                    Debug.MeshesWarning($"Clamping batch at offset {batch.Offset} from {effectiveCount} to {available} draw(s) due to visible count bounds.");
                    effectiveCount = available;
                }

                if (effectiveCount == 0)
                    continue;

                bool isTextBatch = TryDetectTextVertexShader(material.Shaders, out bool includeTextRotations);
                if (isTextBatch)
                {
                    if (!PrepareIndirectTextBatchData(renderPasses, scene, vaoRenderer, batch.Offset, effectiveCount, includeTextRotations))
                    {
                        GpuWarn(
                            LogCategory.Draw,
                            "Skipping text batch at offset={0}, count={1}: failed to prepare indirect glyph buffers.",
                            batch.Offset,
                            effectiveCount);
                        DetachIndirectTextBatchBuffers(vaoRenderer);
                        textBatchBuffersAttached = false;
                        continue;
                    }

                    textBatchBuffersAttached = true;
                }
                else if (textBatchBuffersAttached)
                {
                    DetachIndirectTextBatchBuffers(vaoRenderer);
                    textBatchBuffersAttached = false;
                }

                // Ensure/Use graphics program (combined MVP)
                var program = EnsureCombinedProgram(effectiveMaterialId, material, vaoRenderer);
                if (program is null)
                    continue;

                // Set material uniforms
                renderer.SetMaterialUniforms(material, program);
                renderer.ApplyRenderParameters(material.RenderOptions);

                GpuDebug("Batch draw: materialID={0} offset={1} count={2}", effectiveMaterialId, batch.Offset, effectiveCount);

                DispatchRenderIndirectRange(
                    indirectDrawBuffer,
                    vaoRenderer,
                    renderPasses.CulledSceneToRenderBuffer,
                    scene.DrawMetadataBuffer,
                    scene.LodTransitionBuffer,
                    instanceTransformBuffer,
                    instanceSourceIndexBuffer,
                    useGpuInstanceTransforms && !isTextBatch,
                    batch.Offset,
                    effectiveCount,
                    dispatchParameterBuffer,
                    program,
                    camera,
                        defaultModelMatrix,
                        emitBarrier: false);
            }

            if (textBatchBuffersAttached)
                DetachIndirectTextBatchBuffers(vaoRenderer);
        }

        private void RenderZeroReadbackMaterialTiers(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            XRMeshRenderer? vaoRenderer,
            int currentRenderPass,
            IReadOnlyDictionary<uint, XRMaterial> materialMap)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ZeroReadback.RenderMaterialTiers");
            GPURenderPassCollection.Crumb($"MaterialTiers.BEGIN pass={currentRenderPass}");

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.MeshesWarning("No active renderer for zero-readback indirect path.");
                return;
            }

            XRDataBuffer? indirectDrawBuffer = renderPasses.MaterialTierIndirectDrawBuffer;
            XRDataBuffer? parameterBuffer = renderPasses.MaterialTierDrawCountBuffer;
            XRDataBuffer? culledCommandsBuffer = renderPasses.CulledSceneToRenderBuffer;
            if (indirectDrawBuffer is null || parameterBuffer is null || culledCommandsBuffer is null)
            {
                Debug.MeshesWarning("Zero-readback indirect path missing material-tier buffers.");
                return;
            }

            IReadOnlyList<uint> materialSlotIds = renderPasses.MaterialSlotIds;
            if (materialSlotIds.Count == 0)
                return;

            if (!EnsureZeroReadbackMaterialSlotProgramsReady(
                renderPasses,
                currentRenderPass,
                materialMap,
                materialSlotIds,
                vaoRenderer,
                "ZeroReadbackMaterialTier"))
            {
                return;
            }

            XRDataBuffer? instanceTransformBuffer = scene.TransformBuffer;
            XRDataBuffer? instanceSourceIndexBuffer = renderPasses.InstanceSourceIndexBuffer;
            bool useGpuInstanceTransforms = instanceTransformBuffer is not null;

            Matrix4x4 defaultModelMatrix = Matrix4x4.Identity;
            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint maxDrawsPerBucket = Math.Max(renderPasses.MaxDrawsPerMaterialTier, 1u);

            // O-7: one coalesced barrier ahead of the per-material-tier bucket loop instead of
            // one barrier per DispatchRenderIndirectCountBucket.
            renderer.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.ClientMappedBuffer |
                EMemoryBarrierMask.Command);

            for (int slotIndex = 0; slotIndex < materialSlotIds.Count; ++slotIndex)
            {
                P3Diagnostics.IncSlotIterated();
                uint materialId = materialSlotIds[slotIndex];
                XRMaterial? overrideMaterial = RuntimeEngine.Rendering.State.OverrideMaterial;

                XRMaterial? sourceMaterial = null;
                if (materialId != 0)
                    materialMap.TryGetValue(materialId, out sourceMaterial);

                if (sourceMaterial is not null && sourceMaterial.RenderPass != currentRenderPass)
                {
                    P3Diagnostics.IncSlotSkippedPassMismatch();
                    continue;
                }

                XRMaterial? material = ResolveEffectiveGpuMaterial(sourceMaterial, overrideMaterial);
                if (material is null)
                {
                    XRMaterial? invalidMaterial = XRMaterial.InvalidMaterial;
                    if (invalidMaterial is null)
                    {
                        P3Diagnostics.IncSlotSkippedNoMaterial();
                        continue;
                    }

                    material = invalidMaterial;
                }

                if (TryDetectTextVertexShader(material.Shaders, out _))
                {
                    GpuWarn(LogCategory.Draw, "Skipping zero-readback material slot {0} (MaterialID={1}) because text-material support still requires CPU-prepared glyph buffers.", slotIndex, materialId);
                    P3Diagnostics.IncSlotSkippedTextShader();
                    continue;
                }

                uint effectiveMaterialId = (uint)material.GetHashCode();
                var program = EnsureCombinedProgram(effectiveMaterialId, material, vaoRenderer);
                if (program is null)
                {
                    P3Diagnostics.IncSlotSkippedProgram();
                    continue;
                }
                if (!TryUseIndirectGraphicsProgram(program, "ZeroReadbackMaterialTier"))
                {
                    P3Diagnostics.IncSlotSkippedProgram();
                    continue;
                }

                renderer.SetMaterialUniforms(material, program);
                renderer.ApplyRenderParameters(material.RenderOptions);

                for (uint tier = 0; tier < GPUBatchingBindings.MaterialTierCount; ++tier)
                {
                    P3Diagnostics.IncTierIterated();
                    if (!ConfigureIndirectRendererForTier(scene, vaoRenderer, (EAtlasTier)tier))
                    {
                        P3Diagnostics.IncTierSkippedConfigure();
                        continue;
                    }

                    uint bucketIndex = ((uint)slotIndex * GPUBatchingBindings.MaterialTierCount) + tier;
                    nuint indirectByteOffset = (nuint)(bucketIndex * maxDrawsPerBucket * stride);
                    nuint countByteOffset = (nuint)(bucketIndex * sizeof(uint));
                    GPURenderPassCollection.Crumb(
                        $"MaterialTiers.DISPATCH pass={currentRenderPass} slot={slotIndex} materialId={materialId} materialName={material.Name ?? "<unnamed>"} tier={tier} bucket={bucketIndex} indirectOff={indirectByteOffset} countOff={countByteOffset}");

                    DispatchRenderIndirectCountBucket(
                        indirectDrawBuffer,
                        vaoRenderer,
                        culledCommandsBuffer,
                        scene.DrawMetadataBuffer,
                        scene.LodTransitionBuffer,
                        instanceTransformBuffer,
                        instanceSourceIndexBuffer,
                        useGpuInstanceTransforms,
                        maxDrawsPerBucket,
                        indirectByteOffset,
                        parameterBuffer,
                        countByteOffset,
                        program,
                        camera,
                        defaultModelMatrix,
                        allowMaxDrawFallback: true,
                        emitBarrier: false);
                }
            }

            GPURenderPassCollection.Crumb($"MaterialTiers.END pass={currentRenderPass}");
            P3Diagnostics.MaybeFlush();
        }

        private void RenderZeroReadbackActiveMaterialBuckets(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            XRMeshRenderer? vaoRenderer,
            int currentRenderPass,
            IReadOnlyDictionary<uint, XRMaterial> materialMap)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ZeroReadback.RenderActiveMaterialBuckets");

            if (!renderPasses.ZeroReadbackActiveBucketListPreparedThisFrame)
            {
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.ActiveBucketsMissing.{currentRenderPass}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] Active material bucket list was not prepared for pass {0}; falling back to full bucket scan.",
                    currentRenderPass);
                RenderZeroReadbackMaterialTiers(renderPasses, camera, scene, vaoRenderer, currentRenderPass, materialMap);
                return;
            }

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                Debug.MeshesWarning("No active renderer for active-bucket indirect path.");
                return;
            }

            XRDataBuffer? indirectDrawBuffer = renderPasses.MaterialTierIndirectDrawBuffer;
            XRDataBuffer? parameterBuffer = renderPasses.MaterialTierDrawCountBuffer;
            XRDataBuffer? culledCommandsBuffer = renderPasses.CulledSceneToRenderBuffer;
            if (indirectDrawBuffer is null || parameterBuffer is null || culledCommandsBuffer is null)
            {
                Debug.MeshesWarning("Active-bucket indirect path missing material-tier buffers.");
                return;
            }

            List<uint>? activeBuckets = renderPasses.ReadActiveMaterialTierBuckets();
            if (activeBuckets is null || activeBuckets.Count == 0)
                return;

            IReadOnlyList<uint> materialSlotIds = renderPasses.MaterialSlotIds;
            if (!EnsureZeroReadbackActiveBucketProgramsReady(
                renderPasses,
                currentRenderPass,
                materialMap,
                materialSlotIds,
                activeBuckets,
                vaoRenderer,
                "ZeroReadbackActiveBucket"))
            {
                return;
            }

            XRDataBuffer? instanceTransformBuffer = scene.TransformBuffer;
            XRDataBuffer? instanceSourceIndexBuffer = renderPasses.InstanceSourceIndexBuffer;
            bool useGpuInstanceTransforms = instanceTransformBuffer is not null;

            Matrix4x4 defaultModelMatrix = Matrix4x4.Identity;
            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint maxDrawsPerBucket = Math.Max(renderPasses.MaxDrawsPerMaterialTier, 1u);

            // O-7: one coalesced barrier ahead of the active-bucket loop.
            renderer.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.ClientMappedBuffer |
                EMemoryBarrierMask.Command);

            foreach (uint bucketIndex in activeBuckets)
            {
                uint slotIndex = bucketIndex / GPUBatchingBindings.MaterialTierCount;
                uint tier = bucketIndex % GPUBatchingBindings.MaterialTierCount;
                if (slotIndex >= (uint)materialSlotIds.Count)
                    continue;

                uint materialId = materialSlotIds[(int)slotIndex];
                XRMaterial? overrideMaterial = RuntimeEngine.Rendering.State.OverrideMaterial;

                XRMaterial? sourceMaterial = null;
                if (materialId != 0)
                    materialMap.TryGetValue(materialId, out sourceMaterial);

                if (sourceMaterial is not null && sourceMaterial.RenderPass != currentRenderPass)
                    continue;

                XRMaterial? material = ResolveEffectiveGpuMaterial(sourceMaterial, overrideMaterial) ?? XRMaterial.InvalidMaterial;
                if (material is null)
                    continue;

                if (TryDetectTextVertexShader(material.Shaders, out _))
                {
                    GpuWarn(LogCategory.Draw, "Skipping active-bucket material slot {0} (MaterialID={1}) because text-material support still requires CPU-prepared glyph buffers.", slotIndex, materialId);
                    continue;
                }

                uint effectiveMaterialId = (uint)material.GetHashCode();
                var program = EnsureCombinedProgram(effectiveMaterialId, material, vaoRenderer);
                if (program is null)
                    continue;
                if (!TryUseIndirectGraphicsProgram(program, "ZeroReadbackActiveBucket"))
                    continue;

                renderer.SetMaterialUniforms(material, program);
                renderer.ApplyRenderParameters(material.RenderOptions);

                if (!ConfigureIndirectRendererForTier(scene, vaoRenderer, (EAtlasTier)tier))
                    continue;

                nuint indirectByteOffset = (nuint)(bucketIndex * maxDrawsPerBucket * stride);
                nuint countByteOffset = (nuint)(bucketIndex * sizeof(uint));

                DispatchRenderIndirectCountBucket(
                    indirectDrawBuffer,
                    vaoRenderer,
                    culledCommandsBuffer,
                    scene.DrawMetadataBuffer,
                    scene.LodTransitionBuffer,
                    instanceTransformBuffer,
                    instanceSourceIndexBuffer,
                    useGpuInstanceTransforms,
                    maxDrawsPerBucket,
                    indirectByteOffset,
                    parameterBuffer,
                    countByteOffset,
                    program,
                    camera,
                    defaultModelMatrix,
                    allowMaxDrawFallback: true,
                    emitBarrier: false);
            }
        }

        private void RenderZeroReadbackMaterialTableBuckets(
            GPURenderPassCollection renderPasses,
            XRCamera camera,
            GPUScene scene,
            XRMeshRenderer? vaoRenderer,
            int currentRenderPass,
            bool bindless)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ZeroReadback.RenderMaterialTableBuckets");

            IReadOnlyDictionary<uint, XRMaterial> materialMap = renderPasses.GetMaterialMap(scene);

            // The packed material-table fragment program selects materials by DrawID and bypasses
            // the per-XRMaterial program path. That means it cannot honor a pushed override material
            // or per-material DepthNormalPrePassVariant. Forward depth/normal pre-passes and similar
            // override-driven passes need the per-material tier path to ensure the override or
            // depth-normal variant program is actually used.
            var renderState = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState;
            bool overrideActive = RuntimeEngine.Rendering.State.OverrideMaterial is not null
                || (renderState is not null && renderState.UseDepthNormalMaterialVariants);
            if (overrideActive)
            {
                RenderZeroReadbackMaterialTiers(renderPasses, camera, scene, vaoRenderer, currentRenderPass, materialMap);
                return;
            }

            if (!renderPasses.TryGetGeneratedMaterialTableDispatchLayout(currentRenderPass, out MaterialBindingLayout layout))
            {
                MaterialBindingResolverResult result = MaterialBindingResolverResult.PerMaterial(
                    $"Pass {currentRenderPass} does not expose a generated material-table layout.");
                renderPasses.RecordMaterialBindingResolverResult(result);
                GpuDebug(LogCategory.Validation, "Material binding resolver fallback for pass {0}: {1}", currentRenderPass, result.Reason);
                RenderZeroReadbackMaterialTiers(renderPasses, camera, scene, vaoRenderer, currentRenderPass, materialMap);
                return;
            }

            renderPasses.RecordMaterialBindingResolverResult(MaterialBindingResolverResult.Compatible(layout));
            GpuDebug(
                LogCategory.Validation,
                "Material binding resolver outcome={0} pass={1} layout={2} hash={3} rowBytes={4} bindlessRequested={5}",
                EMaterialBindingResolverOutcome.MaterialTableCompatible,
                currentRenderPass,
                layout.Name,
                layout.LayoutHash,
                layout.RowByteCount,
                bindless);

            if (!renderPasses.ZeroReadbackActiveBucketListPreparedThisFrame)
            {
                renderPasses.RecordMaterialBindingResolverResult(MaterialBindingResolverResult.PerMaterial(
                    "Material-table draw path needs active bucket compaction for this pass.",
                    layout));
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.MaterialTableActiveBucketsMissing.{currentRenderPass}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] Material-table draw path needs active bucket compaction for pass {0}; falling back to per-material bucket draw.",
                    currentRenderPass);
                RenderZeroReadbackMaterialTiers(renderPasses, camera, scene, vaoRenderer, currentRenderPass, materialMap);
                return;
            }

            XRDataBuffer? materialTableBuffer = renderPasses.MaterialTableBuffer;
            if (materialTableBuffer is null)
            {
                renderPasses.RecordMaterialBindingResolverResult(MaterialBindingResolverResult.PerMaterial(
                    "Material-table draw path selected but no material table was prepared.",
                    layout));
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.MaterialTableMissing.{currentRenderPass}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] Material-table draw path was selected for pass {0}, but no material table was prepared; falling back to per-material bucket draw.",
                    currentRenderPass);
                RenderZeroReadbackMaterialTiers(renderPasses, camera, scene, vaoRenderer, currentRenderPass, materialMap);
                return;
            }

            bool useBindless = bindless && SupportsOpenGLBindlessMaterialTable();
            XRDataBuffer? materialTextureHandleBuffer = useBindless ? renderPasses.MaterialTextureHandleBuffer : null;
            if (useBindless && materialTextureHandleBuffer is null)
                useBindless = false;

            if (bindless && !useBindless && _bindlessMaterialTableUnsupportedLogBudget > 0)
            {
                _bindlessMaterialTableUnsupportedLogBudget--;
                Debug.MeshesWarning("[RenderDispatch] Bindless material-table draw path requested, but the active OpenGL renderer does not expose GL_ARB_bindless_texture + GL_ARB_gpu_shader_int64. Falling back to material-table shader.");
            }

            XRRenderProgram? program = EnsureMaterialTableDrawProgram(vaoRenderer, useBindless, layout);
            if (program is null)
            {
                RenderZeroReadbackMaterialTiers(renderPasses, camera, scene, vaoRenderer, currentRenderPass, materialMap);
                return;
            }

            if (!IsProgramReadyForCurrentRenderer(program))
            {
                renderPasses.RecordZeroReadbackProgramPending();
                WarnZeroReadbackProgramWarmup(
                    useBindless ? "ZeroReadbackBindlessMaterialTable" : "ZeroReadbackMaterialTable",
                    currentRenderPass,
                    pendingCount: 1);
                return;
            }

            XRDataBuffer? indirectDrawBuffer = renderPasses.MaterialTierIndirectDrawBuffer;
            XRDataBuffer? parameterBuffer = renderPasses.MaterialTierDrawCountBuffer;
            XRDataBuffer? culledCommandsBuffer = renderPasses.CulledSceneToRenderBuffer;
            if (indirectDrawBuffer is null || parameterBuffer is null || culledCommandsBuffer is null)
            {
                Debug.MeshesWarning("Material-table indirect path missing material-tier buffers.");
                return;
            }

            List<uint>? activeBuckets = renderPasses.ReadActiveMaterialTierBuckets();
            if (activeBuckets is null || activeBuckets.Count == 0)
                return;

            XRDataBuffer? instanceTransformBuffer = scene.TransformBuffer;
            XRDataBuffer? instanceSourceIndexBuffer = renderPasses.InstanceSourceIndexBuffer;
            bool useGpuInstanceTransforms = instanceTransformBuffer is not null;

            Matrix4x4 defaultModelMatrix = Matrix4x4.Identity;
            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            uint maxDrawsPerBucket = Math.Max(renderPasses.MaxDrawsPerMaterialTier, 1u);

            var renderer = AbstractRenderer.Current;
            XRMaterial? invalidMaterial = XRMaterial.InvalidMaterial;
            if (renderer is not null && invalidMaterial is not null)
                renderer.ApplyRenderParameters(invalidMaterial.RenderOptions);

            if (!TryUseIndirectGraphicsProgram(program, bindless ? "ZeroReadbackBindlessMaterialTable" : "ZeroReadbackMaterialTable"))
                return;

            // O-7: one coalesced barrier ahead of the material-table bucket loop.
            renderer?.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.ClientMappedBuffer |
                EMemoryBarrierMask.Command);

            foreach (uint bucketIndex in activeBuckets)
            {
                uint tier = bucketIndex % GPUBatchingBindings.MaterialTierCount;
                if (!ConfigureIndirectRendererForTier(scene, vaoRenderer, (EAtlasTier)tier))
                    continue;

                nuint indirectByteOffset = (nuint)(bucketIndex * maxDrawsPerBucket * stride);
                nuint countByteOffset = (nuint)(bucketIndex * sizeof(uint));

                DispatchRenderIndirectCountBucket(
                    indirectDrawBuffer,
                    vaoRenderer,
                    culledCommandsBuffer,
                    scene.DrawMetadataBuffer,
                    scene.LodTransitionBuffer,
                    instanceTransformBuffer,
                    instanceSourceIndexBuffer,
                    useGpuInstanceTransforms,
                    maxDrawsPerBucket,
                    indirectByteOffset,
                    parameterBuffer,
                    countByteOffset,
                    program,
                    camera,
                    defaultModelMatrix,
                    materialTableBuffer,
                    materialTextureHandleBuffer,
                    allowMaxDrawFallback: true,
                    emitBarrier: false);
            }
        }

        private static bool ConfigureIndirectRendererForTier(GPUScene scene, XRMeshRenderer? vaoRenderer, EAtlasTier tier)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ConfigureIndirectRendererForTier");

            if (vaoRenderer is null)
                return false;

            XRDataBuffer? positions = scene.GetAtlasPositions(tier);
            XRDataBuffer? normals = scene.GetAtlasNormals(tier);
            XRDataBuffer? tangents = scene.GetAtlasTangents(tier);
            XRDataBuffer? uv0 = scene.GetAtlasUV0(tier);
            XRDataBuffer? indices = scene.GetAtlasIndices(tier);
            if (positions is null || indices is null)
                return false;

            static void SetOrRemoveBuffer(XRMeshRenderer renderer, string key, XRDataBuffer? buffer)
            {
                if (renderer.Buffers.ContainsKey(key))
                    renderer.Buffers.Remove(key);

                if (buffer is not null)
                    renderer.Buffers.Add(key, buffer);
            }

            SetOrRemoveBuffer(vaoRenderer, ECommonBufferType.Position.ToString(), positions);
            SetOrRemoveBuffer(vaoRenderer, ECommonBufferType.Normal.ToString(), normals);
            SetOrRemoveBuffer(vaoRenderer, ECommonBufferType.Tangent.ToString(), tangents);
            SetOrRemoveBuffer(vaoRenderer, $"{ECommonBufferType.TexCoord}{0}", uv0);

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
                return false;

            return renderer.TrySyncMeshRendererIndexBuffer(vaoRenderer, indices, scene.GetAtlasIndexElementSize(tier));
        }

        private static void DispatchRenderIndirectCountBucket(
            XRDataBuffer indirectDrawBuffer,
            XRMeshRenderer? vaoRenderer,
            XRDataBuffer culledCommandsBuffer,
            XRDataBuffer drawMetadataBuffer,
            XRDataBuffer? lodTransitionBuffer,
            XRDataBuffer? instanceTransformBuffer,
            XRDataBuffer? instanceSourceIndexBuffer,
            bool useInstanceTransformBuffer,
            uint maxDrawCount,
            nuint indirectByteOffset,
            XRDataBuffer parameterBuffer,
            nuint countByteOffset,
            XRRenderProgram graphicsProgram,
            XRCamera camera,
            Matrix4x4 modelMatrix,
            XRDataBuffer? materialTableBuffer = null,
            XRDataBuffer? materialTextureHandleBuffer = null,
            bool allowMaxDrawFallback = false,
            bool emitBarrier = true)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchRenderIndirectCountBucket");

            var renderer = AbstractRenderer.Current;
            if (renderer is null || maxDrawCount == 0)
                return;

            // P3: with XRE_BUCKET_LOOP_DRY_RUN=1, skip the entire bucket dispatch (including the
            // upstream BindTo / SetEngineUniforms / VAO bind that would otherwise still cost CPU
            // time for an empty bucket). If fps recovers near CpuDirect with this flag set, the
            // bucket fan-out drives the static-scene perf gap (validates P3-A).
            if (P3Diagnostics.BucketLoopDryRun)
            {
                P3Diagnostics.IncBucketDryRunSkipped();
                return;
            }
            P3Diagnostics.IncBucketDispatched();

            if (!TryUseIndirectGraphicsProgram(graphicsProgram, "RenderIndirectCountBucket"))
                return;

            culledCommandsBuffer.BindTo(graphicsProgram, IndirectCommandSsboBinding);
            drawMetadataBuffer.BindTo(graphicsProgram, DrawMetadataSsboBinding);
            lodTransitionBuffer?.BindTo(graphicsProgram, LodTransitionSsboBinding);
            instanceTransformBuffer?.BindTo(graphicsProgram, InstanceTransformSsboBinding);
            instanceSourceIndexBuffer?.BindTo(graphicsProgram, InstanceSourceIndexSsboBinding);
            materialTableBuffer?.BindTo(graphicsProgram, MaterialTableSsboBinding);
            materialTextureHandleBuffer?.BindTo(graphicsProgram, MaterialTextureHandleTableSsboBinding);
            graphicsProgram.Uniform("UseInstanceTransformBuffer", useInstanceTransformBuffer ? 1 : 0);
            renderer.SetEngineUniforms(graphicsProgram, camera);
            graphicsProgram.Uniform(EEngineUniform.ModelMatrix.ToStringFast(), modelMatrix);

            var version = vaoRenderer?.GetDefaultVersion();
            renderer.BindVAOForRenderer(version);
            if (vaoRenderer is not null)
                renderer.ConfigureVAOAttributesForProgram(graphicsProgram, version);

            uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
            IndirectParityChecklist parity = BuildIndirectParityChecklist(renderer, indirectDrawBuffer, parameterBuffer, version);

            // C-GPU-2: production zero-readback must consume the GPU count buffer directly.
            // The bucket dispatcher is invoked exclusively from the zero-readback material-scatter
            // path, so the production invariant is non-negotiable here.
            AssertZeroReadbackProductionInvariants("DispatchRenderIndirectCountBucket");
            AssertZeroReadbackUsesGpuCountPath(parity, "DispatchRenderIndirectCountBucket");

            // Use the GPU count buffer to cap draws whenever the backend supports it. OpenGL 4.6 / ARB_indirect_parameters
            // is a fully supported path; falling back to MaxDrawCount-shaped indirect draws here would force every
            // bucket to dispatch MaxDrawsPerMaterialTier no-op commands, producing a large per-frame CPU/GPU overhead
            // that scales with material count and dwarfs the savings from zero-readback dispatch.
            bool useMaxDrawFallback = allowMaxDrawFallback && !parity.UsesCountDrawPath;
            if (useMaxDrawFallback)
            {
                XREngine.Debug.RenderingWarningEvery(
                    "RenderDispatch.ZeroReadback.MaxDrawFallback",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] Zero-readback material bucket draw is using max-draw fallback (maxDrawsPerBucket={0}). This can be slower than CPU direct; enable indirect-count draws or avoid DebugSettings.DisableCountDrawPath.",
                    maxDrawCount);

                if (!parity.IsSubmissionReady)
                {
                    renderer.BindVAOForRenderer(null);
                    return;
                }

                if (!ValidateIndirectDrawRange(
                    indirectDrawBuffer,
                    maxDrawCount,
                    stride,
                    indirectByteOffset,
                    "RenderIndirectCountBucketFallback"))
                {
                    renderer.BindVAOForRenderer(null);
                    return;
                }

                using (RuntimeEngine.Profiler.Start("GpuIndirect.BindDrawIndirectBuffer"))
                    renderer.BindDrawIndirectBuffer(indirectDrawBuffer);
                try
                {
                    if (emitBarrier)
                    {
                        using var barrierScope = RuntimeEngine.Profiler.Start("GpuIndirect.Draw.MemoryBarrier");
                        renderer.MemoryBarrier(
                            EMemoryBarrierMask.ShaderStorage |
                            EMemoryBarrierMask.ClientMappedBuffer |
                            EMemoryBarrierMask.Command);
                    }

                    using (RuntimeEngine.Profiler.Start("GpuIndirect.MultiDrawElementsIndirectWithOffset"))
                        renderer.MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, indirectByteOffset);
                }
                finally
                {
                    renderer.UnbindDrawIndirectBuffer();
                    renderer.BindVAOForRenderer(null);
                }

                return;
            }

            if (!parity.UsesCountDrawPath)
            {
                renderer.BindVAOForRenderer(null);
                return;
            }

            if (!ValidateIndirectCountDrawRange(
                indirectDrawBuffer,
                parameterBuffer,
                maxDrawCount,
                stride,
                indirectByteOffset,
                countByteOffset,
                "RenderIndirectCountBucket"))
            {
                renderer.BindVAOForRenderer(null);
                return;
            }

            using (RuntimeEngine.Profiler.Start("GpuIndirect.BindDrawIndirectBuffer"))
                renderer.BindDrawIndirectBuffer(indirectDrawBuffer);
            using (RuntimeEngine.Profiler.Start("GpuIndirect.BindParameterBuffer"))
                renderer.BindParameterBuffer(parameterBuffer);
            try
            {
                if (emitBarrier)
                {
                    using var barrierScope = RuntimeEngine.Profiler.Start("GpuIndirect.Draw.MemoryBarrier");
                    renderer.MemoryBarrier(
                        EMemoryBarrierMask.ShaderStorage |
                        EMemoryBarrierMask.ClientMappedBuffer |
                        EMemoryBarrierMask.Command);
                }

                using (RuntimeEngine.Profiler.Start("GpuIndirect.MultiDrawElementsIndirectCount"))
                {
                    uint bucketIndex = countByteOffset <= uint.MaxValue - sizeof(uint)
                        ? (uint)(countByteOffset / sizeof(uint))
                        : uint.MaxValue;
                    LogIndirectDrawSizes("DispatchRenderIndirectCountBucket", maxDrawCount, stride, indirectDrawBuffer, parameterBuffer, indirectByteOffset, countByteOffset);
                    GPURenderPassCollection.Crumb($"MDIC.BUCKET.BEGIN bucket={bucketIndex} maxCmd={maxDrawCount} stride={stride} indirectOff={indirectByteOffset} countOff={countByteOffset} indCap={indirectDrawBuffer.ElementCount} paramCap={parameterBuffer.ElementCount}");
                    renderer.MultiDrawElementsIndirectCount(maxDrawCount, stride, indirectByteOffset, countByteOffset);
                    if (P3Diagnostics.FinishAfterMultiDrawIndirectCount)
                    {
                        GPURenderPassCollection.Crumb("MDIC.BUCKET.FINISH.BEGIN");
                        renderer.WaitForGpu();
                        GPURenderPassCollection.Crumb("MDIC.BUCKET.FINISH.END");
                    }
                    GPURenderPassCollection.Crumb("MDIC.BUCKET.END");
                }
            }
            finally
            {
                GPURenderPassCollection.Crumb("MDIC.BUCKET.UNBIND.BEGIN");
                renderer.UnbindParameterBuffer();
                renderer.UnbindDrawIndirectBuffer();
                renderer.BindVAOForRenderer(null);
                GPURenderPassCollection.Crumb("MDIC.BUCKET.UNBIND.END");
            }
        }

        private static bool ValidateIndirectDrawRange(
            XRDataBuffer indirectDrawBuffer,
            uint maxDrawCount,
            uint stride,
            nuint indirectByteOffset,
            string context)
        {
            if (maxDrawCount == 0 || stride == 0)
                return false;

            ulong indirectOffset = indirectByteOffset;
            ulong indirectBytes = indirectDrawBuffer.Length;
            ulong requestedIndirectBytes = indirectOffset + ((ulong)maxDrawCount * stride);

            if (indirectOffset % stride != 0 || requestedIndirectBytes > indirectBytes)
            {
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.IndirectDrawRange.{context}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] {0} skipped invalid indirect draw range: indirectOffset={1} maxDrawCount={2} stride={3} indirectBytes={4}.",
                    context,
                    indirectOffset,
                    maxDrawCount,
                    stride,
                    indirectBytes);
                return false;
            }

            return true;
        }

        private static bool ValidateIndirectCountDrawRange(
            XRDataBuffer indirectDrawBuffer,
            XRDataBuffer parameterBuffer,
            uint maxDrawCount,
            uint stride,
            nuint indirectByteOffset,
            nuint countByteOffset,
            string context)
        {
            if (maxDrawCount == 0 || stride == 0)
                return false;

            ulong indirectOffset = indirectByteOffset;
            ulong countOffset = countByteOffset;
            ulong indirectBytes = indirectDrawBuffer.Length;
            ulong parameterBytes = parameterBuffer.Length;
            ulong requestedIndirectBytes = indirectOffset + ((ulong)maxDrawCount * stride);
            ulong requestedCountBytes = countOffset + sizeof(uint);

            if (indirectOffset % stride != 0 ||
                requestedIndirectBytes > indirectBytes ||
                requestedCountBytes > parameterBytes)
            {
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.IndirectCountRange.{context}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] {0} skipped invalid indirect-count draw range: indirectOffset={1} maxDrawCount={2} stride={3} indirectBytes={4} countOffset={5} parameterBytes={6}.",
                    context,
                    indirectOffset,
                    maxDrawCount,
                    stride,
                    indirectBytes,
                    countOffset,
                    parameterBytes);
                return false;
            }

            return true;
        }

        private static bool TryUseIndirectGraphicsProgram(XRRenderProgram graphicsProgram, string context)
        {
            if (!graphicsProgram.IsLinked)
                graphicsProgram.Link();

            if (graphicsProgram.IsLinked)
            {
                graphicsProgram.Use();
                return true;
            }

            RuntimeEngine.Rendering.Stats.GpuFallback.RecordForbiddenGpuFallback(1);
            var backend = graphicsProgram.ShaderMetadata.Backend;
            XREngine.Debug.RenderingWarningEvery(
                $"RenderDispatch.ProgramNotReady.{context}.{RuntimeHelpers.GetHashCode(graphicsProgram)}",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] {0} skipped because graphics program '{1}' is not linked yet. BackendStage={2} Backend={3} Detail='{4}' Failure='{5}'. Avoiding stale-program indirect draw.",
                context,
                graphicsProgram.Name ?? "<unnamed>",
                backend.Stage,
                backend.Backend ?? "<none>",
                backend.Detail ?? "<none>",
                backend.FailureReason ?? "<none>");
            return false;
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
                DestroyMaterialProgramCache(cache);
            _materialPrograms.Clear();
            foreach (var cache in _pendingMaterialPrograms.Values)
                DestroyMaterialProgramCache(cache);
            _pendingMaterialPrograms.Clear();
            _materialProgramUseDescriptors.Clear();
            foreach (var cache in _materialTablePrograms.Values)
                DestroyMaterialTableProgramCache(cache);
            _materialTablePrograms.Clear();
            foreach (var cache in _meshletMaterialTablePrograms.Values)
                DestroyMeshletMaterialTableProgramCache(cache);
            _meshletMaterialTablePrograms.Clear();
            _indirectTextTransformsBuffer?.Destroy();
            _indirectTextTexCoordsBuffer?.Destroy();
            _indirectTextRotationsBuffer?.Destroy();
            _indirectTextGlyphOffsetsBuffer?.Destroy();
            GC.SuppressFinalize(this);
        }
    }
}
