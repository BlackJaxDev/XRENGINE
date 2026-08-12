using System;
using System.Diagnostics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Occlusion;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// Executes persistent early/late visibility for current-depth GPU Hi-Z.
    /// </summary>
    public sealed partial class GPURenderPassCollection
    {
        private bool TryPrepareGpuHiZTwoPass(
            GPUScene scene,
            XRCamera camera,
            out GpuHiZDepthInput depthInput)
        {
            depthInput = default;
            if (scene.TotalCommandCount == 0u ||
                ResolveActiveOcclusionMode() != EOcclusionCullingMode.GpuHiZ ||
                MeshSubmissionStrategy != EMeshSubmissionStrategy.GpuIndirectZeroReadback)
            {
                return false;
            }

            if (ShouldUseExternalVrSharedVisibilityPassFilter(camera) ||
                _ownerPipeline?.IsShadowPipeline == true ||
                ((_ownerPipeline as XRRenderPipelineInstance)?.RenderState.UseDepthNormalMaterialVariants ?? false))
            {
                return false;
            }

            if (_hiZInitProgram is null ||
                _hiZGenProgram is null ||
                _hiZPhaseOneProgram is null ||
                _hiZOcclusionProgram is null ||
                _copyCount3Program is null ||
                _clearUIntsComputeShader is null ||
                _culledSceneToRenderBuffer is null ||
                _occlusionCulledBuffer is null ||
                _culledCountBuffer is null ||
                _cullCountScratchBuffer is null ||
                _twoPassCandidateCountBuffer is null ||
                _twoPassPhaseOneCountBuffer is null ||
                _twoPassPhaseOneCommandBuffer is null ||
                _twoPassPhaseOneMaterialTierIndirectDrawBuffer is null ||
                _twoPassPhaseOneMaterialTierDrawCountBuffer is null ||
                _twoPassVisibilityBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _perViewDrawCountBuffer is null)
            {
                return false;
            }

            if (_ownerPipeline is not XRRenderPipelineInstance pipeline ||
                !TryResolveGpuHiZDepthInput(pipeline, camera, out depthInput, out _) ||
                depthInput.History ||
                depthInput.Width == 0u ||
                depthInput.Height == 0u ||
                ShouldBypassCurrentDepthGpuHiZRefine(depthInput))
            {
                depthInput = default;
                return false;
            }

            return true;
        }

        private bool ExecuteGpuHiZTwoPass(
            GPUScene scene,
            XRCamera camera,
            in GpuHiZDepthInput depthInput)
        {
            uint candidates = _visibleCommandUpperBoundValid
                ? Math.Min(_visibleCommandUpperBound, CommandCapacity)
                : Math.Min(scene.TotalCommandCount, CommandCapacity);
            if (candidates == 0u)
                return PrepareAndSubmitVisibleSet(scene, camera, "two-pass-empty");

            Stopwatch occlusionStopwatch = Stopwatch.StartNew();
            PrepareStableHiZDecisions(scene);
            bool temporalInvalidated = ShouldInvalidateGpuHiZTemporalState(scene, camera);

            Crumb($"HiZ.TwoPass.Phase1.BEGIN pass={RenderPass} cand={candidates}");
            if (!SnapshotTwoPassCandidateCount() ||
                !ClearTwoPassPhaseOutputs() ||
                !DispatchTwoPassPhaseOne(scene))
            {
                Crumb($"HiZ.TwoPass.Phase1.FAILED pass={RenderPass}");
                return false;
            }

            if (!PrepareAndSubmitPhaseOneVisibleSet(
                    scene,
                    camera,
                    EAdvancedVisibilitySynchronizationBoundary.PreparationToEarlyRaster))
            {
                return false;
            }
            Crumb($"HiZ.TwoPass.Phase1.END pass={RenderPass}");

            AdvancedVisibilitySynchronizationContract.ApplyOpenGl(
                EAdvancedVisibilitySynchronizationBoundary.EarlyRasterToDepthPyramid);

            Crumb($"HiZ.TwoPass.BuildPyramid.BEGIN pass={RenderPass}");
            EnsureHiZDepthPyramid(depthInput.Width, depthInput.Height);
            if (_hiZDepthPyramid is null)
                return false;

            BuildHiZPyramid(depthInput.Sampler, camera.IsReversedDepth);
            _hiZDepthPyramidReadyForMeshlets = true;
            _hiZDepthPyramidViewProjection = depthInput.ViewProjection;
            _hiZDepthPyramidUsesReversedZ = camera.IsReversedDepth;
            PublishStableHiZHistories(scene);
            Crumb($"HiZ.TwoPass.BuildPyramid.END pass={RenderPass}");

            AdvancedVisibilitySynchronizationContract.ApplyOpenGl(
                EAdvancedVisibilitySynchronizationBoundary.DepthPyramidToLatePreparation);

            // The late list reuses the indirect, material, meshlet, and per-view
            // argument buffers after the early draw has consumed them. The full
            // GPU reset keeps this transition zero-readback and command ordered.
            ResetCounters();
            if (!ClearTwoPassPhaseOutputs() || !DispatchTwoPassPhaseTwo(scene, camera, depthInput.ViewProjection))
            {
                Crumb($"HiZ.TwoPass.Phase2.FAILED pass={RenderPass}");
                return false;
            }

            Crumb($"HiZ.TwoPass.Phase2.BEGIN pass={RenderPass}");
            if (!PrepareAndSubmitVisibleSet(
                    scene,
                    camera,
                    "two-pass-phase2",
                    EAdvancedVisibilitySynchronizationBoundary.LatePreparationToLateRaster))
            {
                return false;
            }
            AdvancedVisibilitySynchronizationContract.ApplyOpenGl(
                EAdvancedVisibilitySynchronizationBoundary.LateRasterToConsumers);
            Crumb($"HiZ.TwoPass.Phase2.END pass={RenderPass}");

            occlusionStopwatch.Stop();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Occlusion,
                occlusionStopwatch.Elapsed);
            RuntimeEngine.Rendering.Stats.RecordGpuDrivenHiZMode("two-phase-current-depth");
            // Exact early/late counts remain on the GPU by design. The early value
            // is a conservative candidate upper bound; zero denotes an intentionally
            // unread late count rather than a CPU synchronization point.
            RuntimeEngine.Rendering.Stats.RecordGpuDrivenHiZPhase(
                twoPhase: true,
                phaseOneDraws: candidates,
                phaseTwoDraws: 0L);
            RecordOcclusionFrameStats(candidates, 0u, 0u, temporalInvalidated ? 1u : 0u);
            OcclusionTelemetry.RecordGpuDepthSource(history: false);
            OcclusionTelemetry.RecordGpuPass((int)candidates, 0, readbackAvailable: false);
            OcclusionTelemetry.RecordActiveMode(EOcclusionCullingMode.GpuHiZ, MeshSubmissionStrategy);
            QueueTwoPassDiagnosticCountReadbacks();
            return true;
        }

        private bool PrepareAndSubmitPhaseOneVisibleSet(
            GPUScene scene,
            XRCamera camera,
            EAdvancedVisibilitySynchronizationBoundary synchronizationBoundary)
        {
            if (_twoPassPhaseOneCommandBuffer is null ||
                _twoPassPhaseOneMaterialTierIndirectDrawBuffer is null ||
                _twoPassPhaseOneMaterialTierDrawCountBuffer is null)
            {
                return false;
            }

            XRDataBuffer? candidateCommands = _culledSceneToRenderBuffer;
            XRDataBuffer? lateIndirectCommands = _materialTierIndirectDrawBuffer;
            XRDataBuffer? lateDrawCounts = _materialTierDrawCountBuffer;

            try
            {
                _culledSceneToRenderBuffer = _twoPassPhaseOneCommandBuffer;
                _materialTierIndirectDrawBuffer = _twoPassPhaseOneMaterialTierIndirectDrawBuffer;
                _materialTierDrawCountBuffer = _twoPassPhaseOneMaterialTierDrawCountBuffer;

                return PrepareAndSubmitVisibleSet(
                    scene,
                    camera,
                    "two-pass-phase1",
                    synchronizationBoundary);
            }
            finally
            {
                _culledSceneToRenderBuffer = candidateCommands;
                _materialTierIndirectDrawBuffer = lateIndirectCommands;
                _materialTierDrawCountBuffer = lateDrawCounts;
            }
        }

        /// <summary>
        /// Publishes the prior completed frame's phase counters only when the
        /// explicit Vulkan indirect trace is enabled. The production path keeps
        /// these snapshots GPU-resident and performs no CPU readback.
        /// </summary>
        private void QueueTwoPassDiagnosticCountReadbacks()
        {
            if (!VulkanDelayedCounterDiagnosticsEnabled || AbstractRenderer.Current is not { } renderer)
                return;

            renderer.QueueGpuRenderDrawCountReadback(
                _twoPassCandidateCountBuffer!,
                countElementCount: GPUScene.VisibleCountComponents);
            renderer.QueueGpuRenderDrawCountReadback(
                _twoPassPhaseOneCountBuffer!,
                countElementCount: GPUScene.VisibleCountComponents);
            renderer.QueueGpuRenderDrawCountReadback(
                _twoPassPhaseOneMaterialTierDrawCountBuffer!,
                countElementCount: Math.Min(
                    _materialTierBucketCount,
                    _twoPassPhaseOneMaterialTierDrawCountBuffer!.ElementCount));
            renderer.QueueGpuRenderDrawCountReadback(
                _cullCountScratchBuffer!,
                countElementCount: GPUScene.VisibleCountComponents);
        }

        private bool SnapshotTwoPassCandidateCount()
        {
            if (_copyCount3Program is null ||
                _culledCountBuffer is null ||
                _twoPassCandidateCountBuffer is null)
            {
                return false;
            }

            _copyCount3Program.Use();
            BindStorageBuffer(_copyCount3Program, _culledCountBuffer, 0);
            BindStorageBuffer(_copyCount3Program, _twoPassCandidateCountBuffer, 1);
            _copyCount3Program.DispatchCompute(
                1,
                1,
                1,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            return true;
        }

        private bool ClearTwoPassPhaseOutputs()
        {
            if (_cullCountScratchBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _perViewDrawCountBuffer is null)
            {
                return false;
            }

            return ClearUIntBufferOnGpu(
                       _cullCountScratchBuffer,
                       GPUScene.VisibleCountComponents,
                       EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command) &&
                   ClearUIntBufferOnGpu(
                       _occlusionOverflowFlagBuffer,
                       1u,
                       EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command) &&
                   ClearUIntBufferOnGpu(
                       _perViewDrawCountBuffer,
                       Math.Max(_activeViewCount, 1u),
                       EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        }

        private bool DispatchTwoPassPhaseOne(GPUScene scene)
        {
            if (_hiZPhaseOneProgram is null ||
                _culledSceneToRenderBuffer is null ||
                _twoPassPhaseOneCommandBuffer is null ||
                _twoPassCandidateCountBuffer is null ||
                _cullCountScratchBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _twoPassVisibilityBuffer is null)
            {
                return false;
            }

            _hiZPhaseOneProgram.Use();
            _hiZPhaseOneProgram.Uniform("MaxOutputCommands", (int)_culledSceneToRenderBuffer.ElementCount);
            _hiZPhaseOneProgram.Uniform("ActiveViewCount", (int)_activeViewCount);
            _hiZPhaseOneProgram.Uniform("CurrentRenderPass", RenderPass);
            _hiZPhaseOneProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _hiZPhaseOneProgram.BindBuffer(_twoPassPhaseOneCommandBuffer, 1);
            BindStorageBuffer(_hiZPhaseOneProgram, _twoPassCandidateCountBuffer, 2);
            BindStorageBuffer(_hiZPhaseOneProgram, _cullCountScratchBuffer, 3);
            _hiZPhaseOneProgram.BindBuffer(_occlusionOverflowFlagBuffer, 4);
            scene.CullControlBuffer.BindTo(_hiZPhaseOneProgram, 5);
            _twoPassVisibilityBuffer.BindTo(_hiZPhaseOneProgram, 6);
            scene.CullBoundsBuffer.BindTo(_hiZPhaseOneProgram, 7);
            BindViewSetBuffers(_hiZPhaseOneProgram);

            uint groups = Math.Max(1u, (_culledSceneToRenderBuffer.ElementCount + 255u) / 256u);
            _hiZPhaseOneProgram.DispatchCompute(
                groups,
                1,
                1,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            CopyTwoPassOutputCountToPrimary();
            CopyTwoPassCount(_cullCountScratchBuffer, _twoPassPhaseOneCountBuffer!);
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            return true;
        }

        private bool DispatchTwoPassPhaseTwo(
            GPUScene scene,
            XRCamera camera,
            in System.Numerics.Matrix4x4 viewProjection)
        {
            if (_hiZOcclusionProgram is null ||
                _hiZDepthPyramid is null ||
                _occlusionCulledBuffer is null ||
                _culledSceneToRenderBuffer is null ||
                _twoPassCandidateCountBuffer is null ||
                _cullCountScratchBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _twoPassVisibilityBuffer is null)
            {
                return false;
            }

            _hiZOcclusionProgram.Use();
            _hiZOcclusionProgram.Uniform("ViewProj", viewProjection);
            _hiZOcclusionProgram.Uniform("HiZMaxMip", _hiZMaxMip);
            _hiZOcclusionProgram.Uniform("IsReversedDepth", camera.IsReversedDepth ? 1u : 0u);
            _hiZOcclusionProgram.Uniform("MaxOutputCommands", (int)_culledSceneToRenderBuffer.ElementCount);
            _hiZOcclusionProgram.Uniform("TwoPassPhase", 2);
            _hiZOcclusionProgram.Uniform("ActiveViewCount", (int)_activeViewCount);
            _hiZOcclusionProgram.Uniform("CurrentRenderPass", RenderPass);
            _hiZOcclusionProgram.Sampler("HiZDepth", _hiZDepthPyramid, 0);
            _hiZOcclusionProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _hiZOcclusionProgram.BindBuffer(_occlusionCulledBuffer, 1);
            BindStorageBuffer(_hiZOcclusionProgram, _twoPassCandidateCountBuffer, 2);
            BindStorageBuffer(_hiZOcclusionProgram, _cullCountScratchBuffer, 3);
            _hiZOcclusionProgram.BindBuffer(_occlusionOverflowFlagBuffer, 4);
            scene.CullBoundsBuffer.BindTo(_hiZOcclusionProgram, 5);
            _twoPassVisibilityBuffer.BindTo(_hiZOcclusionProgram, 6);
            if (_statsBuffer is not null)
                _hiZOcclusionProgram.BindBuffer(_statsBuffer, 8);
            BindViewSetBuffers(_hiZOcclusionProgram);

            uint groups = Math.Max(1u, (_culledSceneToRenderBuffer.ElementCount + 255u) / 256u);
            _hiZOcclusionProgram.DispatchCompute(
                groups,
                1,
                1,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            CopyTwoPassOutputCountToPrimary();
            SwapCulledBufferAfterOcclusion();
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            return true;
        }

        private void CopyTwoPassOutputCountToPrimary()
            => CopyTwoPassCount(_cullCountScratchBuffer!, _culledCountBuffer!);

        private void CopyTwoPassCount(XRDataBuffer source, XRDataBuffer destination)
        {
            _copyCount3Program!.Use();
            BindStorageBuffer(_copyCount3Program, source, 0);
            BindStorageBuffer(_copyCount3Program, destination, 1);
            _copyCount3Program.DispatchCompute(
                1,
                1,
                1,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        }
    }
}
