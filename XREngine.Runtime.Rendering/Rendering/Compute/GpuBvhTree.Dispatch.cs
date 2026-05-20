using System;
using System.Numerics;
using XREngine.Data.Rendering;
using static XREngine.Data.Core.XRMath;

namespace XREngine.Rendering.Compute;

// Compute-shader dispatch for GpuBvhTree.
//
// Each Dispatch* method binds the SSBOs and uniforms expected by its shader
// and issues the workgroup launches. All dispatches use ShaderStorage memory
// barriers so subsequent dispatches see the previous writes.
public sealed partial class GpuBvhTree
{
    private void DispatchMortonCodes(uint primitiveCount, Vector3 sceneMin, Vector3 sceneMax)
    {
        if (_mortonProgram is null || _mortonBuffer is null || _aabbBuffer is null)
            return;

        var program = _mortonProgram;
        program.BindBuffer(_aabbBuffer, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer, Bindings.Morton);
        program.Uniform("sceneMin", sceneMin);
        program.Uniform("sceneMax", sceneMax);
        program.Uniform("numObjects", primitiveCount);
        program.Uniform("mortonCapacity", GetMortonCapacity());
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);
        program.DispatchCompute(ComputeGroups(primitiveCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    /// <summary>
    /// Sorts (mortonCode, objectId) pairs in <c>_mortonBuffer</c> ascending.
    /// </summary>
    /// <remarks>
    /// Two code paths:
    /// <list type="bullet">
    ///   <item><b>primitiveCount &lt;= 1024:</b> one workgroup, full
    ///     in-shared-memory bitonic sort.</item>
    ///   <item><b>primitiveCount &gt; 1024:</b> pad + per-tile shared-memory
    ///     sort + cross-tile bitonic merge. The merge stage emits one global
    ///     dispatch per j-pass that spans more than one workgroup
    ///     (<c>j &gt;= 1024</c>), then collapses the remaining j-passes
    ///     (<c>j = 512..1</c>) into a single shared-memory dispatch of
    ///     <c>merge_morton_local.comp</c>.</item>
    /// </list>
    /// </remarks>
    private void SortMortonCodes(uint primitiveCount)
    {
        if (primitiveCount <= 1 || _mortonBuffer is null)
            return;

        if (primitiveCount <= 1024)
        {
            var program = _smallSortProgram;
            if (program is null)
                return;
            program.BindBuffer(_mortonBuffer, Bindings.Morton);
            program.Uniform("numObjects", primitiveCount);
            program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            return;
        }

        // Fail fast on missing programs so we can never leave the buffer in a
        // half-sorted state (e.g. padded but not sorted, which would feed
        // garbage into bvh_build).
        var padProgram = _padProgram;
        var tileProgram = _tileSortProgram;
        var mergeProgram = _mergeProgram;
        var mergeLocalProgram = _mergeLocalProgram;
        if (padProgram is null || tileProgram is null || mergeProgram is null || mergeLocalProgram is null)
            return;

        // primitiveCount > 1024 here, so NextPowerOfTwo(primitiveCount) >= 2048.
        uint paddedCount = NextPowerOfTwo(primitiveCount);

        // Pad: fill the padded tail with sentinels so the tile sort can treat
        // every tile as a full power-of-two block.
        padProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        padProgram.Uniform("numObjects", primitiveCount);
        padProgram.Uniform("paddedCount", paddedCount);
        padProgram.DispatchCompute(ComputeGroups(paddedCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        // Per-tile bitonic sort. paddedCount >= 2048 guarantees >= 2 tiles.
        tileProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        tileProgram.Uniform("paddedCount", paddedCount);
        tileProgram.DispatchCompute(paddedCount / 1024u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        // Cross-tile bitonic merge.
        //
        // For each K (the bitonic run length), we issue a global-memory pass
        // only for j-passes that cross workgroup boundaries (j >= 1024). All
        // remaining passes (j = 512..1) are collapsed into a single dispatch
        // of merge_morton_local.comp, which does them in shared memory with
        // cheap workgroup `barrier()`s.
        //
        // For paddedCount = 2^20 this cuts merge dispatches from ~155 to ~65
        // and replaces ~10 full-array global-memory passes per K with one
        // shared-memory pass.
        mergeProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        mergeProgram.Uniform("paddedCount", paddedCount);
        mergeLocalProgram.BindBuffer(_mortonBuffer, Bindings.Morton);
        mergeLocalProgram.Uniform("paddedCount", paddedCount);

        uint localGroupCount = paddedCount / 1024u;
        for (uint k = 2048u; k <= paddedCount; k <<= 1)
        {
            // Global merge for j-passes that span beyond a single 1024-wide
            // workgroup. For k = 2048 this is just j = 1024; larger k adds
            // j = 2048, 4096, ....
            mergeProgram.Uniform("K", k);
            for (uint j = k >> 1; j >= 1024u; j >>= 1)
            {
                mergeProgram.Uniform("J", j);
                mergeProgram.DispatchCompute(ComputeGroups(paddedCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            }

            // Shared-memory merge for j = min(K/2, 512), ..., 1. Each
            // workgroup handles 1024 contiguous elements; pairs (i, i^j) stay
            // inside the same workgroup for all j <= 512.
            mergeLocalProgram.Uniform("K", k);
            mergeLocalProgram.DispatchCompute(localGroupCount, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }
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
        program.BindBuffer(_aabbBuffer, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_rangeBuffer, Bindings.Range);
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);
        program.Uniform("numPrimitives", primitiveCount);
        program.Uniform("nodeScalarCapacity", _nodeBuffer.ElementCount);
        program.Uniform("rangeScalarCapacity", _rangeBuffer.ElementCount);
        program.Uniform("mortonCapacity", GetMortonCapacity());

        // OpenGL-compatible replacement for Vulkan specialization constants.
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        // Stage 0: initialize leaves.
        program.Uniform("buildStage", 0u);
        program.DispatchCompute(ComputeGroups(Math.Max(leafCount, 1u), 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        if (internalCount > 0)
        {
            // Stage 1: build internal nodes.
            program.Uniform("buildStage", 1u);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            // Stage 2: assign parents.
            program.Uniform("buildStage", 2u);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        // Stage 3: compute root index.
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
        program.BindBuffer(_aabbBuffer!, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_rangeBuffer, Bindings.Range);
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);

        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        program.DispatchCompute(ComputeGroups(_lastNodeCount, 128u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchRefit()
    {
        if (_refitProgram is null || _nodeBuffer is null || _rangeBuffer is null || _counterBuffer is null || _lastNodeCount == 0)
            return;

        var program = _refitProgram;
        program.BindBuffer(_aabbBuffer!, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_rangeBuffer, Bindings.Range);
        program.BindBuffer(_counterBuffer, Bindings.Counters);
        // bvh_refit.comp does not declare an OverflowFlags binding; nothing to bind here.
        program.Uniform("debugValidation", 0u);

        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);
        program.Uniform("BVH_MODE", _buildMode == BvhBuildMode.MortonPlusSah ? 1u : 0u);

        // Stage 0: clear counters.
        program.Uniform("refitStage", 0u);
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        // Stage 1: refit leaves and propagate.
        program.Uniform("refitStage", 1u);
        program.DispatchCompute(ComputeGroups(_lastNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private static uint ComputeGroups(uint count, uint localSize)
        => (count + localSize - 1u) / localSize;
}
