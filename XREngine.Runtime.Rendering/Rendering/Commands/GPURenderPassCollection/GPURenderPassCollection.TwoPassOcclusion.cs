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
        private const string TwoPassCandidateCountCopyLabel = "GpuHiZ.TwoPass.CandidateCount";
        private const string TwoPassOutputCountCopyLabel = "GpuHiZ.TwoPass.OutputCount";
        private const string TwoPassPhaseOneCountCopyLabel = "GpuHiZ.TwoPass.PhaseOneCount";

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
                TwoPassPhaseOneCulledCommandViewMaskBuffer is null ||
                _twoPassPhaseOneMaterialTierIndirectDrawBuffer is null ||
                _twoPassPhaseOneMaterialTierDrawCountBuffer is null ||
                _twoPassVisibilityBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _culledCommandViewMaskBuffer is null ||
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

            long occlusionStart = Stopwatch.GetTimestamp();
            GpuHiZTemporalInvalidation temporalInvalidation = EvaluateGpuHiZTemporalInvalidation(scene, camera, depthInput);
            PrepareStableHiZDecisions(scene, temporalInvalidation);
            bool temporalInvalidated = temporalInvalidation.Invalidated;

            Crumb($"HiZ.TwoPass.Phase1.BEGIN pass={RenderPass} cand={candidates}");
            if (!CopyTwoPassCount(
                    _culledCountBuffer!,
                    _twoPassCandidateCountBuffer!,
                    TwoPassCandidateCountCopyLabel) ||
                !ClearTwoPassPhaseOutputs() ||
                !DispatchTwoPassPhaseOne(scene, temporalInvalidated))
            {
                Crumb($"HiZ.TwoPass.Phase1.FAILED pass={RenderPass}");
                return false;
            }

            bool phaseOneSubmitted;
            EnableTemporalMeshletHiZForSubmission(camera, temporalInvalidated);
            try
            {
                phaseOneSubmitted = PrepareAndSubmitPhaseOneVisibleSet(
                    scene,
                    camera,
                    EAdvancedVisibilitySynchronizationBoundary.PreparationToEarlyRaster);
            }
            finally
            {
                DisableMeshletHiZForSubmission();
            }

            if (!phaseOneSubmitted)
            {
                return false;
            }
            Crumb($"HiZ.TwoPass.Phase1.END pass={RenderPass}");

            AdvancedVisibilitySynchronizationContract.ApplyOpenGl(
                EAdvancedVisibilitySynchronizationBoundary.EarlyRasterToDepthPyramid);

            Crumb($"HiZ.TwoPass.BuildPyramid.BEGIN pass={RenderPass}");
            if (IsBoundedCoarseHiZEnabled())
            {
                if (!BuildHiZCoarseTilesForDiagnostics(
                        depthInput.Sampler,
                        depthInput.Width,
                        depthInput.Height,
                        camera.IsReversedDepth))
                {
                    return false;
                }
            }
            else
            {
                EnsureHiZDepthPyramid(depthInput.Width, depthInput.Height);
                if (_hiZDepthPyramid is null)
                    return false;

                BuildHiZPyramid(depthInput.Sampler, camera.IsReversedDepth);
                BuildHiZCoarseTilesForDiagnostics(
                    depthInput.Sampler,
                    depthInput.Width,
                    depthInput.Height,
                    camera.IsReversedDepth);
            }
            _hiZDepthPyramidReadyForMeshlets = TryGetActiveHiZOcclusionTexture(out _, out _);
            _hiZDepthPyramidViewProjection = depthInput.ViewProjection;
            _hiZDepthPyramidUsesReversedZ = camera.IsReversedDepth;
            PublishStableHiZHistories(scene);
            Crumb($"HiZ.TwoPass.BuildPyramid.END pass={RenderPass}");

            AdvancedVisibilitySynchronizationContract.ApplyOpenGl(
                EAdvancedVisibilitySynchronizationBoundary.DepthPyramidToLatePreparation);

            // The late list reuses the indirect, material, meshlet, and per-view
            // argument buffers after the early draw has consumed them. The full
            // GPU reset keeps this transition zero-readback and command ordered.
            if (!ResetCounters() ||
                !DispatchTwoPassPhaseTwo(
                    scene,
                    camera,
                    depthInput.ViewProjection,
                    temporalInvalidated))
            {
                Crumb($"HiZ.TwoPass.Phase2.FAILED pass={RenderPass}");
                return false;
            }

            Crumb($"HiZ.TwoPass.Phase2.BEGIN pass={RenderPass}");
            bool phaseTwoSubmitted;
            EnableCurrentMeshletHiZForSubmission(camera);
            try
            {
                phaseTwoSubmitted = PrepareAndSubmitVisibleSet(
                    scene,
                    camera,
                    "two-pass-phase2",
                    EAdvancedVisibilitySynchronizationBoundary.LatePreparationToLateRaster);
            }
            finally
            {
                DisableMeshletHiZForSubmission();
            }

            if (!phaseTwoSubmitted)
            {
                return false;
            }
            AdvancedVisibilitySynchronizationContract.ApplyOpenGl(
                EAdvancedVisibilitySynchronizationBoundary.LateRasterToConsumers);
            Crumb($"HiZ.TwoPass.Phase2.END pass={RenderPass}");

            // This only publishes references after both GPU draw streams have
            // been successfully submitted. It intentionally does not read them.
            TimeSpan occlusionElapsed = Stopwatch.GetElapsedTime(occlusionStart);
            StampCompletedGpuHiZTwoPassDiagnostic(scene, candidates, in temporalInvalidation, occlusionElapsed);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Occlusion,
                occlusionElapsed);
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
                TwoPassPhaseOneCulledCommandViewMaskBuffer is null ||
                _twoPassPhaseOneMaterialTierIndirectDrawBuffer is null ||
                _twoPassPhaseOneMaterialTierDrawCountBuffer is null)
            {
                return false;
            }

            XRDataBuffer? candidateCommands = _culledSceneToRenderBuffer;
            XRDataBuffer<GPUViewMask>? candidateViewMasks = _culledCommandViewMaskBuffer;
            XRDataBuffer? lateIndirectCommands = _materialTierIndirectDrawBuffer;
            XRDataBuffer? lateDrawCounts = _materialTierDrawCountBuffer;

            try
            {
                _culledSceneToRenderBuffer = _twoPassPhaseOneCommandBuffer;
                _culledCommandViewMaskBuffer = TwoPassPhaseOneCulledCommandViewMaskBuffer;
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
                _culledCommandViewMaskBuffer = candidateViewMasks;
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

        private bool DispatchTwoPassPhaseOne(GPUScene scene, bool forceVisible)
        {
            if (_hiZPhaseOneProgram is null ||
                _culledSceneToRenderBuffer is null ||
                _twoPassPhaseOneCommandBuffer is null ||
                _twoPassCandidateCountBuffer is null ||
                _cullCountScratchBuffer is null ||
                _culledCountBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _twoPassVisibilityBuffer is null ||
                _culledCommandViewMaskBuffer is null ||
                TwoPassPhaseOneCulledCommandViewMaskBuffer is null)
            {
                return false;
            }

            if (TwoPassPhaseOneCulledCommandViewMaskBuffer.ElementCount !=
                _twoPassPhaseOneCommandBuffer.ElementCount)
            {
                return false;
            }

            _hiZPhaseOneProgram.Use();
            _hiZPhaseOneProgram.Uniform("MaxOutputCommands", (int)_twoPassPhaseOneCommandBuffer.ElementCount);
            _hiZPhaseOneProgram.Uniform("ActiveViewCount", (int)_activeViewCount);
            _hiZPhaseOneProgram.Uniform("CurrentRenderPass", RenderPass);
            _hiZPhaseOneProgram.Uniform("ForceVisible", forceVisible ? 1u : 0u);
            _hiZPhaseOneProgram.Uniform(
                "ExactSourceViewMasksValid",
                HasCurrentFrameExactCommandViewMasks ? 1u : 0u);
            _hiZPhaseOneProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _hiZPhaseOneProgram.BindBuffer(_twoPassPhaseOneCommandBuffer, 1);
            TwoPassPhaseOneCulledCommandViewMaskBuffer.BindTo(
                _hiZPhaseOneProgram,
                GPUViewSetBindings.PhaseOneCulledCommandViewMaskBuffer);
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
            if (!CopyTwoPassCount(
                    _cullCountScratchBuffer,
                    _culledCountBuffer!,
                    TwoPassOutputCountCopyLabel) ||
                !CopyTwoPassCount(
                    _cullCountScratchBuffer,
                    _twoPassPhaseOneCountBuffer!,
                    TwoPassPhaseOneCountCopyLabel))
            {
                return false;
            }
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            return true;
        }

        private bool DispatchTwoPassPhaseTwo(
            GPUScene scene,
            XRCamera camera,
            in System.Numerics.Matrix4x4 viewProjection,
            bool forcePhaseOneVisible)
        {
            if (_hiZOcclusionProgram is null ||
                !TryGetActiveHiZOcclusionTexture(out XRTexture2D activeHiZ, out int activeHiZMaxMip) ||
                _occlusionCulledBuffer is null ||
                _culledSceneToRenderBuffer is null ||
                _twoPassCandidateCountBuffer is null ||
                _cullCountScratchBuffer is null ||
                _culledCountBuffer is null ||
                _occlusionOverflowFlagBuffer is null ||
                _twoPassVisibilityBuffer is null)
            {
                return false;
            }

            _hiZOcclusionProgram.Use();
            _hiZOcclusionProgram.Uniform("ViewProj", viewProjection);
            _hiZOcclusionProgram.Uniform("HiZMaxMip", activeHiZMaxMip);
            _hiZOcclusionProgram.Uniform("CoarseSourceSize", IsBoundedCoarseHiZEnabled() ? _hiZCoarseSourceSize : default);
            _hiZOcclusionProgram.Uniform("IsReversedDepth", camera.IsReversedDepth ? 1u : 0u);
            _hiZOcclusionProgram.Uniform("MaxOutputCommands", (int)_occlusionCulledBuffer.ElementCount);
            _hiZOcclusionProgram.Uniform("TwoPassPhase", 2);
            _hiZOcclusionProgram.Uniform("ActiveViewCount", (int)_activeViewCount);
            _hiZOcclusionProgram.Uniform("CurrentRenderPass", RenderPass);
            _hiZOcclusionProgram.Uniform("ForcePhaseOneVisible", forcePhaseOneVisible ? 1u : 0u);
            SetHiZOcclusionClipSpaceUniforms(_hiZOcclusionProgram);
            _hiZOcclusionProgram.Sampler("HiZDepth", activeHiZ, 0);
            _hiZOcclusionProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _hiZOcclusionProgram.BindBuffer(_occlusionCulledBuffer, 1);
            BindStorageBuffer(_hiZOcclusionProgram, _twoPassCandidateCountBuffer, 2);
            BindStorageBuffer(_hiZOcclusionProgram, _cullCountScratchBuffer, 3);
            _hiZOcclusionProgram.BindBuffer(_occlusionOverflowFlagBuffer, 4);
            scene.CullBoundsBuffer.BindTo(_hiZOcclusionProgram, 5);
            _twoPassVisibilityBuffer.BindTo(_hiZOcclusionProgram, 6);
            if (_statsBuffer is not null)
                _hiZOcclusionProgram.BindBuffer(_statsBuffer, 8);
            scene.CullControlBuffer.BindTo(_hiZOcclusionProgram, 10u);
            BindViewSetBuffers(_hiZOcclusionProgram);

            uint groups = Math.Max(1u, (_culledSceneToRenderBuffer.ElementCount + 255u) / 256u);
            long testRecordStart = Stopwatch.GetTimestamp();
            using (OcclusionGpuElapsedTiming.Instance.Begin(EOcclusionGpuElapsedStage.Test))
            {
                _hiZOcclusionProgram.DispatchCompute(
                    groups,
                    1,
                    1,
                    EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            }
            OcclusionTelemetry.RecordHiZTest(
                _culledSceneToRenderBuffer.ElementCount,
                Stopwatch.GetElapsedTime(testRecordStart).TotalMilliseconds);
            if (!CopyTwoPassCount(
                    _cullCountScratchBuffer,
                    _culledCountBuffer!,
                    TwoPassOutputCountCopyLabel))
            {
                return false;
            }
            SwapCulledBufferAfterOcclusion();
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            return true;
        }

        private static void SetHiZOcclusionClipSpaceUniforms(XRRenderProgram program)
        {
            program.Uniform(
                "ClipDepthRange",
                (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
            program.Uniform(
                "FramebufferTextureYDirection",
                (int)RenderClipSpacePolicy.FramebufferTextureYDirection(
                    RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend));
        }

        private bool CopyTwoPassCount(
            XRDataBuffer source,
            XRDataBuffer destination,
            string label)
        {
            const nuint byteCount = GPUScene.VisibleCountComponents * sizeof(uint);
            if (AbstractRenderer.Current is { } renderer)
            {
                ERendererComputeEnqueueStatus status = renderer.TryEnqueueGpuBufferCopy(
                    source,
                    0,
                    destination,
                    0,
                    byteCount,
                    label);
                if (status == ERendererComputeEnqueueStatus.Enqueued)
                {
                    renderer.MemoryBarrier(
                        EMemoryBarrierMask.BufferUpdate |
                        EMemoryBarrierMask.ShaderStorage |
                        EMemoryBarrierMask.Command);
                    return true;
                }

                if (status != ERendererComputeEnqueueStatus.Unsupported)
                    return false;
            }

            if (_copyCount3Program is null)
                return false;

            _copyCount3Program!.Use();
            BindStorageBuffer(_copyCount3Program, source, 0);
            BindStorageBuffer(_copyCount3Program, destination, 1);
            _copyCount3Program.DispatchCompute(
                1,
                1,
                1,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            return true;
        }
    }
}
