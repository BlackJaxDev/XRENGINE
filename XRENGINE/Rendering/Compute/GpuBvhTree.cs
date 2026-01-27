using System;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering.Models.Materials;
using static XREngine.Data.Core.XRMath;

namespace XREngine.Rendering.Compute;

/// <summary>
/// A fully GPU-based BVH tree that can be used for scene-level culling, per-model collision,
/// skinned mesh BVH, and other spatial acceleration needs.
/// </summary>
/// <remarks>
/// This class provides a reusable BVH infrastructure that builds and maintains a binary tree
/// on the GPU using compute shaders. The tree supports:
/// - Morton-code based radix construction
/// - Optional SAH refinement
/// - Incremental refit for animated/skinned meshes
/// - Frustum culling traversal
/// - Ray traversal (closest hit, any hit)
/// </remarks>
public sealed class GpuBvhTree : IDisposable
{
    private bool _disposed;
    private readonly object _syncRoot = new();

    // Buffer storage
    private XRDataBuffer? _nodeBuffer;
    private XRDataBuffer? _rangeBuffer;
    private XRDataBuffer? _mortonBuffer;
    private XRDataBuffer? _counterBuffer;
    private XRDataBuffer? _aabbBuffer;

    // Shader programs
    private XRShader? _buildShader;
    private XRShader? _refitShader;
    private XRShader? _refineShader;
    private XRRenderProgram? _buildProgram;
    private XRRenderProgram? _refitProgram;
    private XRRenderProgram? _refineProgram;

    // State tracking
    private uint _lastNodeCount;
    private uint _lastPrimitiveCount;
    private bool _isDirty = true;
    private BvhBuildMode _buildMode = BvhBuildMode.MortonOnly;
    private uint _maxLeafPrimitives = 1;

    /// <summary>
    /// Gets the GPU buffer containing BVH nodes.
    /// Layout: [nodeCount, rootIndex, nodeStrideScalars, maxLeafPrimitives, ...nodes]
    /// </summary>
    public XRDataBuffer? NodeBuffer => _nodeBuffer;

    /// <summary>
    /// Gets the GPU buffer containing primitive ranges for each leaf node.
    /// Layout: [start, count] pairs for each node.
    /// </summary>
    public XRDataBuffer? RangeBuffer => _rangeBuffer;

    /// <summary>
    /// Gets the GPU buffer containing Morton codes and object IDs.
    /// Layout: [mortonCode, objectId] pairs.
    /// </summary>
    public XRDataBuffer? MortonBuffer => _mortonBuffer;

    /// <summary>
    /// Gets the number of nodes currently in the BVH.
    /// </summary>
    public uint NodeCount => _lastNodeCount;

    /// <summary>
    /// Gets the number of primitives (leaf objects) in the BVH.
    /// </summary>
    public uint PrimitiveCount => _lastPrimitiveCount;

    /// <summary>
    /// Gets or sets the BVH construction mode.
    /// </summary>
    public BvhBuildMode BuildMode
    {
        get => _buildMode;
        set
        {
            if (_buildMode == value)
                return;
            _buildMode = value;
            ResetPrograms();
            MarkDirty();
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of primitives per leaf node.
    /// </summary>
    public uint MaxLeafPrimitives
    {
        get => _maxLeafPrimitives;
        set
        {
            uint clamped = Math.Max(1u, value);
            if (_maxLeafPrimitives == clamped)
                return;
            _maxLeafPrimitives = clamped;
            ResetPrograms();
            MarkDirty();
        }
    }

    /// <summary>
    /// Whether the BVH needs to be rebuilt.
    /// </summary>
    public bool IsDirty => _isDirty;

    /// <summary>
    /// Marks the BVH as needing a full rebuild.
    /// </summary>
    public void MarkDirty()
    {
        lock (_syncRoot)
            _isDirty = true;
    }

    /// <summary>
    /// Builds or rebuilds the BVH from the provided AABB data.
    /// </summary>
    /// <param name="aabbBuffer">Buffer containing AABB data (vec4 min, vec4 max pairs).</param>
    /// <param name="primitiveCount">Number of primitives (AABBs) to build from.</param>
    /// <param name="sceneBounds">World-space bounds for Morton code normalization.</param>
    public void Build(XRDataBuffer aabbBuffer, uint primitiveCount, AABB sceneBounds)
    {
        if (primitiveCount == 0)
        {
            Clear();
            return;
        }

        lock (_syncRoot)
        {
            _aabbBuffer = aabbBuffer;
            EnsurePrograms();
            EnsureBuffers(primitiveCount);

            Vector3 sceneMin = sceneBounds.Min;
            Vector3 sceneMax = sceneBounds.Max;
            if (sceneMin == sceneMax)
            {
                sceneMin -= new Vector3(0.5f);
                sceneMax += new Vector3(0.5f);
            }

            // Morton code generation
            DispatchMortonCodes(primitiveCount, sceneMin, sceneMax);

            // Sort Morton codes (simple for now, can add radix sort later)
            SortMortonCodes(primitiveCount);

            // Build BVH hierarchy
            DispatchBuild(primitiveCount);

            // Optional SAH refinement
            if (_buildMode == BvhBuildMode.MortonPlusSah)
                DispatchRefine();

            // Refit bounds
            DispatchRefit();

            _lastPrimitiveCount = primitiveCount;
            _isDirty = false;
        }
    }

    /// <summary>
    /// Refits the BVH bounds without rebuilding the hierarchy.
    /// Use this for animated/skinned meshes where topology doesn't change.
    /// </summary>
    public void Refit()
    {
        if (_lastNodeCount == 0 || _aabbBuffer is null)
            return;

        lock (_syncRoot)
        {
            EnsurePrograms();
            DispatchRefit();
        }
    }

    /// <summary>
    /// Clears the BVH and releases GPU resources.
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _lastNodeCount = 0;
            _lastPrimitiveCount = 0;
            _isDirty = true;

            ClearBuffer(_nodeBuffer);
            ClearBuffer(_rangeBuffer);
            ClearBuffer(_mortonBuffer);
        }
    }

