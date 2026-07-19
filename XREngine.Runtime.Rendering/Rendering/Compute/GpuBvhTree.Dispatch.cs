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

        using var timing = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Morton, primitiveCount);
        var program = _mortonProgram;
        program.BindBuffer(_aabbBuffer, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer, Bindings.Morton);
        program.Uniform("sceneMin", sceneMin);
        program.Uniform("sceneMax", sceneMax);
        program.Uniform("numObjects", primitiveCount);
        program.Uniform("mortonCapacity", GetMortonCapacity());
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);

        // Reset in-band before Morton generation. A CPU PushSubData can recreate
        // a Vulkan buffer after the compute snapshot has captured its handle,
        // leaving the mandatory overflow descriptor null for this dispatch.
        program.Uniform("resetOverflow", 1u);
        program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        program.Uniform("resetOverflow", 0u);
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
    ///   <item><b>primitiveCount &gt; 1024:</b> four stable 8-bit radix passes.
    ///     Each pass performs block histograms, global bin/block prefix, and a
    ///     stable scatter. Duplicate Morton codes retain object-id order.</item>
    /// </list>
    /// </remarks>
    private void SortMortonCodes(uint primitiveCount)
    {
        if (primitiveCount <= 1 || _mortonBuffer is null)
            return;

        using var timing = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Sort, primitiveCount);

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

        var histogramProgram = _radixHistogramProgram;
        var prefixProgram = _radixPrefixProgram;
        var scatterProgram = _radixScatterProgram;
        if (histogramProgram is null || prefixProgram is null || scatterProgram is null ||
            _radixScratchBuffer is null || _radixOffsetsBuffer is null)
            return;

        uint blockCount = ComputeGroups(primitiveCount, 256u);
        for (uint shift = 0u; shift < 32u; shift += 8u)
        {
            XRDataBuffer source = (shift & 8u) == 0u ? _mortonBuffer : _radixScratchBuffer;
            XRDataBuffer destination = (shift & 8u) == 0u ? _radixScratchBuffer : _mortonBuffer;

            histogramProgram.BindBuffer(source, Bindings.Morton);
            histogramProgram.BindBuffer(_radixOffsetsBuffer, Bindings.RadixOffsets);
            histogramProgram.Uniform("numObjects", primitiveCount);
            histogramProgram.Uniform("radixShift", shift);
            histogramProgram.DispatchCompute(blockCount, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            prefixProgram.BindBuffer(_radixOffsetsBuffer, Bindings.RadixOffsets);
            prefixProgram.Uniform("blockCount", blockCount);
            prefixProgram.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            scatterProgram.BindBuffer(source, Bindings.Morton);
            scatterProgram.BindBuffer(destination, Bindings.RadixScratch);
            scatterProgram.BindBuffer(_radixOffsetsBuffer, Bindings.RadixOffsets);
            scatterProgram.Uniform("numObjects", primitiveCount);
            scatterProgram.Uniform("radixShift", shift);
            scatterProgram.DispatchCompute(blockCount, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }
    }

    private void DispatchBuild(uint primitiveCount)
    {
        if (_buildProgram is null || _nodeBuffer is null || _aabbBuffer is null)
            return;

        using var timing = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.HierarchyBuild, primitiveCount);
        uint leafCount = (primitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint internalCount = leafCount > 0 ? leafCount - 1u : 0u;
        if (leafCount == 0)
            return;

        var program = _buildProgram;
        program.BindBuffer(_aabbBuffer, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        if (_overflowFlagBuffer is not null)
            program.BindBuffer(_overflowFlagBuffer, Bindings.OverflowFlag);
        program.Uniform("numPrimitives", primitiveCount);
        program.Uniform("nodeScalarCapacity", _nodeBuffer.ElementCount);
        program.Uniform("mortonCapacity", GetMortonCapacity());

        // OpenGL-compatible replacement for Vulkan specialization constants.
        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);

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

    private void DispatchRefit()
    {
        if (_refitProgram is null || _nodeBuffer is null || _counterBuffer is null || _lastNodeCount == 0)
            return;

        var program = _refitProgram;
        program.BindBuffer(_aabbBuffer!, Bindings.Aabb);
        program.BindBuffer(_mortonBuffer!, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_counterBuffer, Bindings.Counters);
        // bvh_refit.comp does not declare an OverflowFlags binding; nothing to bind here.
        program.Uniform("debugValidation", 0u);

        program.Uniform("MAX_LEAF_PRIMITIVES", _maxLeafPrimitives);

        uint leafCount = (_lastPrimitiveCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
        uint internalCount = leafCount > 0u ? leafCount - 1u : 0u;
        program.Uniform("leafCount", leafCount);
        program.Uniform("internalCount", internalCount);

        // Stage 0: clear only counters consumed by internal nodes.
        program.Uniform("refitStage", 0u);
        if (internalCount > 0u)
        {
            using var clearTiming = BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.RefitClear, internalCount);
            program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        // Stage 1: refit leaves and propagate.
        program.Uniform("refitStage", 1u);
        using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.LeafRefit, leafCount))
            program.DispatchCompute(ComputeGroups(leafCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    /// <summary>
    /// Produces a GPU-resident quality snapshot. No CPU readback is issued;
    /// diagnostic overlays and capture tooling can consume the SSBO directly.
    /// </summary>
    private void DispatchQualityAnalysis(GpuBvhRebuildReason rebuildReason)
    {
        if (_qualityProgram is null || _qualityDiagnosticsBuffer is null ||
            _mortonBuffer is null || _nodeBuffer is null || _lastPrimitiveCount == 0u)
            return;

        XRRenderProgram program = _qualityProgram;
        program.BindBuffer(_mortonBuffer, Bindings.Morton);
        program.BindBuffer(_nodeBuffer, Bindings.Node);
        program.BindBuffer(_qualityDiagnosticsBuffer, Bindings.QualityDiagnostics);
        program.Uniform("primitiveCount", _lastPrimitiveCount);
        program.Uniform("analysisRevision", ++_qualityAnalysisRevision);
        program.Uniform("rebuildReason", (uint)rebuildReason);

        program.Uniform("analysisStage", 0u);
        program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        program.Uniform("analysisStage", 1u);
        program.DispatchCompute(ComputeGroups(Math.Max(_lastPrimitiveCount, _lastNodeCount), 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        program.Uniform("analysisStage", 2u);
        program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        _qualityAnalysisCount++;
    }

    private static uint ComputeGroups(uint count, uint localSize)
        => (count + localSize - 1u) / localSize;
}
