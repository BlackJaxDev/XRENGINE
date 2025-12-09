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
    private const uint ThreadGroupSize = 256u;

    private static readonly Lazy<SkinningPrepassDispatcher> _instance = new(() => new SkinningPrepassDispatcher());
    public static SkinningPrepassDispatcher Instance => _instance.Value;

    private readonly object _syncRoot = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, RendererResources> _resources = new();

    private XRShader? _shader;
    private XRRenderProgram? _program;

    private SkinningPrepassDispatcher()
    {
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _program?.Destroy();
            _shader?.Destroy();
            _program = null;
            _shader = null;
        }
    }

    /// <summary>
    /// Executes the compute pre-pass for the supplied renderer if the current engine settings require it.
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

        bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && mesh.MaxWeightCount <= 4);

        lock (_syncRoot)
        {
            EnsureProgram();
            if (_program is null)
                return;

            RendererResources resources = _resources.GetValue(renderer, r => new RendererResources(r));
            if (!resources.Validate(mesh, doSkinning, doBlendshapes, optimizeTo4Weights))
                return;

            resources.SyncDynamicBuffers();
            resources.RestoreBaseVertexStreams();
            resources.BindBlocks(doSkinning, doBlendshapes);

            uint vertexCount = (uint)mesh.VertexCount;
            _program.Uniform("vertexCount", vertexCount);
            _program.Uniform("hasSkinning", doSkinning ? 1 : 0);
            _program.Uniform("hasNormals", mesh.HasNormals ? 1 : 0);
            _program.Uniform("hasTangents", mesh.HasTangents ? 1 : 0);
            _program.Uniform("hasBlendshapes", doBlendshapes ? 1 : 0);
            _program.Uniform("allowBlendshapes", Engine.Rendering.Settings.AllowBlendshapes ? 1 : 0);
            _program.Uniform("absoluteBlendshapePositions", Engine.Rendering.Settings.UseAbsoluteBlendshapePositions ? 1 : 0);
            _program.Uniform("maxBlendshapeAccumulation", mesh.MaxBlendshapeAccumulation ? 1 : 0);
            _program.Uniform("useIntegerUniforms", Engine.Rendering.Settings.UseIntegerUniformsInShaders ? 1 : 0);
            _program.Uniform("optimized4", optimizeTo4Weights ? 1 : 0);
            _program.Uniform("interleaved", mesh.Interleaved ? 1 : 0);
            _program.Uniform("interleavedStride", mesh.Interleaved ? mesh.InterleavedStride : 0u);
            _program.Uniform("positionOffsetBytes", mesh.PositionOffset);
            _program.Uniform("normalOffsetBytes", mesh.NormalOffset ?? 0u);
            _program.Uniform("tangentOffsetBytes", mesh.TangentOffset ?? 0u);

            uint groupsX = Math.Max(1u, (vertexCount + ThreadGroupSize - 1u) / ThreadGroupSize);
            _program.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray);
        }
    }

    private void EnsureProgram()
    {
        if (_program is not null)
            return;

        _shader ??= ShaderHelper.LoadEngineShader(ShaderPath, EShaderType.Compute);
        _program = _shader is null ? null : new XRRenderProgram(true, false, _shader);
    }

    private sealed class RendererResources
    {
        private readonly XRMeshRenderer _renderer;

        public RendererResources(XRMeshRenderer renderer)
        {
            _renderer = renderer;
        }

        public bool Validate(XRMesh mesh, bool doSkinning, bool doBlendshapes, bool optimizeTo4Weights)
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

            if (mesh.Interleaved)
            {
                if (mesh.InterleavedVertexBuffer is null)
                    return false;
            }
            else if (mesh.PositionsBuffer is null)
                return false;

            return true;
        }

        public void SyncDynamicBuffers()
        {
            _renderer.PushBoneMatricesToGPU();
            _renderer.PushBlendshapeWeightsToGPU();
        }

        public void RestoreBaseVertexStreams()
        {
            var mesh = _renderer.Mesh;
            if (mesh is null)
                return;

            if (mesh.Interleaved)
            {
                mesh.InterleavedVertexBuffer?.PushData();
            }
            else
            {
                mesh.PositionsBuffer?.PushData();
                if (mesh.HasNormals)
                    mesh.NormalsBuffer?.PushData();
                if (mesh.HasTangents)
                    mesh.TangentsBuffer?.PushData();
            }
        }

        public void BindBlocks(bool doSkinning, bool doBlendshapes)
        {
            var mesh = _renderer.Mesh;
            if (mesh is null)
                return;

            mesh.PositionsBuffer?.SetBlockIndex(8);
            mesh.NormalsBuffer?.SetBlockIndex(9);
            mesh.TangentsBuffer?.SetBlockIndex(10);
            mesh.InterleavedVertexBuffer?.SetBlockIndex(15);

            if (doSkinning)
            {
                _renderer.BoneMatricesBuffer?.SetBlockIndex(0);
                _renderer.BoneInvBindMatricesBuffer?.SetBlockIndex(1);
                mesh.BoneWeightOffsets?.SetBlockIndex(2);
                mesh.BoneWeightCounts?.SetBlockIndex(3);
                mesh.BoneWeightIndices?.SetBlockIndex(13);
                mesh.BoneWeightValues?.SetBlockIndex(14);
            }

            if (doBlendshapes)
            {
                mesh.BlendshapeCounts?.SetBlockIndex(4);
                mesh.BlendshapeIndices?.SetBlockIndex(5);
                mesh.BlendshapeDeltas?.SetBlockIndex(6);
                _renderer.BlendshapeWeights?.SetBlockIndex(7);
            }
        }
    }
}