    private void EnsurePrograms()
    {
        _buildProgram ??= CreateProgram(ref _buildShader, "Scene3D/RenderPipeline/bvh_build.comp");
        _refitProgram ??= CreateProgram(ref _refitShader, "Scene3D/RenderPipeline/bvh_refit.comp");
        _refineProgram ??= CreateProgram(ref _refineShader, "Scene3D/RenderPipeline/bvh_sah_refine.comp");
    }

    private void ResetPrograms()
    {
        _buildProgram = null;
        _refitProgram = null;
        _refineProgram = null;
        _buildShader = null;
        _refitShader = null;
        _refineShader = null;
    }

    private void EnsureBuffers(uint primitiveCount)
    {
        uint leafCount = (primitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint nodeCount = leafCount > 0 ? (leafCount * 2u) - 1u : 0u;
        _lastNodeCount = nodeCount;

        uint nodeScalars = GpuBvhLayout.NodeScalarCapacity(Math.Max(nodeCount, 1u));
        uint rangeScalars = GpuBvhLayout.RangeScalarCapacity(Math.Max(nodeCount, 1u));
        uint mortonScalars = Math.Max(1u, NextPowerOfTwo(primitiveCount)) * 2u;

        EnsureBuffer(ref _nodeBuffer, "GpuBvhTree.Nodes", nodeScalars, 6);
        EnsureBuffer(ref _rangeBuffer, "GpuBvhTree.Ranges", rangeScalars, 7);
        EnsureBuffer(ref _mortonBuffer, "GpuBvhTree.Morton", mortonScalars, null);
        EnsureBuffer(ref _counterBuffer, "GpuBvhTree.Counters", Math.Max(nodeCount, 1u), 11);
    }

    private static void EnsureBuffer(ref XRDataBuffer? buffer, string name, uint scalarCount, uint? bindingIndex)
    {
        if (buffer is null)
        {
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, scalarCount, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                Resizable = true,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
                ShouldMap = false
            };
            if (bindingIndex.HasValue)
                buffer.SetBlockIndex(bindingIndex.Value);
        }
        else if (buffer.ElementCount < scalarCount)
        {
            buffer.Resize(scalarCount, false, true);
        }
    }

