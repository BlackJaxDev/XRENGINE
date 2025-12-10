using Silk.NET.OpenGL;
using System.Linq;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            /// <summary>
            /// Swap triangle index buffer and mark bindings dirty.
            /// </summary>
            public void SetTriangleIndexBuffer(GLDataBuffer? buffer, IndexSize elementType)
            {
                TriangleIndicesBuffer = buffer;
                _trianglesElementType = elementType;
                BuffersBound = false;
            }

            /// <summary>
            /// Create or refresh index buffers for each primitive topology.
            /// </summary>
            private void MakeIndexBuffers()
            {
                Dbg("MakeIndexBuffers begin", "Buffers");

                var mesh = Mesh;
                if (mesh is null)
                {
                    Dbg("MakeIndexBuffers aborted - mesh null", "Buffers");
                    return;
                }

                _triangleIndicesBuffer?.Destroy();
                SetIndexBuffer(ref _triangleIndicesBuffer, ref _trianglesElementType, mesh, EPrimitiveType.Triangles);

                _lineIndicesBuffer?.Destroy();
                SetIndexBuffer(ref _lineIndicesBuffer, ref _lineIndicesElementType, mesh, EPrimitiveType.Lines);

                _pointIndicesBuffer?.Destroy();
                SetIndexBuffer(ref _pointIndicesBuffer, ref _pointIndicesElementType, mesh, EPrimitiveType.Points);

                Dbg($"MakeIndexBuffers done (tri={(TriangleIndicesBuffer != null ? TriangleIndicesBuffer.Data?.ElementCount : 0)}, line={(LineIndicesBuffer != null ? LineIndicesBuffer.Data?.ElementCount : 0)}, point={(PointIndicesBuffer != null ? PointIndicesBuffer.Data?.ElementCount : 0)})", "Buffers");
            }

            /// <summary>
            /// Helper to set an index buffer for a given primitive type.
            /// </summary>
            private void SetIndexBuffer(ref GLDataBuffer? buffer, ref IndexSize bufferElementSize, XRMesh mesh, EPrimitiveType type)
            {
                buffer = Renderer.GenericToAPI<GLDataBuffer>(mesh.GetIndexBuffer(type, out bufferElementSize))!;
                Dbg($"SetIndexBuffer type={type} elementSize={bufferElementSize}", "Buffers");
            }

            /// <summary>
            /// Merge mesh-level and renderer-level buffers into the VAO binding cache.
            /// </summary>
            private void CollectBuffers()
            {
                _bufferCache = [];
                Dbg("CollectBuffers start", "Buffers");

                var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
                var rendBuffers = (IEventDictionary<string, XRDataBuffer>)MeshRenderer.Buffers;

                if (meshBuffers is not null)
                    foreach (var pair in meshBuffers)
                        _bufferCache[pair.Key] = Renderer.GenericToAPI<GLDataBuffer>(pair.Value)!;

                foreach (var pair in rendBuffers)
                    _bufferCache[pair.Key] = Renderer.GenericToAPI<GLDataBuffer>(pair.Value)!;

                if (Engine.Rendering.Settings.CalculateSkinningInComputeShader)
                {
                    _bufferCache.Remove(ECommonBufferType.BoneMatrixOffset.ToString());
                    _bufferCache.Remove(ECommonBufferType.BoneMatrixCount.ToString());
                }

                Dbg($"CollectBuffers end. Total={_bufferCache.Count}", "Buffers");
            }

            private void Buffers_Removed(string key, XRDataBuffer value)
            {
                _bufferCache.Remove(key);
            }

            private void Buffers_Added(string key, XRDataBuffer value)
            {
                _bufferCache[key] = Renderer.GenericToAPI<GLDataBuffer>(value)!;
            }

            /// <summary>
            /// Rebind SSBOs for the current program; important when GL state is reused across draws.
            /// </summary>
            private void BindSSBOs(GLRenderProgram program)
            {
                int count = 0;
                foreach (var buffer in _bufferCache.Where(x => x.Value.Data.Target == EBufferTarget.ShaderStorageBuffer))
                {
                    buffer.Value.BindSSBO(program);
                    count++;
                }

                if (count > 0)
                    Dbg($"BindSSBOs bound {count} SSBO(s)", "Buffers");
            }

            /// <summary>
            /// Drop compute-skinning outputs when no longer needed.
            /// </summary>
            private void DestroySkinnedBuffers()
            {
                MeshRenderer.SkinnedInterleavedBuffer?.Destroy();
                MeshRenderer.SkinnedPositionsBuffer?.Destroy();
                MeshRenderer.SkinnedNormalsBuffer?.Destroy();
                MeshRenderer.SkinnedTangentsBuffer?.Destroy();

                MeshRenderer.SkinnedInterleavedBuffer = null;
                MeshRenderer.SkinnedPositionsBuffer = null;
                MeshRenderer.SkinnedNormalsBuffer = null;
                MeshRenderer.SkinnedTangentsBuffer = null;
            }

            /// <summary>
            /// Bind compute-skinned outputs as SSBOs or attributes depending on configuration.
            /// </summary>
            private void BindSkinnedVertexBuffers(GLRenderProgram vertexProgram)
            {
                if (!Engine.Rendering.Settings.CalculateSkinningInComputeShader)
                    return;

                var skinnedInterleaved = MeshRenderer.SkinnedInterleavedBuffer;
                if (skinnedInterleaved is not null)
                {
                    BindSkinnedInterleavedBuffer(vertexProgram, skinnedInterleaved);
                    return;
                }

                var skinnedPos = MeshRenderer.SkinnedPositionsBuffer;
                var skinnedNorm = MeshRenderer.SkinnedNormalsBuffer;
                var skinnedTan = MeshRenderer.SkinnedTangentsBuffer;

                if (skinnedPos is null && skinnedNorm is null && skinnedTan is null)
                    return;

                const uint positionBinding = 11;
                const uint normalBinding = 12;
                const uint tangentBinding = 15;

                if (skinnedPos is not null)
                {
                    var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedPos);
                    if (glBuffer is not null)
                    {
                        glBuffer.Generate();
                        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, positionBinding, glBuffer.BindingId);
                    }
                }

                if (skinnedNorm is not null)
                {
                    var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedNorm);
                    if (glBuffer is not null)
                    {
                        glBuffer.Generate();
                        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, normalBinding, glBuffer.BindingId);
                    }
                }

                if (skinnedTan is not null)
                {
                    var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedTan);
                    if (glBuffer is not null)
                    {
                        glBuffer.Generate();
                        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, tangentBinding, glBuffer.BindingId);
                    }
                }

                Dbg("Bound skinned vertex buffers as SSBOs for compute pre-pass", "Buffers");
            }

            /// <summary>
            /// Bind interleaved skinned buffer as SSBO for compute skinning.
            /// </summary>
            private void BindSkinnedInterleavedBuffer(GLRenderProgram vertexProgram, XRDataBuffer skinnedInterleavedBuffer)
            {
                var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedInterleavedBuffer);
                if (glBuffer is null)
                    return;

                glBuffer.Generate();

                const uint interleavedBinding = 9;
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, interleavedBinding, glBuffer.BindingId);

                Dbg("Bound skinned interleaved buffer as SSBO at binding 9 for compute pre-pass", "Buffers");
            }

            /// <summary>
            /// Bind VAO attributes/index buffers for the provided program if not already bound.
            /// </summary>
            public void BindBuffers(GLRenderProgram program)
            {
                var mesh = Mesh;
                if (BuffersBound)
                {
                    Dbg("BindBuffers early-out: already bound", "Buffers");
                    return;
                }

                Renderer.BindMeshRenderer(this);
                Dbg(mesh is null ? "BindBuffers: binding renderer buffers (mesh=null)" : "BindBuffers: binding attribute & index buffers", "Buffers");

                foreach (GLDataBuffer buffer in _bufferCache.Values)
                {
                    buffer.Generate();
                    buffer.BindToRenderer(program, this);
                }

                if (TriangleIndicesBuffer is not null)
                    Api.VertexArrayElementBuffer(BindingId, TriangleIndicesBuffer.BindingId);
                if (LineIndicesBuffer is not null)
                    Api.VertexArrayElementBuffer(BindingId, LineIndicesBuffer.BindingId);
                if (PointIndicesBuffer is not null)
                    Api.VertexArrayElementBuffer(BindingId, PointIndicesBuffer.BindingId);

                Renderer.BindMeshRenderer(null);

                BuffersBound = true;
                Dbg("BindBuffers: complete", "Buffers");
            }
        }
    }
}
