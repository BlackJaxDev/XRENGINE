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

            public bool AreBuffersReadyForRendering()
            {
                if (!BuffersBound)
                    return false;

                foreach (var buffer in _bufferCache.Values)
                {
                    if (!buffer.IsReadyForRendering)
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
            using var prof = Engine.Profiler.Start("OpenGLRenderer.UnbindMeshRenderer");
            Api.BindVertexArray(0);
            ActiveMeshRenderer = null;
        }

        public void BindMeshRenderer(GLMeshRenderer? mesh)
        {
            using var prof = Engine.Profiler.Start("OpenGLRenderer.BindMeshRenderer");
            Api.BindVertexArray(mesh?.BindingId ?? 0);
            ActiveMeshRenderer = mesh;
            if (mesh == null)
                return;

            // Ensure an index buffer is bound for any indirect or indexed draws.
            GLDataBuffer? elem = mesh.TriangleIndicesBuffer ?? mesh.LineIndicesBuffer ?? mesh.PointIndicesBuffer;
            if (elem != null)
            {
                using (Engine.Profiler.Start("OpenGLRenderer.BindMeshRenderer.BindElementBuffer"))
                {
                    elem.Generate();
                    Api.VertexArrayElementBuffer(mesh.BindingId, elem.BindingId);
                }
            }
        }

        public void RenderMesh(GLMeshRenderer manager, bool preservePreviouslyBound = true, uint instances = 1)
        {
            using var prof = Engine.Profiler.Start("OpenGLRenderer.RenderMesh");
            GLMeshRenderer? prev = ActiveMeshRenderer;
            using (Engine.Profiler.Start("OpenGLRenderer.RenderMesh.Bind"))
            {
                BindMeshRenderer(manager);
            }
            using (Engine.Profiler.Start("OpenGLRenderer.RenderMesh.Draw"))
            {
                RenderCurrentMesh(instances);
            }
            using (Engine.Profiler.Start("OpenGLRenderer.RenderMesh.Restore"))
            {
                BindMeshRenderer(preservePreviouslyBound ? prev : null);
            }
        }

        /// <summary>
        /// Render the currently bound mesh.
        /// </summary>
        public void RenderCurrentMesh(uint instances = 1)
        {
            using var prof = Engine.Profiler.Start("OpenGLRenderer.RenderCurrentMesh");
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            using (Engine.Profiler.Start("OpenGLRenderer.RenderCurrentMesh.ReadyCheck"))
            {
                if (!ActiveMeshRenderer.AreBuffersReadyForRendering())
                    return;
            }

            // Skip rendering if index buffer data hasn't been uploaded yet
            var triBuffer = ActiveMeshRenderer.TriangleIndicesBuffer;
            var lineBuffer = ActiveMeshRenderer.LineIndicesBuffer;
            var pointBuffer = ActiveMeshRenderer.PointIndicesBuffer;

            uint triangles = triBuffer?.Data?.ElementCount ?? 0u;
            if (triangles > 0)
            {
                using (Engine.Profiler.Start("OpenGLRenderer.RenderCurrentMesh.DrawTriangles"))
                {
                    Api.DrawElementsInstanced(GLEnum.Triangles, triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, instances);
                    Engine.Rendering.Stats.IncrementDrawCalls();
                    Engine.Rendering.Stats.AddTrianglesRendered((int)(triangles / 3 * instances));
                }
            }

            uint lines = lineBuffer?.Data?.ElementCount ?? 0u;
            if (lines > 0)
            {
                using (Engine.Profiler.Start("OpenGLRenderer.RenderCurrentMesh.DrawLines"))
                {
                    Api.DrawElementsInstanced(GLEnum.Lines, lines, ToGLEnum(ActiveMeshRenderer.LineIndicesElementType), null, instances);
                    Engine.Rendering.Stats.IncrementDrawCalls();
                }
            }

            uint points = pointBuffer?.Data?.ElementCount ?? 0u;
            if (points > 0)
            {
                using (Engine.Profiler.Start("OpenGLRenderer.RenderCurrentMesh.DrawPoints"))
                {
                    Api.DrawElementsInstanced(GLEnum.Points, points, ToGLEnum(ActiveMeshRenderer.PointIndicesElementType), null, instances);
                    Engine.Rendering.Stats.IncrementDrawCalls();
                }
            }
        }

        /// <summary>
        /// Render the currently bound mesh using indirect draw commands.
        /// </summary>
        public void RenderCurrentMeshIndirect()
        {
            using var prof = Engine.Profiler.Start("OpenGLRenderer.RenderCurrentMeshIndirect");
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            uint meshCount = 1u;
            Api.MultiDrawElementsIndirect(GLEnum.Triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, meshCount, 0);
        }
    }
}
