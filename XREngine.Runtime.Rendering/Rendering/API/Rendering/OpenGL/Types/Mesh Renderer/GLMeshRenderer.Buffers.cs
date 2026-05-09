using Silk.NET.OpenGL;
using System.Linq;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            private const uint MaxTrackedVertexAttribs = 16u;
            private const uint ComputeInterleavedBinding = 9u;
            private const uint ComputePositionBinding = 11u;
            private const uint ComputeNormalBinding = 12u;
            private const uint ComputeTangentBinding = 15u;

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
                using var prof = Engine.Profiler.Start("GLMeshRenderer.MakeIndexBuffers", ProfilerScopeKind.OneOffInvoke);
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
            /// The underlying mesh index buffer is built asynchronously by <see cref="XRMesh.GetIndexBuffer"/>;
            /// the first call typically returns <c>null</c> and schedules the build on a background task.
            /// When the build completes we re-enqueue this renderer so <see cref="MakeIndexBuffers"/> runs
            /// again on the render thread and picks up the cached buffer.
            /// </summary>
            private void SetIndexBuffer(ref GLDataBuffer? buffer, ref IndexSize bufferElementSize, XRMesh mesh, EPrimitiveType type)
            {
                var xrBuffer = mesh.GetIndexBuffer(
                    type,
                    out bufferElementSize,
                    EBufferTarget.ElementArrayBuffer,
                    OnAsyncIndexBufferReady);

                buffer = xrBuffer is not null
                    ? Renderer.GenericToAPI<GLDataBuffer>(xrBuffer)
                    : null;

                Dbg($"SetIndexBuffer type={type} elementSize={bufferElementSize} buffer={(buffer is null ? "null (async pending)" : "ready")}", "Buffers");
            }

            /// <summary>
            /// Invoked (possibly on a background thread) when XRMesh finishes building an index buffer
            /// that was previously pending. Marshals back onto the render thread to attach the buffer.
            /// </summary>
            private void OnAsyncIndexBufferReady(XRDataBuffer xrBuffer, IndexSize elementSize)
            {
                RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
                    RefreshIndexBuffersFromCache,
                    "GLMeshRenderer.RefreshIndexBuffersFromCache");
            }

            /// <summary>
            /// Fills any currently-null index buffer slots from the now-cached XRMesh index buffers.
            /// Existing non-null buffers are left untouched to avoid destroying live GL resources.
            /// </summary>
            private void RefreshIndexBuffersFromCache()
            {
                var mesh = Mesh;
                if (mesh is null)
                    return;

                bool changed = false;
                changed |= TryRefreshIndexBuffer(ref _triangleIndicesBuffer, ref _trianglesElementType, mesh, EPrimitiveType.Triangles);
                changed |= TryRefreshIndexBuffer(ref _lineIndicesBuffer, ref _lineIndicesElementType, mesh, EPrimitiveType.Lines);
                changed |= TryRefreshIndexBuffer(ref _pointIndicesBuffer, ref _pointIndicesElementType, mesh, EPrimitiveType.Points);

                if (changed)
                {
                    BuffersBound = false;
                    Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                }
            }

            private bool TryRefreshIndexBuffer(ref GLDataBuffer? buffer, ref IndexSize bufferElementSize, XRMesh mesh, EPrimitiveType type)
            {
                if (buffer is not null)
                    return false;

                // No callback here: by the time we get invoked the buffer is already cached.
                // If it somehow isn't (e.g. the mesh has no indices of this type) we get null back
                // and simply leave the slot null.
                var xrBuffer = mesh.GetIndexBuffer(type, out bufferElementSize);
                if (xrBuffer is null)
                    return false;

                buffer = Renderer.GenericToAPI<GLDataBuffer>(xrBuffer);
                return buffer is not null;
            }

            /// <summary>
            /// Merge mesh-level and renderer-level buffers into the VAO binding cache.
            /// </summary>
            private void CollectBuffers()
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CollectBuffers", ProfilerScopeKind.OneOffInvoke);
                _bufferCache = [];
                _ssboBufferCache = [];
                _allBuffersList = [];
                Dbg("CollectBuffers start", "Buffers");

                var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
                var rendBuffers = (IEventDictionary<string, XRDataBuffer>)MeshRenderer.Buffers;

                if (meshBuffers is not null)
                    foreach (var pair in meshBuffers)
                        AddCollectedBuffer(pair.Key, pair.Value);

                foreach (var pair in rendBuffers)
                    AddCollectedBuffer(pair.Key, pair.Value);

                XRMesh? mesh = Mesh;
                bool allowSkinning = Engine.Rendering.Settings.AllowSkinning;
                bool allowBlendshapes = Engine.Rendering.Settings.AllowBlendshapes;
                bool hasSkinning = mesh?.HasSkinning == true;
                bool hasBlendshapes = mesh?.BlendshapeCount > 0;
                bool useComputeSkinning = hasSkinning
                    && allowSkinning
                    && Engine.Rendering.Settings.CalculateSkinningInComputeShader;
                bool useVertexSkinning = hasSkinning && allowSkinning && !useComputeSkinning;
                bool optimizeSkinningTo4Weights = useVertexSkinning
                    && (Engine.Rendering.Settings.OptimizeSkinningTo4Weights
                        || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && (mesh?.MaxWeightCount ?? int.MaxValue) <= 4));
                bool useComputeBlendshapes = hasBlendshapes
                    && allowBlendshapes
                    && (Engine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);
                bool useVertexBlendshapes = hasBlendshapes && allowBlendshapes && !useComputeBlendshapes;

                if (!useVertexSkinning)
                {
                    RemoveCollectedBuffer(ECommonBufferType.BoneMatrixOffset.ToString());
                    RemoveCollectedBuffer(ECommonBufferType.BoneMatrixCount.ToString());
                    RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrixIndices}Buffer");
                    RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrixWeights}Buffer");
                    RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrices}Buffer");
                    RemoveCollectedBuffer($"{ECommonBufferType.BoneInvBindMatrices}Buffer");
                }
                else if (optimizeSkinningTo4Weights)
                {
                    RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrixIndices}Buffer");
                    RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrixWeights}Buffer");
                }

                if (!useVertexBlendshapes)
                {
                    RemoveCollectedBuffer(ECommonBufferType.BlendshapeCount.ToString());
                    RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeIndices}Buffer");
                    RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer");
                    RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeWeights}Buffer");
                }

                Dbg($"CollectBuffers end. Total={_bufferCache.Count}, SSBOs={_ssboBufferCache.Count}", "Buffers");
            }

            private void AddCollectedBuffer(string key, XRDataBuffer xrBuffer)
            {
                var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(xrBuffer)!;
                if (_bufferCache.TryGetValue(key, out var previous))
                {
                    _bufferCache.Remove(key);
                    RemoveCollectedBufferValue(previous);
                }

                _bufferCache[key] = glBuffer;
                if (!_allBuffersList.Contains(glBuffer))
                    _allBuffersList.Add(glBuffer);
                if (xrBuffer.Target == EBufferTarget.ShaderStorageBuffer && !_ssboBufferCache.Contains(glBuffer))
                    _ssboBufferCache.Add(glBuffer);
            }

            private void RemoveCollectedBuffer(string key)
            {
                if (!_bufferCache.Remove(key, out var glBuffer))
                    return;

                RemoveCollectedBufferValue(glBuffer);
            }

            private void RemoveCollectedBufferValue(GLDataBuffer glBuffer)
            {
                if (_bufferCache.ContainsValue(glBuffer))
                    return;

                _allBuffersList.Remove(glBuffer);
                if (glBuffer.Data.Target == EBufferTarget.ShaderStorageBuffer)
                    _ssboBufferCache.Remove(glBuffer);
            }

            private void Buffers_Removed(string key, XRDataBuffer value)
            {
                if (_bufferCache.TryGetValue(key, out var glBuffer))
                {
                    _bufferCache.Remove(key);
                    RemoveCollectedBufferValue(glBuffer);
                    BuffersBound = false;
                }
            }

            private void Buffers_Added(string key, XRDataBuffer value)
            {
                AddCollectedBuffer(key, value);
                BuffersBound = false;
            }

            /// <summary>
            /// Rebind SSBOs for the current program; important when GL state is reused across draws.
            /// </summary>
            private void BindSSBOs(GLRenderProgram program)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.BindSSBOs", ProfilerScopeKind.AlwaysOnHotPathLoop);
                int count = _ssboBufferCache.Count;
                for (int i = 0; i < count; i++)
                    _ssboBufferCache[i].BindSSBO(program);

                if (IsBatchedTextDiagnosticMesh() && s_batchedTextSsboBindDiagCount++ < 40)
                {
                    Debug.Log(
                        ELogCategory.UI,
                        "[FpsTextDiag] GLMeshRenderer.BindSSBOs #{0}: program='{1}' ssbos=[{2}]",
                        s_batchedTextSsboBindDiagCount,
                        program.Data?.Name ?? program.BindingId.ToString(),
                        GetShaderStorageBindingSummary());
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

                ClearSkinnedVertexBufferBindings();
            }

            private void ClearSkinnedVertexBufferBindings()
            {
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeInterleavedBinding, 0);
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputePositionBinding, 0);
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeNormalBinding, 0);
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeTangentBinding, 0);
            }

            /// <summary>
            /// Bind compute-skinned outputs as SSBOs or attributes depending on configuration.
            /// </summary>
            private void BindSkinnedVertexBuffers(GLRenderProgram vertexProgram)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.BindSkinnedVertexBuffers", ProfilerScopeKind.AlwaysOnHotPathLoop);
                var mesh = MeshRenderer.Mesh;
                bool useComputeSkinning = mesh?.HasSkinning == true
                    && Engine.Rendering.Settings.AllowSkinning
                    && Engine.Rendering.Settings.CalculateSkinningInComputeShader;
                bool useComputeBlendshapes = mesh?.BlendshapeCount > 0
                    && Engine.Rendering.Settings.AllowBlendshapes
                    && (Engine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);

                if (!useComputeSkinning && !useComputeBlendshapes)
                {
                    ClearSkinnedVertexBufferBindings();
                    return;
                }

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

                if (skinnedPos is not null)
                {
                    var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedPos);
                    if (glBuffer is not null)
                    {
                        glBuffer.Generate();
                        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputePositionBinding, glBuffer.BindingId);
                    }
                }
                else
                {
                    Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputePositionBinding, 0);
                }

                if (skinnedNorm is not null)
                {
                    var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedNorm);
                    if (glBuffer is not null)
                    {
                        glBuffer.Generate();
                        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeNormalBinding, glBuffer.BindingId);
                    }
                }
                else
                {
                    Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeNormalBinding, 0);
                }

                if (skinnedTan is not null)
                {
                    var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedTan);
                    if (glBuffer is not null)
                    {
                        glBuffer.Generate();
                        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeTangentBinding, glBuffer.BindingId);
                    }
                }
                else
                {
                    Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeTangentBinding, 0);
                }

                BindMeshDeformSourceBuffers();

                Dbg("Bound skinned vertex buffers as SSBOs for compute pre-pass", "Buffers");
            }

            private void BindMeshDeformSourceBuffers()
            {
                if (MeshRenderer.DeformerPositionsBuffer is null || MeshRenderer.DeformMeshRenderer is null || MeshRenderer.MeshDeformInfluences is null)
                    return;

                var deformerRenderer = MeshRenderer.DeformMeshRenderer;
                if (deformerRenderer.SkinnedInterleavedBuffer is not null)
                {
                    Dbg("Mesh deform compute-source aliasing skipped for interleaved deformer output; CPU mirror path remains active.", "Buffers");
                    return;
                }

                BindStorageBufferAtBinding(deformerRenderer.SkinnedPositionsBuffer, 0u);

                uint nextBinding = 2u;
                if (MeshRenderer.DeformerNormalsBuffer is not null)
                    BindStorageBufferAtBinding(deformerRenderer.SkinnedNormalsBuffer, nextBinding++);
                if (MeshRenderer.DeformerTangentsBuffer is not null)
                    BindStorageBufferAtBinding(deformerRenderer.SkinnedTangentsBuffer, nextBinding);
            }

            private void BindStorageBufferAtBinding(XRDataBuffer? buffer, uint binding)
            {
                if (buffer is null)
                    return;

                var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(buffer);
                if (glBuffer is null)
                    return;

                glBuffer.Generate();
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, binding, glBuffer.BindingId);
            }

            /// <summary>
            /// Bind interleaved skinned buffer as SSBO for compute skinning.
            /// </summary>
            private void BindSkinnedInterleavedBuffer(GLRenderProgram vertexProgram, XRDataBuffer skinnedInterleavedBuffer)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.BindSkinnedInterleavedBuffer", ProfilerScopeKind.AlwaysOnHotPathLoop);
                var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(skinnedInterleavedBuffer);
                if (glBuffer is null)
                    return;

                glBuffer.Generate();

                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeInterleavedBinding, glBuffer.BindingId);
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputePositionBinding, 0);
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeNormalBinding, 0);
                Api.BindBufferBase(GLEnum.ShaderStorageBuffer, ComputeTangentBinding, 0);

                Dbg("Bound skinned interleaved buffer as SSBO at binding 9 for compute pre-pass", "Buffers");
            }

            private void ResetVertexArrayBindings()
            {
                uint vaoId = BindingId;
                Api.VertexArrayElementBuffer(vaoId, 0);

                for (uint attribIndex = 0; attribIndex < MaxTrackedVertexAttribs; attribIndex++)
                {
                    Api.DisableVertexArrayAttrib(vaoId, attribIndex);
                    Api.VertexArrayAttribBinding(vaoId, attribIndex, attribIndex);
                    Api.VertexArrayBindingDivisor(vaoId, attribIndex, 0);
                    Api.VertexArrayVertexBuffer(vaoId, attribIndex, 0, 0, 16);
                }
            }

            /// <summary>
            /// Bind VAO attributes/index buffers for the provided program if not already bound.
            /// </summary>
            public void BindBuffers(GLRenderProgram program)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.BindBuffers", ProfilerScopeKind.ConditionalLoop);
                var mesh = Mesh;
                if (BuffersBound)
                {
                    if (!VertexArrayBindingsStale())
                    {
                        Dbg("BindBuffers early-out: already bound", "Buffers");
                        return;
                    }

                    Dbg("BindBuffers: VAO bindings stale, rebinding", "Buffers");
                    BuffersBound = false;
                }

                if (!program.IsLinked)
                {
                    Dbg("BindBuffers: program not linked, skipping", "Buffers");
                    return;
                }

                using (Engine.Profiler.Start("GLMeshRenderer.BindBuffers.BindVAO", ProfilerScopeKind.ConditionalLoop))
                {
                    Renderer.BindMeshRenderer(this);
                }
                ResetVertexArrayBindings();
                Dbg(mesh is null ? "BindBuffers: binding renderer buffers (mesh=null)" : "BindBuffers: binding attribute & index buffers", "Buffers");

                // Track whether at least one vertex attribute was successfully bound.
                // If none bind (e.g. wrong program or OOM-corrupted state), we must not mark BuffersBound.
                int attributesBound = 0;
                using (Engine.Profiler.Start("GLMeshRenderer.BindBuffers.BindAttributes", ProfilerScopeKind.ConditionalLoop))
                {
                    foreach (GLDataBuffer buffer in _bufferCache.Values)
                    {
                        using (Engine.Profiler.Start("GLMeshRenderer.BindBuffers.Buffer", ProfilerScopeKind.ConditionalLoop))
                        {
                            buffer.Generate();
                            if (buffer.TryGetAttributeLocation(program, out _))
                                attributesBound++;
                            buffer.BindToRenderer(program, this);
                        }
                    }
                }

                if (attributesBound == 0 && _bufferCache.Count > 0)
                {
                    string programName = program.Data?.Name ?? program.BindingId.ToString();
                    Debug.OpenGLWarning($"[GLMeshRenderer] BindBuffers: no vertex attributes found in program '{programName}'. Skipping VAO setup to prevent rendering with corrupt state.");

                    Renderer.BindMeshRenderer(null);
                    return;
                }

                using (Engine.Profiler.Start("GLMeshRenderer.BindBuffers.BindIndexBuffers", ProfilerScopeKind.ConditionalLoop))
                {
                    if (TriangleIndicesBuffer is not null)
                        Api.VertexArrayElementBuffer(BindingId, TriangleIndicesBuffer.BindingId);
                    if (LineIndicesBuffer is not null)
                        Api.VertexArrayElementBuffer(BindingId, LineIndicesBuffer.BindingId);
                    if (PointIndicesBuffer is not null)
                        Api.VertexArrayElementBuffer(BindingId, PointIndicesBuffer.BindingId);
                }
                CaptureVertexArrayBindingSnapshot();

                using (Engine.Profiler.Start("GLMeshRenderer.BindBuffers.UnbindVAO", ProfilerScopeKind.ConditionalLoop))
                {
                    Renderer.BindMeshRenderer(null);
                }

                BuffersBound = true;
                Dbg("BindBuffers: complete", "Buffers");
            }

            private void CaptureVertexArrayBindingSnapshot()
            {
                _boundVertexArrayBuffers.Clear();
                _boundVertexArrayBufferIds.Clear();

                foreach (GLDataBuffer buffer in _bufferCache.Values)
                {
                    if (buffer.Data.Target != EBufferTarget.ArrayBuffer)
                        continue;

                    _boundVertexArrayBuffers.Add(buffer);
                    _boundVertexArrayBufferIds.Add(GetBufferBindingId(buffer));
                }

                _boundTriangleIndicesBufferId = GetBufferBindingId(TriangleIndicesBuffer);
                _boundLineIndicesBufferId = GetBufferBindingId(LineIndicesBuffer);
                _boundPointIndicesBufferId = GetBufferBindingId(PointIndicesBuffer);
            }

            private bool VertexArrayBindingsStale()
            {
                if (!BuffersBound)
                    return false;

                if (_boundVertexArrayBuffers.Count != _boundVertexArrayBufferIds.Count)
                    return true;

                for (int i = 0; i < _boundVertexArrayBuffers.Count; i++)
                {
                    if (GetBufferBindingId(_boundVertexArrayBuffers[i]) != _boundVertexArrayBufferIds[i])
                        return true;
                }

                return _boundTriangleIndicesBufferId != GetBufferBindingId(TriangleIndicesBuffer) ||
                    _boundLineIndicesBufferId != GetBufferBindingId(LineIndicesBuffer) ||
                    _boundPointIndicesBufferId != GetBufferBindingId(PointIndicesBuffer);
            }
        }
    }
}
