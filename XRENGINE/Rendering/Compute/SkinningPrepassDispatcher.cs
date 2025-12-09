using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Dispatches the skinning + blendshape compute pre-pass ahead of any viewport rendering.
/// </summary>
internal sealed class SkinningPrepassDispatcher : IDisposable
{
    private const string ShaderPath = "Compute/SkinningPrepass.comp";
    private const string InterleavedShaderPath = "Compute/SkinningPrepassInterleaved.comp";
    private const uint ThreadGroupSize = 256u;

    private static readonly Lazy<SkinningPrepassDispatcher> _instance = new(() => new SkinningPrepassDispatcher());
    public static SkinningPrepassDispatcher Instance => _instance.Value;

    private readonly object _syncRoot = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, RendererResources> _resources = new();

    private XRShader? _shader;
    private XRRenderProgram? _program;
    private XRShader? _interleavedShader;
    private XRRenderProgram? _interleavedProgram;

    private SkinningPrepassDispatcher()
    {
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _program?.Destroy();
            _shader?.Destroy();
            _interleavedProgram?.Destroy();
            _interleavedShader?.Destroy();
            _program = null;
            _shader = null;
            _interleavedProgram = null;
            _interleavedShader = null;
        }
    }

    /// <summary>
    /// Gets the skinned output buffers for a renderer after compute pass has run.
    /// Returns null if compute skinning is not enabled or buffers don't exist.
    /// </summary>
    public (XRDataBuffer? positions, XRDataBuffer? normals, XRDataBuffer? tangents, XRDataBuffer? interleaved) GetSkinnedBuffers(XRMeshRenderer renderer)
    {
        lock (_syncRoot)
        {
            if (_resources.TryGetValue(renderer, out var resources))
                return (resources.SkinnedPositions, resources.SkinnedNormals, resources.SkinnedTangents, resources.SkinnedInterleaved);
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Checks if a renderer has skinned output buffers available.
    /// </summary>
    public bool HasSkinnedBuffers(XRMeshRenderer renderer)
    {
        lock (_syncRoot)
        {
            if (!_resources.TryGetValue(renderer, out var resources))
                return false;
            return resources.SkinnedPositions is not null || resources.SkinnedInterleaved is not null;
        }
    }

    /// <summary>
    /// Executes the compute pre-pass for the supplied renderer if the current engine settings require it.
    /// Supports both interleaved and non-interleaved meshes with separate shader programs.
    /// </summary>
    public void Run(XRMeshRenderer renderer)
    {
        if (AbstractRenderer.Current is null)
            return;

        var mesh = renderer.Mesh;
        if (mesh is null || mesh.VertexCount <= 0)
            return;

        bool doSkinning = Engine.Rendering.Settings.CalculateSkinningInComputeShader && mesh.HasSkinning;
        bool doBlendshapes = Engine.Rendering.Settings.CalculateBlendshapesInComputeShader && mesh.BlendshapeCount > 0 && Engine.Rendering.Settings.AllowBlendshapes;
        if (!doSkinning && !doBlendshapes)
            return;

        bool isInterleaved = mesh.Interleaved;
        bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && mesh.MaxWeightCount <= 4);

        lock (_syncRoot)
        {
            XRRenderProgram? activeProgram;
            if (isInterleaved)
            {
                EnsureInterleavedProgram();
                activeProgram = _interleavedProgram;
            }
            else
            {
                EnsureProgram();
                activeProgram = _program;
            }

            if (activeProgram is null)
                return;

            RendererResources resources = _resources.GetValue(renderer, r => new RendererResources(r));
            if (!resources.Validate(mesh, doSkinning, doBlendshapes, optimizeTo4Weights, isInterleaved))
                return;

            resources.SyncDynamicBuffers();
            resources.BindBlocks(doSkinning, doBlendshapes, isInterleaved);

            uint vertexCount = (uint)mesh.VertexCount;
            activeProgram.Uniform("vertexCount", vertexCount);
            activeProgram.Uniform("hasSkinning", doSkinning ? 1 : 0);
            activeProgram.Uniform("hasNormals", mesh.HasNormals ? 1 : 0);
            activeProgram.Uniform("hasTangents", mesh.HasTangents ? 1 : 0);
            activeProgram.Uniform("hasBlendshapes", doBlendshapes ? 1 : 0);
            activeProgram.Uniform("allowBlendshapes", Engine.Rendering.Settings.AllowBlendshapes ? 1 : 0);
            activeProgram.Uniform("absoluteBlendshapePositions", Engine.Rendering.Settings.UseAbsoluteBlendshapePositions ? 1 : 0);
            activeProgram.Uniform("maxBlendshapeAccumulation", mesh.MaxBlendshapeAccumulation ? 1 : 0);
            activeProgram.Uniform("useIntegerUniforms", Engine.Rendering.Settings.UseIntegerUniformsInShaders ? 1 : 0);
            activeProgram.Uniform("optimized4", optimizeTo4Weights ? 1 : 0);

            // Set interleaved-specific uniforms
            if (isInterleaved)
            {
                activeProgram.Uniform("interleavedStride", mesh.InterleavedStride);
                activeProgram.Uniform("positionOffsetBytes", mesh.PositionOffset);
                activeProgram.Uniform("normalOffsetBytes", mesh.NormalOffset ?? 0u);
                activeProgram.Uniform("tangentOffsetBytes", mesh.TangentOffset ?? 0u);
            }

            uint groupsX = Math.Max(1u, (vertexCount + ThreadGroupSize - 1u) / ThreadGroupSize);
            activeProgram.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray);
        }
    }

    private void EnsureProgram()
    {
        if (_program is not null)
            return;

        _shader ??= ShaderHelper.LoadEngineShader(ShaderPath, EShaderType.Compute);
        _program = _shader is null ? null : new XRRenderProgram(true, false, _shader);
    }

    private void EnsureInterleavedProgram()
    {
        if (_interleavedProgram is not null)
            return;

        _interleavedShader ??= ShaderHelper.LoadEngineShader(InterleavedShaderPath, EShaderType.Compute);
        _interleavedProgram = _interleavedShader is null ? null : new XRRenderProgram(true, false, _interleavedShader);
    }

    private sealed class RendererResources(XRMeshRenderer renderer)
    {
        private readonly XRMeshRenderer _renderer = renderer;
        private int _lastVertexCount;
        private bool _lastWasInterleaved;

        /// <summary>
        /// Gets the output buffer containing skinned positions.
        /// </summary>
        public XRDataBuffer? SkinnedPositions => _renderer.SkinnedPositionsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned normals.
        /// </summary>
        public XRDataBuffer? SkinnedNormals => _renderer.SkinnedNormalsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned tangents.
        /// </summary>
        public XRDataBuffer? SkinnedTangents => _renderer.SkinnedTangentsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned interleaved data.
        /// </summary>
        public XRDataBuffer? SkinnedInterleaved => _renderer.SkinnedInterleavedBuffer;

        public bool Validate(XRMesh mesh, bool doSkinning, bool doBlendshapes, bool optimizeTo4Weights, bool isInterleaved)
        {
            if (doSkinning)
            {
                if (_renderer.BoneMatricesBuffer is null || _renderer.BoneInvBindMatricesBuffer is null)
                    return false;
                if (mesh.BoneWeightOffsets is null || mesh.BoneWeightCounts is null)
                    return false;
                if (!optimizeTo4Weights && (mesh.BoneWeightIndices is null || mesh.BoneWeightValues is null))
                    return false;
            }

            if (doBlendshapes)
            {
                if (mesh.BlendshapeCounts is null || mesh.BlendshapeIndices is null || mesh.BlendshapeDeltas is null)
                    return false;
                if (_renderer.BlendshapeWeights is null)
                    return false;
            }

            // Check required buffers based on interleaved mode
            if (isInterleaved)
            {
                if (mesh.InterleavedVertexBuffer is null)
                    return false;
            }
            else
            {
                if (mesh.PositionsBuffer is null)
                    return false;
            }

            // Ensure output buffers exist and match the mesh size
            EnsureOutputBuffers(mesh, isInterleaved);

            return true;
        }

        private void EnsureOutputBuffers(XRMesh mesh, bool isInterleaved)
        {
            int vertexCount = mesh.VertexCount;
            if (_lastVertexCount == vertexCount && _lastWasInterleaved == isInterleaved)
                return;

            _lastVertexCount = vertexCount;
            _lastWasInterleaved = isInterleaved;

            // Dispose old buffers on renderer
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;

            if (isInterleaved)
            {
                // Create a single interleaved output buffer matching the input format
                // Use resizable=true so compute shader can write to it with mutable storage
                int stride = (int)mesh.InterleavedStride;
                _renderer.SkinnedInterleavedBuffer = new XRDataBuffer(
                    "InterleavedSkinned",
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)(vertexCount * stride / sizeof(float)),
                    EComponentType.Float,
                    1, // Individual floats, stride handled at binding time
                    true, // Resizable - uses mutable storage that compute shaders can write to
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };
            }
            else
            {
                // Create separate output buffers for positions, normals, tangents
                // Use ShaderStorageBuffer target and resizable=true so the compute shader can write to them
                _renderer.SkinnedPositionsBuffer = new XRDataBuffer(
                    ECommonBufferType.Position.ToString(),
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)vertexCount,
                    EComponentType.Float,
                    4, // vec4
                    true, // Resizable - uses mutable storage that compute shaders can write to
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    DisposeOnPush = false
                };

                if (mesh.HasNormals)
                {
                    _renderer.SkinnedNormalsBuffer = new XRDataBuffer(
                        ECommonBufferType.Normal.ToString(),
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4, // vec4
                        true, // Resizable - uses mutable storage that compute shaders can write to
                        false)
                    {
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
                }

                if (mesh.HasTangents)
                {
                    _renderer.SkinnedTangentsBuffer = new XRDataBuffer(
                        ECommonBufferType.Tangent.ToString(),
                        EBufferTarget.ShaderStorageBuffer,
                        (uint)vertexCount,
                        EComponentType.Float,
                        4, // vec4
                        true, // Resizable - uses mutable storage that compute shaders can write to
                        false)
                    {
                        Usage = EBufferUsage.DynamicDraw,
                        DisposeOnPush = false
                    };
                }
            }
        }

        public void SyncDynamicBuffers()
        {
            _renderer.PushBoneMatricesToGPU();
            _renderer.PushBlendshapeWeightsToGPU();
        }

        public void BindBlocks(bool doSkinning, bool doBlendshapes, bool isInterleaved)
        {
            var mesh = _renderer.Mesh;
            if (mesh is null)
                return;

            if (isInterleaved)
            {
                // Bind interleaved input/output buffers
                mesh.InterleavedVertexBuffer?.SetBlockIndex(8);
                _renderer.SkinnedInterleavedBuffer?.SetBlockIndex(9);
            }
            else
            {
                // Bind input buffers (original mesh data) - read-only
                mesh.PositionsBuffer?.SetBlockIndex(8);
                mesh.NormalsBuffer?.SetBlockIndex(9);
                mesh.TangentsBuffer?.SetBlockIndex(10);

                // Bind output buffers (skinned results) - write-only
                _renderer.SkinnedPositionsBuffer?.SetBlockIndex(11);
                _renderer.SkinnedNormalsBuffer?.SetBlockIndex(12);
                _renderer.SkinnedTangentsBuffer?.SetBlockIndex(15);
            }

            if (doSkinning)
            {
                _renderer.BoneMatricesBuffer?.SetBlockIndex(0);
                _renderer.BoneInvBindMatricesBuffer?.SetBlockIndex(1);
                mesh.BoneWeightOffsets?.SetBlockIndex(2);
                mesh.BoneWeightCounts?.SetBlockIndex(3);
                
                // Variable weight skinning buffers have different bindings for interleaved vs non-interleaved
                if (isInterleaved)
                {
                    // Interleaved shader uses bindings 10-11 for variable weights
                    mesh.BoneWeightIndices?.SetBlockIndex(10);
                    mesh.BoneWeightValues?.SetBlockIndex(11);
                }
                else
                {
                    // Non-interleaved shader uses bindings 13-14 for variable weights
                    mesh.BoneWeightIndices?.SetBlockIndex(13);
                    mesh.BoneWeightValues?.SetBlockIndex(14);
                }
            }

            if (doBlendshapes)
            {
                mesh.BlendshapeCounts?.SetBlockIndex(4);
                mesh.BlendshapeIndices?.SetBlockIndex(5);
                mesh.BlendshapeDeltas?.SetBlockIndex(6);
                _renderer.BlendshapeWeights?.SetBlockIndex(7);
            }
        }

        public void Dispose()
        {
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;
            _lastVertexCount = 0;
            _lastWasInterleaved = false;
        }
    }
}