    private static void ClearBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        // Zero out the buffer
        uint count = buffer.ElementCount;
        if (count > 0)
        {
            buffer.SetDataRaw(new uint[count], (int)count);
            buffer.PushSubData();
        }
    }

    private void DispatchMortonCodes(uint primitiveCount, Vector3 sceneMin, Vector3 sceneMax)
    {
        // Morton code generation is typically done by the OctreeGPU pipeline
        // For now, we assume the morton buffer is populated externally or use a simple fallback
        // TODO: Add morton_codes.comp dispatch here if needed
    }

    private void SortMortonCodes(uint primitiveCount)
    {
        // Morton code sorting - use bitonic/radix sort
        // For now, assume sorted externally or use simple GPU sort
        // TODO: Add sorting passes here
    }

    private void DispatchBuild(uint primitiveCount)
    {
        if (_buildProgram is null || _nodeBuffer is null || _rangeBuffer is null || _aabbBuffer is null)
            return;

        uint leafCount = (primitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint internalCount = leafCount > 0 ? leafCount - 1u : 0u;
        if (leafCount == 0)
            return;

        var program = _buildProgram;
        program.BindBuffer(_aabbBuffer, 0);
        program.BindBuffer(_mortonBuffer!, 1);
        program.BindBuffer(_nodeBuffer, 2);
        program.BindBuffer(_rangeBuffer, 3);
        program.Uniform("numPrimitives", primitiveCount);
        program.Uniform("nodeScalarCapacity", _nodeBuffer.ElementCount);
        program.Uniform("rangeScalarCapacity", _rangeBuffer.ElementCount);
        program.Uniform("mortonCapacity", _mortonBuffer?.ElementCount ?? 0u);
        
        // Set BVH configuration uniforms (OpenGL-compatible replacement for Vulkan specialization constants)
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        // Stage 0: Initialize leaves
        program.Uniform("buildStage", 0u);
        program.DispatchCompute(ComputeGroups(Math.Max(leafCount, 1u), 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        if (internalCount > 0)
        {
            // Stage 1: Build internal nodes
            program.Uniform("buildStage", 1u);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            // Stage 2: Assign parents
            program.Uniform("buildStage", 2u);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        // Stage 3: Compute root index
        program.Uniform("buildStage", 3u);
        program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchRefine()
    {
        if (_buildMode != BvhBuildMode.MortonPlusSah)
            return;

        if (_refineProgram is null || _nodeBuffer is null || _rangeBuffer is null || _lastNodeCount == 0)
            return;

        var program = _refineProgram;
        program.BindBuffer(_aabbBuffer!, 0);
        program.BindBuffer(_mortonBuffer!, 1);
        program.BindBuffer(_nodeBuffer, 2);
        program.BindBuffer(_rangeBuffer, 3);
        
        // Set BVH configuration uniforms (OpenGL-compatible replacement for Vulkan specialization constants)
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);
        
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 128u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchRefit()
    {
        if (_refitProgram is null || _nodeBuffer is null || _rangeBuffer is null || _counterBuffer is null || _lastNodeCount == 0)
            return;

        var program = _refitProgram;
        program.BindBuffer(_aabbBuffer!, 0);
        program.BindBuffer(_mortonBuffer!, 1);
        program.BindBuffer(_nodeBuffer, 2);
        program.BindBuffer(_rangeBuffer, 3);
        program.BindBuffer(_counterBuffer, 11);
        program.Uniform("debugValidation", 0u);
        
        // Set BVH configuration uniforms (OpenGL-compatible replacement for Vulkan specialization constants)
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        // Stage 0: Clear counters
        program.Uniform("refitStage", 0u);
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        // Stage 1: Refit leaves and propagate
        program.Uniform("refitStage", 1u);
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private static uint ComputeGroups(uint count, uint localSize)
        => (count + localSize - 1u) / localSize;

    private static XRRenderProgram CreateProgram(ref XRShader? shader, string path)
    {
        shader ??= ShaderHelper.LoadEngineShader(path, EShaderType.Compute);
        return new XRRenderProgram(true, false, shader);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _nodeBuffer?.Dispose();
        _rangeBuffer?.Dispose();
        _mortonBuffer?.Dispose();
        _counterBuffer?.Dispose();

        _nodeBuffer = null;
        _rangeBuffer = null;
        _mortonBuffer = null;
        _counterBuffer = null;
    }
}

/// <summary>
/// Interface for objects that can provide BVH data for GPU culling.
/// </summary>
public interface IGpuBvhProvider
{
    /// <summary>
    /// Gets the BVH node buffer for GPU traversal.
    /// </summary>
    XRDataBuffer? BvhNodeBuffer { get; }

    /// <summary>
    /// Gets the primitive range buffer for leaf node lookups.
    /// </summary>
    XRDataBuffer? BvhRangeBuffer { get; }

    /// <summary>
    /// Gets the Morton code buffer with object IDs.
    /// </summary>
    XRDataBuffer? BvhMortonBuffer { get; }

    /// <summary>
    /// Gets the number of nodes in the BVH.
    /// </summary>
    uint BvhNodeCount { get; }

    /// <summary>
    /// Whether the BVH is ready for use.
    /// </summary>
    bool IsBvhReady { get; }
}
