using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// OpenGL-backed mesh renderer responsible for VAO setup, shader selection, and draw dispatch.
        /// </summary>
        public partial class GLMeshRenderer(OpenGLRenderer renderer, XRMeshRenderer.BaseVersion mesh) : GLObject<XRMeshRenderer.BaseVersion>(renderer, mesh), IRenderPreparationState
        {
            public XRMeshRenderer MeshRenderer => Data.Parent;
            public XRMesh? Mesh => MeshRenderer.Mesh;

            public override EGLObjectType Type => EGLObjectType.VertexArray;

            public delegate void DelSettingUniforms(GLRenderProgram vertexProgram, GLRenderProgram materialProgram);

            // Cached buffers/programs to avoid regenerating on every draw.
            private Dictionary<string, GLDataBuffer> _bufferCache = [];
            /// <summary>
            /// Pre-filtered list of SSBO buffers to avoid LINQ filtering every frame.
            /// </summary>
            private List<GLDataBuffer> _ssboBufferCache = [];
            /// <summary>
            /// Flat list of all buffer values for fast iteration in CheckBuffersReady.
            /// </summary>
            private List<GLDataBuffer> _allBuffersList = [];
            private GLRenderProgramPipeline? _pipeline;
            private GLRenderProgram? _combinedProgram;
            private GLRenderProgram? _separatedVertexProgram;
            private GLRenderProgram? _forcedGeneratedVertexProgram;
            private int _shaderConfigVersion = Engine.Rendering.Settings.ShaderConfigVersion;
            private XRMaterial? _programMaterialStateKey;
            private long _programMaterialShaderStateRevision;
            private string _lastPrepareResult = "NeverCalled";
            private string _lastPrepareDetail = string.Empty;

            // Identity / lifecycle counters for diagnosing program churn.
            private static int s_nextInstanceId;
            private readonly int _instanceId = System.Threading.Interlocked.Increment(ref s_nextInstanceId);
            private int _programGenerationCount;
            private int _programDestructionCount;
            private int _meshChangedCount;
            private int _materialChangedCount;
            // Per-callsite counters for GenProgramsAndBuffers.
            internal int _genCallSiteEnsureSettings;
            internal int _genCallSiteTryPrepareNull;
            internal int _genCallSitePostGenerated;
            internal int _genCallSiteRegenerate;

            public int InstanceId => _instanceId;
            public int ProgramGenerationCount => _programGenerationCount;
            public int ProgramDestructionCount => _programDestructionCount;

            /// <summary>
            /// Result string of the most recent <see cref="TryPrepareForRendering()"/> call.
            /// One of: "Ready", "BuffersPending", "ProgramsPending", "GenerateFailed", "MaterialMissing", "NoData", "NeverCalled".
            /// </summary>
            public string LastPrepareResult => _lastPrepareResult;

            /// <summary>
            /// Supplemental detail describing the most recent ProgramsPending failure
            /// (variant counts, material revision, which program slots are null).
            /// Empty for non-pending or non-program failures.
            /// </summary>
            public string LastPrepareDetail => _lastPrepareDetail;

            // Cached shadow material lookup to avoid ConcurrentDictionary hit per shadow draw.
            private XRMaterial? _shadowVariantKey;
            private GLMaterial? _shadowMaterialCache;

            private GLDataBuffer? _triangleIndicesBuffer;
            private GLDataBuffer? _lineIndicesBuffer;
            private GLDataBuffer? _pointIndicesBuffer;
            private readonly List<GLDataBuffer> _boundVertexArrayBuffers = [];
            private readonly List<uint> _boundVertexArrayBufferIds = [];
            private uint _boundTriangleIndicesBufferId;
            private uint _boundLineIndicesBufferId;
            private uint _boundPointIndicesBufferId;
            private static int s_batchedTextDrawDiagCount;
            private static int s_batchedTextSsboBindDiagCount;
            private static int s_batchedTextSamplesDiagCount;
            private XRRenderQuery? _batchedTextSamplesQuery;

            private IndexSize _trianglesElementType;
            private IndexSize _lineIndicesElementType;
            private IndexSize _pointIndicesElementType;
            private bool _usesPatchTopology;
            private int _patchVertexCount = 3;

            private bool _buffersBound;

            public GLDataBuffer? TriangleIndicesBuffer
            {
                get => _triangleIndicesBuffer;
                set => _triangleIndicesBuffer = value;
            }

            public GLDataBuffer? LineIndicesBuffer
            {
                get => _lineIndicesBuffer;
                set => _lineIndicesBuffer = value;
            }

            public GLDataBuffer? PointIndicesBuffer
            {
                get => _pointIndicesBuffer;
                set => _pointIndicesBuffer = value;
            }

            public IndexSize TrianglesElementType
            {
                get => _trianglesElementType;
                private set => _trianglesElementType = value;
            }

            public IndexSize LineIndicesElementType
            {
                get => _lineIndicesElementType;
                private set => _lineIndicesElementType = value;
            }

            public IndexSize PointIndicesElementType
            {
                get => _pointIndicesElementType;
                private set => _pointIndicesElementType = value;
            }

            public bool UsesPatchTopology
            {
                get => _usesPatchTopology;
                private set => _usesPatchTopology = value;
            }

            public int PatchVertexCount
            {
                get => _patchVertexCount;
                private set => _patchVertexCount = value < 1 ? 1 : value;
            }

            public EConditionalRenderType ConditionalRenderType { get; set; } = EConditionalRenderType.QueryNoWait;
            public GLRenderQuery? ConditionalRenderQuery { get; set; }

            public uint Instances { get; set; } = 1;
            public GLMaterial? Material => Renderer.GenericToAPI<GLMaterial>(MeshRenderer.Material);

            /// <summary>
            /// Tracks whether per-frame vertex buffers are currently bound to the VAO.
            /// Marked transient so that toggling on/off each frame does NOT invalidate
            /// the GL object and trigger a destroy/recreate of the VAO and shader programs.
            /// </summary>
            [TransientGLState]
            public bool BuffersBound
            {
                get => _buffersBound;
                private set
                {
                    if (SetField(ref _buffersBound, value))
                        _buffersReadyCacheFrame = -1;
                }
            }

            private bool IsBatchedTextDiagnosticMesh()
            {
                var meshRenderer = Data?.Parent;
                return string.Equals(meshRenderer?.Material?.Name, "UIBatchTextMaterial", StringComparison.Ordinal)
                    || string.Equals(meshRenderer?.Name, "UIBatchTextRenderer", StringComparison.Ordinal)
                    || string.Equals(meshRenderer?.Mesh?.Name, "UIBatchTextQuadMesh", StringComparison.Ordinal);
            }

            internal void LogBatchedTextDraw(string phase, uint instances, string? detail = null)
            {
                if (!IsBatchedTextDiagnosticMesh() || s_batchedTextDrawDiagCount++ >= 80)
                    return;

                var meshRenderer = Data?.Parent;
                Debug.Log(
                    ELogCategory.UI,
                    "[FpsTextDiag] GLMeshRenderer.{0} #{1}: instances={2} generated={3} prepared={4} buffersBound={5} buffersReady={6} vao={7} triCount={8} triReady={9} triGenerated={10} triId={11} material='{12}' mesh='{13}' detail='{14}' buffers=[{15}]",
                    phase,
                    s_batchedTextDrawDiagCount,
                    instances,
                    IsGenerated,
                    IsPreparedForRendering,
                    BuffersBound,
                    AreBuffersReadyForRendering(),
                    TryGetBindingId(out uint vaoId) ? vaoId : 0u,
                    TriangleIndicesBuffer?.Data?.ElementCount ?? 0u,
                    TriangleIndicesBuffer?.IsReadyForRendering ?? false,
                    TriangleIndicesBuffer?.IsGenerated ?? false,
                    GetBufferBindingId(TriangleIndicesBuffer),
                    meshRenderer?.Material?.Name ?? "<null>",
                    meshRenderer?.Mesh?.Name ?? "<null>",
                    detail ?? string.Empty,
                    GetBufferReadinessSummary());
            }

            private string GetBufferReadinessSummary()
            {
                if (_allBuffersList.Count == 0)
                    return "<none>";

                StringBuilder sb = new(256);
                for (int i = 0; i < _allBuffersList.Count; i++)
                {
                    if (i > 0)
                        sb.Append("; ");

                    GLDataBuffer buffer = _allBuffersList[i];
                    sb.Append(buffer.Data.AttributeName ?? buffer.Data.Target.ToString())
                        .Append(":gen=").Append(buffer.IsGenerated)
                        .Append(",ready=").Append(buffer.IsReadyForRendering)
                        .Append(",id=").Append(GetBufferBindingId(buffer))
                        .Append(",len=").Append(buffer.Data.Length)
                        .Append(",elements=").Append(buffer.Data.ElementCount);
                }

                return sb.ToString();
            }

            private string GetShaderStorageBindingSummary()
            {
                if (_ssboBufferCache.Count == 0)
                    return "<none>";

                StringBuilder sb = new(192);
                for (int i = 0; i < _ssboBufferCache.Count; i++)
                {
                    if (i > 0)
                        sb.Append("; ");

                    GLDataBuffer buffer = _ssboBufferCache[i];
                    sb.Append(buffer.Data.AttributeName ?? buffer.Data.Target.ToString())
                        .Append(":binding=").Append(buffer.Data.BindingIndexOverride?.ToString() ?? "<auto>")
                        .Append(",id=").Append(GetBufferBindingId(buffer))
                        .Append(",ready=").Append(buffer.IsReadyForRendering);
                }

                return sb.ToString();
            }

            internal GLRenderQuery? BeginBatchedTextSamplesProbe()
            {
                if (!IsBatchedTextDiagnosticMesh() || s_batchedTextSamplesDiagCount >= 20)
                    return null;

                _batchedTextSamplesQuery ??= new XRRenderQuery();
                GLRenderQuery? query = Renderer.GenericToAPI<GLRenderQuery>(_batchedTextSamplesQuery);
                query?.BeginQuery(EQueryTarget.SamplesPassed);
                return query;
            }

            internal void EndBatchedTextSamplesProbe(GLRenderQuery? query, uint instances, uint triangles)
            {
                if (query is null)
                    return;

                query.EndQuery();
                long samples = query.GetQueryObject(EGetQueryObject.QueryResult);
                s_batchedTextSamplesDiagCount++;
                Debug.Log(
                    ELogCategory.UI,
                    "[FpsTextDiag] GLMeshRenderer.TextSamplesPassed #{0}: instances={1} triangles={2} samples={3} state={4} ssbos=[{5}]",
                    s_batchedTextSamplesDiagCount,
                    instances,
                    triangles,
                    samples,
                    GetBatchedTextRasterStateSummary(),
                    GetShaderStorageBindingSummary());
            }

            private string GetBatchedTextRasterStateSummary()
            {
                var camera = Engine.Rendering.State.RenderingCamera;
                var viewProjection = camera?.ViewProjectionMatrix ?? Matrix4x4.Identity;
                string cameraName = camera?.Transform.SceneNode?.Name ?? camera?.GetType().Name ?? "<null>";
                return $"depth={Api.IsEnabled(EnableCap.DepthTest)}, stencil={Api.IsEnabled(EnableCap.StencilTest)}, scissor={Api.IsEnabled(EnableCap.ScissorTest)}, cull={Api.IsEnabled(EnableCap.CullFace)}, discard={Api.IsEnabled(EnableCap.RasterizerDiscard)}, blend={Api.IsEnabled(EnableCap.Blend)}, area={Engine.Rendering.State.RenderArea}, camera='{cameraName}', vp=({viewProjection.M11:F4},{viewProjection.M22:F4},{viewProjection.M41:F4},{viewProjection.M42:F4})";
            }

            private static uint GetBufferBindingId(GLDataBuffer? buffer)
                => buffer is not null && buffer.TryGetBindingId(out uint id) ? id : 0u;

            public GLRenderProgram GetVertexProgram()
            {
                if (_combinedProgram is not null)
                    return _combinedProgram;

                if (Material?.SeparableProgram?.Data.GetShaderTypeMask().HasFlag(EProgramStageMask.VertexShaderBit) ?? false)
                    return Material.SeparableProgram!;

                return _separatedVertexProgram!;
            }

            /// <summary>
            /// Checks if all buffers needed for rendering have been uploaded to the GPU.
            /// Cached per-frame to avoid repeated dictionary lookups.
            /// </summary>
            private bool _buffersReadyCache = false;
            private long _buffersReadyCacheFrame = -1;

            public bool AreBuffersReadyForRendering()
            {
                // Cache the result per frame to avoid repeated dictionary lookups
                long currentFrame = Renderer._frameCounter;
                if (_buffersReadyCacheFrame == currentFrame)
                    return _buffersReadyCache;

                _buffersReadyCacheFrame = currentFrame;
                _buffersReadyCache = CheckBuffersReady();
                return _buffersReadyCache;
            }

            private bool CheckBuffersReady()
            {
                if (!BuffersBound)
                    return false;

                // Validate ALL buffers have live GL objects with uploaded GPU data.
                // A buffer can report _lastPushedLength > 0 but be destroyed (stale),
                // or never uploaded without entering the upload queue, so we must
                // check every buffer unconditionally.
                var buffers = _allBuffersList;
                for (int i = 0; i < buffers.Count; i++)
                {
                    if (!buffers[i].IsReadyForRendering)
                        return false;
                }

                if (_triangleIndicesBuffer is not null && !_triangleIndicesBuffer.IsReadyForRendering)
                    return false;
                if (_lineIndicesBuffer is not null && !_lineIndicesBuffer.IsReadyForRendering)
                    return false;
                if (_pointIndicesBuffer is not null && !_pointIndicesBuffer.IsReadyForRendering)
                    return false;

                return true;
            }

            private void PrepareDynamicRenderData()
            {
                if (!MeshRenderer.HasRenderDataPreparation)
                    return;

                MeshRenderer.OnPreparingRenderData();
                _buffersReadyCacheFrame = -1;
            }

            private void ConfigureDrawTopology(GLRenderProgram vertexProgram, GLRenderProgram? materialProgram)
            {
                EProgramStageMask mask = vertexProgram.Data.GetShaderTypeMask();
                if (materialProgram is not null && !ReferenceEquals(materialProgram, vertexProgram))
                    mask |= materialProgram.Data.GetShaderTypeMask();

                bool hasTessellationStages =
                    mask.HasFlag(EProgramStageMask.TessControlShaderBit) ||
                    mask.HasFlag(EProgramStageMask.TessEvaluationShaderBit);

                UsesPatchTopology = hasTessellationStages && TriangleIndicesBuffer is not null;
                PatchVertexCount = 3;
            }

            internal GLDataBuffer? GetActiveElementBuffer()
            {
                if (UsesPatchTopology)
                    return TriangleIndicesBuffer;

                return TriangleIndicesBuffer ?? LineIndicesBuffer ?? PointIndicesBuffer;
            }
        }

        /// <summary>
        /// Globally tracks which mesh VAO is currently bound within this renderer so draws can be restored.
        /// </summary>
        public GLMeshRenderer? ActiveMeshRenderer { get; private set; } = null;

        public void UnbindMeshRenderer()
        {
            // Profiler instrumentation removed from this hot path - called per mesh per frame
            Api.BindVertexArray(0);
            ActiveMeshRenderer = null;
        }

        public void BindMeshRenderer(GLMeshRenderer? mesh)
        {
            uint vao = mesh?.BindingId ?? 0;
            Api.BindVertexArray(vao);

            if (mesh != null && vao == 0)
            {
                // VAO generation failed — do not record this renderer as active.
                ActiveMeshRenderer = null;
                return;
            }

            ActiveMeshRenderer = mesh;
            if (mesh == null)
                return;

            // Ensure an index buffer is bound for any indirect or indexed draws.
            GLDataBuffer? elem = mesh.GetActiveElementBuffer();
            if (elem != null)
            {
                // Only generate if not already generated
                if (!elem.IsGenerated)
                    elem.Generate();
                Api.VertexArrayElementBuffer(mesh.BindingId, elem.BindingId);
            }
        }

        public void RenderMesh(GLMeshRenderer manager, bool preservePreviouslyBound = true, uint instances = 1)
        {
            // Profiler instrumentation removed from this hot path - called per mesh per frame
            if (SuppressDrawsForOomRecovery)
            {
                manager.LogBatchedTextDraw("RenderMesh suppressed-oom", instances);
                return;
            }

            GLMeshRenderer? prev = ActiveMeshRenderer;
            BindMeshRenderer(manager);
            RenderCurrentMesh(instances);
            BindMeshRenderer(preservePreviouslyBound ? prev : null);
        }

        /// <summary>
        /// Render the currently bound mesh.
        /// </summary>
        public void RenderCurrentMesh(uint instances = 1)
        {
            // Profiler instrumentation removed from this hot path - called per mesh per frame
            if (SuppressDrawsForOomRecovery)
            {
                ActiveMeshRenderer?.LogBatchedTextDraw("RenderCurrentMesh suppressed-oom", instances);
                return;
            }

            if (ActiveMeshRenderer?.Mesh is null)
            {
                ActiveMeshRenderer?.LogBatchedTextDraw("RenderCurrentMesh no-mesh", instances);
                return;
            }

            // VAO must still be alive — a deferred Destroy can delete it between frames.
            if (!ActiveMeshRenderer.IsGenerated)
            {
                ActiveMeshRenderer.LogBatchedTextDraw("RenderCurrentMesh vao-not-generated", instances);
                return;
            }

            if (!ActiveMeshRenderer.AreBuffersReadyForRendering())
            {
                ActiveMeshRenderer.LogBatchedTextDraw("RenderCurrentMesh buffers-not-ready", instances);
                return;
            }

            ActiveMeshRenderer.LogBatchedTextDraw("RenderCurrentMesh ready", instances);

            // Skip rendering if index buffer data hasn't been uploaded yet
            var triBuffer = ActiveMeshRenderer.TriangleIndicesBuffer;
            var lineBuffer = ActiveMeshRenderer.LineIndicesBuffer;
            var pointBuffer = ActiveMeshRenderer.PointIndicesBuffer;

            if (ActiveMeshRenderer.UsesPatchTopology)
            {
                uint patchControlPoints = triBuffer?.Data?.ElementCount ?? 0u;
                if (patchControlPoints > 0
                    && triBuffer!.IsReadyForRendering
                    && triBuffer.IsGenerated
                    && triBuffer.TryGetBindingId(out uint patchEbo) && patchEbo != 0)
                {
                    Api.VertexArrayElementBuffer(ActiveMeshRenderer.BindingId, patchEbo);
                    ApplyPatchParameters(ActiveMeshRenderer);
                    Api.DrawElementsInstanced(
                        GLEnum.Patches,
                        patchControlPoints,
                        ToGLEnum(ActiveMeshRenderer.TrianglesElementType),
                        null,
                        instances);
                    Engine.Rendering.Stats.IncrementDrawCalls();
                    Engine.Rendering.Stats.AddTrianglesRendered((int)(patchControlPoints / 3u * instances));
                }

                return;
            }

            uint triangles = triBuffer?.Data?.ElementCount ?? 0u;
            if (triangles > 0
                && triBuffer!.IsReadyForRendering
                && triBuffer.IsGenerated
                && triBuffer.TryGetBindingId(out uint triEbo) && triEbo != 0)
            {
                Api.VertexArrayElementBuffer(ActiveMeshRenderer.BindingId, triEbo);
                ActiveMeshRenderer.LogBatchedTextDraw("DrawElementsInstanced triangles", instances, $"ebo={triEbo}");
                GLRenderQuery? samplesProbe = ActiveMeshRenderer.BeginBatchedTextSamplesProbe();
                Api.DrawElementsInstanced(GLEnum.Triangles, triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, instances);
                ActiveMeshRenderer.EndBatchedTextSamplesProbe(samplesProbe, instances, triangles);
                Engine.Rendering.Stats.IncrementDrawCalls();
                Engine.Rendering.Stats.AddTrianglesRendered((int)(triangles / 3 * instances));
            }
            else
            {
                ActiveMeshRenderer.LogBatchedTextDraw("DrawElementsInstanced triangles-skipped", instances);
            }

            if (ActiveMeshRenderer.RequiresTriangleOnlyDrawsForCurrentPass())
                return;

            uint lines = lineBuffer?.Data?.ElementCount ?? 0u;
            if (lines > 0
                && lineBuffer!.IsReadyForRendering
                && lineBuffer.IsGenerated
                && lineBuffer.TryGetBindingId(out uint lineEbo) && lineEbo != 0)
            {
                Api.VertexArrayElementBuffer(ActiveMeshRenderer.BindingId, lineEbo);
                Api.DrawElementsInstanced(GLEnum.Lines, lines, ToGLEnum(ActiveMeshRenderer.LineIndicesElementType), null, instances);
                Engine.Rendering.Stats.IncrementDrawCalls();
            }

            uint points = pointBuffer?.Data?.ElementCount ?? 0u;
            if (points > 0
                && pointBuffer!.IsReadyForRendering
                && pointBuffer.IsGenerated
                && pointBuffer.TryGetBindingId(out uint pointEbo) && pointEbo != 0)
            {
                Api.VertexArrayElementBuffer(ActiveMeshRenderer.BindingId, pointEbo);
                Api.DrawElementsInstanced(GLEnum.Points, points, ToGLEnum(ActiveMeshRenderer.PointIndicesElementType), null, instances);
                Engine.Rendering.Stats.IncrementDrawCalls();
            }
        }

        /// <summary>
        /// Render the currently bound mesh using indirect draw commands.
        /// </summary>
        public void RenderCurrentMeshIndirect()
        {
            // Profiler instrumentation removed from this hot path
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            uint meshCount = 1u;
            var (primitiveType, elementType) = GetActivePrimitiveAndElementType();
            ApplyPatchParameters(ActiveMeshRenderer);
            Api.MultiDrawElementsIndirect(primitiveType, elementType, null, meshCount, 0);
        }
    }
}
