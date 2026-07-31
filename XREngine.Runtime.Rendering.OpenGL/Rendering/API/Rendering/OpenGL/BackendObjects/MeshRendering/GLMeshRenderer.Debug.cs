using XREngine.Extensions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            // Simple opt-in verbose logger to aid GPU/VAO troubleshooting in debug builds.
            private static volatile bool _verbose = false;
            private static int _deformationBindingDiagnosticCount;
            private static readonly HashSet<string> _enabledDebugCategories = new(StringComparer.OrdinalIgnoreCase)
            {
                "Lifecycle",
                "Buffers",
                "Programs",
                "Render",
                "Atlas",
                "General"
            };

            public static void SetVerbose(bool enabled) => _verbose = enabled;

            public static void EnableCategory(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return;
                lock (_enabledDebugCategories) _enabledDebugCategories.Add(category);
            }

            public static void DisableCategory(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return;
                lock (_enabledDebugCategories) _enabledDebugCategories.Remove(category);
            }

            public static void SetCategories(IEnumerable<string> categories)
            {
                lock (_enabledDebugCategories)
                {
                    _enabledDebugCategories.Clear();
                    foreach (var c in categories.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(c))
                            _enabledDebugCategories.Add(c);
                    }
                }
            }

            [Conditional("DEBUG")]
            private static void Dbg(string msg, string category = "General")
            {
                if (!_verbose)
                    return;

                bool enabled;
                lock (_enabledDebugCategories)
                    enabled = _enabledDebugCategories.Contains(category) || _enabledDebugCategories.Contains("All");

                if (enabled)
                    Debug.OpenGL($"[GLMeshRenderer/{category}] {msg}");
            }

            private void LogDeformationBindingDiagnostic(GLRenderProgram vertexProgram)
            {
                if (!RenderDiagnosticsFlags.SkinningPrepassDiag ||
                    _deformationBindingDiagnosticCount >= 20 ||
                    MeshRenderer.SkinnedPositionsBuffer is null)
                {
                    return;
                }

                ++_deformationBindingDiagnosticCount;
                int boundPosition = Api.GetInteger(GLEnum.ShaderStorageBufferBinding, ComputePositionBinding);
                int boundNormal = Api.GetInteger(GLEnum.ShaderStorageBufferBinding, ComputeNormalBinding);
                int boundTangent = Api.GetInteger(GLEnum.ShaderStorageBufferBinding, ComputeTangentBinding);
                uint expectedPosition = GetBufferBindingId(
                    Renderer.GenericToAPI<GLDataBuffer>(MeshRenderer.SkinnedPositionsBuffer));
                uint expectedNormal = GetBufferBindingId(
                    Renderer.GenericToAPI<GLDataBuffer>(MeshRenderer.SkinnedNormalsBuffer));
                uint expectedTangent = GetBufferBindingId(
                    Renderer.GenericToAPI<GLDataBuffer>(MeshRenderer.SkinnedTangentsBuffer));
                int declaredPosition = GetShaderStorageBlockBinding(vertexProgram, "SkinnedPositionsInput");
                int declaredNormal = GetShaderStorageBlockBinding(vertexProgram, "SkinnedNormalsInput");
                int declaredTangent = GetShaderStorageBlockBinding(vertexProgram, "SkinnedTangentsInput");

                Debug.OpenGLWarning(
                    "[SkinDrawBindings] mesh='{0}' program={1} position={2}:{3}/{4}/{5} normal={6}:{7}/{8}/{9} tangent={10}:{11}/{12}/{13}.",
                    Mesh?.Name ?? "<unnamed>",
                    vertexProgram.BindingId,
                    ComputePositionBinding,
                    boundPosition,
                    expectedPosition,
                    declaredPosition,
                    ComputeNormalBinding,
                    boundNormal,
                    expectedNormal,
                    declaredNormal,
                    ComputeTangentBinding,
                    boundTangent,
                    expectedTangent,
                    declaredTangent);
            }

            private int GetShaderStorageBlockBinding(GLRenderProgram program, string blockName)
            {
                uint blockIndex = Api.GetProgramResourceIndex(program.BindingId, GLEnum.ShaderStorageBlock, blockName);
                if (blockIndex == uint.MaxValue)
                    return -1;

                GLEnum property = GLEnum.BufferBinding;
                int binding = -1;
                Api.GetProgramResource(
                    program.BindingId,
                    GLEnum.ShaderStorageBlock,
                    blockIndex,
                    1u,
                    &property,
                    1u,
                    null,
                    &binding);
                return binding;
            }
        }
    }
}
