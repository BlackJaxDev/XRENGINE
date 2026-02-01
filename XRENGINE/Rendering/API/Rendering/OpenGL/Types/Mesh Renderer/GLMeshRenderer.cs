using Extensions;
using Silk.NET.OpenGL;
using System.Collections.Generic;
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
        public partial class GLMeshRenderer(OpenGLRenderer renderer, XRMeshRenderer.BaseVersion mesh) : GLObject<XRMeshRenderer.BaseVersion>(renderer, mesh)
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
            private int _shaderConfigVersion = Engine.Rendering.Settings.ShaderConfigVersion;

            private GLDataBuffer? _triangleIndicesBuffer;
            private GLDataBuffer? _lineIndicesBuffer;
            private GLDataBuffer? _pointIndicesBuffer;

            private IndexSize _trianglesElementType;
            private IndexSize _lineIndicesElementType;
            private IndexSize _pointIndicesElementType;

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

            public EConditionalRenderType ConditionalRenderType { get; set; } = EConditionalRenderType.QueryNoWait;
            public GLRenderQuery? ConditionalRenderQuery { get; set; }

            public uint Instances { get; set; } = 1;
            public GLMaterial? Material => Renderer.GenericToAPI<GLMaterial>(MeshRenderer.Material);

            public bool BuffersBound
            {
                get => _buffersBound;
                private set => SetField(ref _buffersBound, value);
            }

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

                // Check if upload queue has any pending buffers at all first (fast path)
                if (Renderer.UploadQueue.PendingCount == 0)
                    return true;

                // Use pre-cached list to avoid dictionary enumerator allocation
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
            Api.BindVertexArray(mesh?.BindingId ?? 0);
            ActiveMeshRenderer = mesh;
            if (mesh == null)
                return;

            // Ensure an index buffer is bound for any indirect or indexed draws.
            GLDataBuffer? elem = mesh.TriangleIndicesBuffer ?? mesh.LineIndicesBuffer ?? mesh.PointIndicesBuffer;
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
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            if (!ActiveMeshRenderer.AreBuffersReadyForRendering())
                return;

            // Skip rendering if index buffer data hasn't been uploaded yet
            var triBuffer = ActiveMeshRenderer.TriangleIndicesBuffer;
            var lineBuffer = ActiveMeshRenderer.LineIndicesBuffer;
            var pointBuffer = ActiveMeshRenderer.PointIndicesBuffer;

            uint triangles = triBuffer?.Data?.ElementCount ?? 0u;
            if (triangles > 0)
            {
                Api.DrawElementsInstanced(GLEnum.Triangles, triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, instances);
                Engine.Rendering.Stats.IncrementDrawCalls();
                Engine.Rendering.Stats.AddTrianglesRendered((int)(triangles / 3 * instances));
            }

            uint lines = lineBuffer?.Data?.ElementCount ?? 0u;
            if (lines > 0)
            {
                Api.DrawElementsInstanced(GLEnum.Lines, lines, ToGLEnum(ActiveMeshRenderer.LineIndicesElementType), null, instances);
                Engine.Rendering.Stats.IncrementDrawCalls();
            }

            uint points = pointBuffer?.Data?.ElementCount ?? 0u;
            if (points > 0)
            {
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
            Api.MultiDrawElementsIndirect(GLEnum.Triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, meshCount, 0);
        }
    }
}
