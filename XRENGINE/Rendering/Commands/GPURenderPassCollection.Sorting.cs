namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        //public void Sort(GPUScene gpuCommands)
        //{
        //    uint count = VisibleCommandCount; if (count <= 1) return;
        //    switch (SortAlgorithm)
        //    {
        //        default:
        //        case GPUSortAlgorithm.Bitonic: SortBitonic(count); break;
        //        case GPUSortAlgorithm.Radix:   SortRadix(count);   break;
        //        case GPUSortAlgorithm.Merge:   SortMerge(count);   break;
        //    }
        //}

        //private void SortBitonic(uint visibleCount)
        //{
        //    Dbg($"SortBitonic count={visibleCount}","Sorting");
        //    if (SortingComputeShader is null || visibleCount <= 1) return;
        //    SortingComputeShader.Uniform("SortByDistance", SortByDistance ? 1 : 0);
        //    SortingComputeShader.Uniform("SortDirection", (int)SortDirection);
        //    SortingComputeShader.Uniform("SortAlgorithm", 0);
        //    SortingComputeShader.Uniform("VisibleCount", (int)visibleCount);
        //    SortingComputeShader.BindBuffer(CulledSceneToRenderBuffer, 0);
        //    uint stages = (uint)Math.Ceiling(Math.Log2(visibleCount));
        //    uint groupSize = 256; uint numGroups = (visibleCount + groupSize - 1) / groupSize;
        //    for (uint stage = 0; stage < stages; stage++)
        //    {
        //        SortingComputeShader.Uniform("Stage", (int)stage);
        //        SortingComputeShader.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage);
        //    }
        //    Dbg("SortBitonic complete","Sorting");
        //}

        //private void SortRadix(uint visibleCount)
        //{
        //    Dbg($"SortRadix count={visibleCount}","Sorting");
        //    if (RadixSortComputeShader is null || visibleCount <= 1 || _sortedCommandBuffer is null || _histogramBuffer is null) return;
        //    RadixSortComputeShader.Uniform("SortByDistance", SortByDistance ? 1 : 0);
        //    RadixSortComputeShader.Uniform("SortDirection", (int)SortDirection);
        //    RadixSortComputeShader.Uniform("TotalCommands", (int)visibleCount);
        //    RadixSortComputeShader.BindBuffer(CulledSceneToRenderBuffer, 0);
        //    RadixSortComputeShader.BindBuffer(_sortedCommandBuffer, 1);
        //    RadixSortComputeShader.BindBuffer(_histogramBuffer, 2);
        //    uint groupSize = 256; uint numGroups = (visibleCount + groupSize - 1) / groupSize;
        //    for (int pass = 0; pass < 4; pass++)
        //    {
        //        _histogramBuffer.SetDataRaw(new uint[256]); _histogramBuffer.PushSubData();
        //        RadixSortComputeShader.Uniform("RadixPass", pass);
        //        RadixSortComputeShader.DispatchCompute(numGroups * 4, 1, 1, EMemoryBarrierMask.ShaderStorage);
        //    }
        //    Dbg("SortRadix complete","Sorting");
        //}

        //private void SortMerge(uint visibleCount)
        //{
        //    Dbg($"SortMerge count={visibleCount}","Sorting");
        //    if (SortingComputeShader is null || visibleCount <= 1 || _sortedCommandBuffer is null) return;
        //    SortingComputeShader.Uniform("SortByDistance", SortByDistance ? 1 : 0);
        //    SortingComputeShader.Uniform("SortDirection", (int)SortDirection);
        //    SortingComputeShader.Uniform("SortAlgorithm", 2);
        //    SortingComputeShader.Uniform("CurrentPass", RenderPass);
        //    SortingComputeShader.Uniform("VisibleCount", (int)visibleCount);
        //    SortingComputeShader.BindBuffer(CulledSceneToRenderBuffer, 0);
        //    uint groupSize = 256; uint numGroups = (visibleCount + groupSize - 1) / groupSize; uint mergePasses = (uint)Math.Ceiling(Math.Log2(visibleCount));
        //    for (uint mergePass = 0; mergePass < mergePasses; mergePass++)
        //    {
        //        SortingComputeShader.Uniform("MergePass", (int)mergePass);
        //        SortingComputeShader.DispatchCompute(numGroups, 1, 1, EMemoryBarrierMask.ShaderStorage);
        //    }
        //    Dbg("SortMerge complete","Sorting");
        //}

        //private void GPUIndexRadixSort(GPUScene scene)
        //{
        //    Dbg("GPUIndexRadixSort begin","Sorting");

        //    if (BuildKeysComputeShader is null || RadixIndexSortComputeShader is null || _culledCountBuffer is null) 
        //        return;

        //    uint count = VisibleCommandCount;
        //    if (count <= 1)
        //        return; EnsureSortBuffers(count);

        //    BuildKeysComputeShader.Uniform("SortByDistance", SortByDistance ? 1 : 0);
        //    BuildKeysComputeShader.Uniform("SortDirection", (int)SortDirection);
        //    BuildKeysComputeShader.Uniform("UseMaterialBatchKey", UseMaterialBatchKey ? 1 : 0);
        //    BuildKeysComputeShader.BindBuffer(CulledSceneToRenderBuffer, 0);
        //    BuildKeysComputeShader.BindBuffer(_culledCountBuffer, 1);
        //    BuildKeysComputeShader.BindBuffer(_keyIndexBufferA, 2);

        //    if (_materialIDsBuffer != null)
        //        BuildKeysComputeShader.BindBuffer(_materialIDsBuffer, 3);

        //    uint groupSize = 256;
        //    uint groups = (count + groupSize - 1) / groupSize;

        //    BuildKeysComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

        //    for (int pass = 0; pass < 4; pass++)
        //    {
        //        Dbg($"Radix pass {pass}","Sorting");

        //        // Reset histogram data each pass
        //        _histogramIndexBuffer.SetDataRaw(new uint[256]);
        //        _histogramIndexBuffer.PushSubData();

        //        // Phase 0: histogram build
        //        RadixIndexSortComputeShader.Uniform("RadixPass", pass);
        //        RadixIndexSortComputeShader.Uniform("SortDirection", (int)SortDirection);
        //        RadixIndexSortComputeShader.Uniform("Phase", 0);
        //        RadixIndexSortComputeShader.BindBuffer(_keyIndexBufferA, 0); // in
        //        RadixIndexSortComputeShader.BindBuffer(_keyIndexBufferB, 1); // out (unused phase 0)
        //        RadixIndexSortComputeShader.BindBuffer(_histogramIndexBuffer, 2); // histogram
        //        RadixIndexSortComputeShader.BindBuffer(_culledCountBuffer, 3); // count
        //        RadixIndexSortComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

        //        // Phase 1: prefix scan (single workgroup)
        //        RadixIndexSortComputeShader.Uniform("Phase", 1);
        //        RadixIndexSortComputeShader.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage);

        //        // Phase 2: scatter to alternate buffer
        //        RadixIndexSortComputeShader.Uniform("Phase", 2);
        //        RadixIndexSortComputeShader.BindBuffer(_keyIndexBufferA, 0);
        //        RadixIndexSortComputeShader.BindBuffer(_keyIndexBufferB, 1);
        //        RadixIndexSortComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);

        //        // Swap buffers for next pass (ping-pong)
        //        (_keyIndexBufferA, _keyIndexBufferB) = (_keyIndexBufferB, _keyIndexBufferA);
        //    }
        //    Dbg("GPUIndexRadixSort complete","Sorting");
        //}
    }
}
