using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private sealed class HiZSharedState
        {
            public XRTexture2D? Pyramid;
            public int MaxMip;
            public ulong LastBuiltFrameId;
            public uint Width;
            public uint Height;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, HiZSharedState> _hiZSharedCache = new();

        private EOcclusionCullingMode _lastLoggedOcclusionMode = (EOcclusionCullingMode)(-1);
        private bool _loggedGpuHiZOcclusionScaffold;
        private bool _loggedCpuQueryAsyncScaffold;

        private static readonly AsyncOcclusionQueryManager s_cpuOcclusionQueryManager = new();
        private readonly List<(uint SourceCommandIndex, XRRenderQuery Query)> _cpuOcclusionPending = [];
        private readonly Dictionary<uint, bool> _cpuOcclusionLastResolved = new();
        private ulong _cpuOcclusionLastResolveFrameId;
        private uint _cpuOcclusionLastSceneCommandCount;
        private Vector3 _cpuOcclusionLastCameraPosition;
        private Matrix4x4 _cpuOcclusionLastProjection;
        private bool _cpuOcclusionHasCameraState;

        private readonly Dictionary<uint, TemporalOcclusionState> _temporalOcclusion = [];

        private struct TemporalOcclusionState
        {
            public int ConsecutiveOccludedFrames;
            public ulong LastTouchedFrame;
        }

        private const int TemporalOcclusionHysteresisFrames = 2;
        private const float TemporalCameraJumpDistance = 2.0f;
        private const float TemporalProjectionDeltaThreshold = 0.125f;
        private const int CpuOcclusionMaxQueriesPerFrame = 64;

        private uint _occlusionCandidatesTested;
        private uint _occlusionAccepted;
        private uint _occlusionFalsePositiveRecoveries;
        private uint _occlusionTemporalOverrides;

        public EOcclusionCullingMode ActiveOcclusionMode => ResolveActiveOcclusionMode();
        public uint OcclusionCandidatesTested => _occlusionCandidatesTested;
        public uint OcclusionAccepted => _occlusionAccepted;
        public uint OcclusionFalsePositiveRecoveries => _occlusionFalsePositiveRecoveries;
        public uint OcclusionTemporalOverrides => _occlusionTemporalOverrides;

        private void ResetOcclusionFrameStats()
        {
            _occlusionCandidatesTested = 0u;
            _occlusionAccepted = 0u;
            _occlusionFalsePositiveRecoveries = 0u;
            _occlusionTemporalOverrides = 0u;
        }

        private void RecordOcclusionFrameStats(
            uint candidatesTested,
            uint occludedAccepted,
            uint falsePositiveRecoveries,
            uint temporalOverrides)
        {
            _occlusionCandidatesTested = candidatesTested;
            _occlusionAccepted = occludedAccepted;
            _occlusionFalsePositiveRecoveries = falsePositiveRecoveries;
            _occlusionTemporalOverrides = temporalOverrides;
        }

        private EOcclusionCullingMode ResolveActiveOcclusionMode()
        {
            // Passthrough mode is a debug-only escape hatch; keep it behaviorally stable.
            if (ForcePassthroughCulling)
                return EOcclusionCullingMode.Disabled;

            return Engine.EffectiveSettings.GpuOcclusionCullingMode;
        }

        private void ApplyOcclusionCulling(GPUScene scene, XRCamera? camera)
        {
            EOcclusionCullingMode mode = ResolveActiveOcclusionMode();
            LogOcclusionModeActivation(mode);

            if (_lastLoggedOcclusionMode != mode)
                ResetTemporalOcclusionState();

            _lastLoggedOcclusionMode = mode;

            if (mode == EOcclusionCullingMode.Disabled)
                return;

            // Pass-awareness: keep shadow/depth contributors out of occlusion hiding to avoid missing required passes.
            if (Engine.Rendering.State.IsShadowPass)
            {
                RecordOcclusionFrameStats(0u, 0u, 0u, 0u);
                return;
            }

            // In shipping mode we often avoid CPU readback of VisibleCommandCount.
            // When readback is disabled, run occlusion using buffer capacity; the compute shader gates on GPU count.
            uint candidates = VisibleCommandCount;
            if (candidates == 0u && IndirectDebug.DisableCpuReadbackCount)
                candidates = CulledSceneToRenderBuffer?.ElementCount ?? 0u;

            if (candidates == 0u)
                return;

            switch (mode)
            {
                case EOcclusionCullingMode.GpuHiZ:
                    ApplyGpuHiZOcclusionScaffold(scene, camera, candidates);
                    break;

                case EOcclusionCullingMode.CpuQueryAsync:
                    ApplyCpuQueryAsyncOcclusionScaffold(scene, camera, candidates);
                    break;
            }
        }

        private void LogOcclusionModeActivation(EOcclusionCullingMode mode)
        {
            if (_lastLoggedOcclusionMode == mode)
                return;

            bool isFirstObservation = _lastLoggedOcclusionMode == (EOcclusionCullingMode)(-1);
            if (isFirstObservation && mode == EOcclusionCullingMode.Disabled)
                return;

            Log(LogCategory.Culling, LogLevel.Info, "Occlusion mode active: {0} (pass={1})", mode, RenderPass);
        }

        private void ApplyGpuHiZOcclusionScaffold(GPUScene scene, XRCamera? camera, uint candidates)
        {
            ApplyGpuHiZOcclusion(scene, camera, candidates);
        }

        private void ApplyGpuHiZOcclusion(GPUScene scene, XRCamera? camera, uint candidates)
        {
            if (camera is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            // Need: shaders + buffers + a depth texture from the active pipeline.
            if (_hiZInitProgram is null || _hiZGenProgram is null || _hiZOcclusionProgram is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                if (!_loggedGpuHiZOcclusionScaffold)
                {
                    _loggedGpuHiZOcclusionScaffold = true;
                    Log(LogCategory.Culling, LogLevel.Warning,
                        "Occlusion mode {0} missing shader programs for pass {1}; keeping {2} candidates visible.",
                        EOcclusionCullingMode.GpuHiZ,
                        RenderPass,
                        candidates);
                }
                return;
            }

            var pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            // DefaultRenderPipeline's current depth view texture name is stable across passes.
            // If the pipeline doesn't provide it, we can't build Hi-Z.
            const string DepthViewTextureName = DefaultRenderPipeline.DepthViewTextureName;
            if (!pipeline.TryGetTexture(DepthViewTextureName, out XRTexture? depthTex) || depthTex is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                if (!_loggedGpuHiZOcclusionScaffold)
                {
                    _loggedGpuHiZOcclusionScaffold = true;
                    Log(LogCategory.Culling, LogLevel.Warning,
                        "Occlusion mode {0} missing depth texture '{1}' for pass {2}; keeping {3} candidates visible.",
                        EOcclusionCullingMode.GpuHiZ,
                        DepthViewTextureName,
                        RenderPass,
                        candidates);
                }
                return;
            }

            if (depthTex is not XRTexture2D depth2D)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            if (_cullCountScratchBuffer is null || _culledCountBuffer is null || _occlusionCulledBuffer is null || _occlusionOverflowFlagBuffer is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            if (CulledSceneToRenderBuffer is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            bool isReverseZ = camera.IsReversedDepth;
            bool cacheOncePerFrame = Engine.Rendering.Settings.CacheGpuHiZOcclusionOncePerFrame;
            if (cacheOncePerFrame)
            {
                var shared = _hiZSharedCache.GetValue(pipeline, static _ => new HiZSharedState());
                EnsureSharedHiZDepthPyramid(shared, depth2D);
                _hiZDepthPyramid = shared.Pyramid;
                _hiZMaxMip = shared.MaxMip;

                if (_hiZDepthPyramid is null)
                {
                    RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                    return;
                }

                ulong frameId = Engine.Rendering.State.RenderFrameId;
                if (shared.LastBuiltFrameId != frameId)
                {
                    BuildHiZPyramid(depth2D, isReverseZ);
                    shared.LastBuiltFrameId = frameId;
                }
            }
            else
            {
                // Per-pass: ensure Hi-Z pyramid exists and matches depth size, then build each pass.
                EnsureHiZDepthPyramid(depth2D);
                if (_hiZDepthPyramid is null)
                {
                    RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                    return;
                }

                BuildHiZPyramid(depth2D, isReverseZ);
            }

            // Occlusion refinement: read candidates from CulledSceneToRenderBuffer and count from scratch.
            // Write refined visible commands into the ping-pong buffer and final counts into _culledCountBuffer.
            ApplyHiZOcclusionRefine(camera);

            // Swap in refined buffer for subsequent indirect build.
            SwapCulledBufferAfterOcclusion();

            // Stats: we conservatively report all candidates tested; accepted is the number removed.
            // Avoid CPU readbacks here; in shipping mode we may not have a CPU-visible count.
            uint occluded = 0u;
            if (!IndirectDebug.DisableCpuReadbackCount)
            {
                uint visibleAfter = VisibleCommandCount;
                occluded = candidates > visibleAfter ? (candidates - visibleAfter) : 0u;
            }
            RecordOcclusionFrameStats(candidates, occluded, 0u, 0u);
        }

        private void EnsureHiZDepthPyramid(XRTexture2D depthTexture)
        {
            uint width = Math.Max(1u, depthTexture.Width);
            uint height = Math.Max(1u, depthTexture.Height);

            int smallestMip = XRTexture.GetSmallestMipmapLevel(width, height);
            _hiZMaxMip = Math.Max(0, smallestMip);

            bool needsRecreate = _hiZDepthPyramid is null ||
                                 _hiZDepthPyramid.Mipmaps.Length < (_hiZMaxMip + 1) ||
                                 _hiZDepthPyramid.Mipmaps[0].Width != width ||
                                 _hiZDepthPyramid.Mipmaps[0].Height != height;

            if (!needsRecreate)
                return;

            // Only destroy the per-pass owned pyramid here.
            _hiZDepthPyramidOwned?.Destroy();
            _hiZDepthPyramidOwned = null;
            _hiZDepthPyramid = null;

            // Allocate an RGBA32F mip chain; only .r is used.
            // IMPORTANT: avoid allocating CPU-side pixel data for the mip chain.
            var mips = new Mipmap2D[_hiZMaxMip + 1];
            uint w = width;
            uint h = height;
            for (int i = 0; i < mips.Length; ++i)
            {
                mips[i] = new Mipmap2D(w, h, EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, allocateData: false);
                w = Math.Max(1u, w >> 1);
                h = Math.Max(1u, h >> 1);
            }

            _hiZDepthPyramidOwned = new XRTexture2D
            {
                Name = "HiZDepthPyramid",
                Mipmaps = mips,
                SizedInternalFormat = ESizedInternalFormat.Rgba32f,
                MinFilter = ETexMinFilter.NearestMipmapNearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                Resizable = false,
            };

            // Ensure GPU object is created.
            _hiZDepthPyramidOwned.PushData();
            _hiZDepthPyramid = _hiZDepthPyramidOwned;
        }

        private void EnsureSharedHiZDepthPyramid(HiZSharedState shared, XRTexture2D depthTexture)
        {
            uint width = Math.Max(1u, depthTexture.Width);
            uint height = Math.Max(1u, depthTexture.Height);

            int smallestMip = XRTexture.GetSmallestMipmapLevel(width, height);
            int maxMip = Math.Max(0, smallestMip);

            bool needsRecreate = shared.Pyramid is null ||
                                 shared.MaxMip != maxMip ||
                                 shared.Width != width ||
                                 shared.Height != height ||
                                 shared.Pyramid.Mipmaps.Length < (maxMip + 1);

            if (!needsRecreate)
                return;

            shared.Pyramid?.Destroy();
            shared.Pyramid = null;

            shared.Width = width;
            shared.Height = height;
            shared.MaxMip = maxMip;
            shared.LastBuiltFrameId = ulong.MaxValue;

            var mips = new Mipmap2D[maxMip + 1];
            uint w = width;
            uint h = height;
            for (int i = 0; i < mips.Length; ++i)
            {
                mips[i] = new Mipmap2D(w, h, EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, allocateData: false);
                w = Math.Max(1u, w >> 1);
                h = Math.Max(1u, h >> 1);
            }

            shared.Pyramid = new XRTexture2D
            {
                Name = "HiZDepthPyramid(shared)",
                Mipmaps = mips,
                SizedInternalFormat = ESizedInternalFormat.Rgba32f,
                MinFilter = ETexMinFilter.NearestMipmapNearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                Resizable = false,
            };

            shared.Pyramid.PushData();
        }

        private void BuildHiZPyramid(XRTexture2D depthTexture, bool isReverseZ)
        {
            if (_hiZDepthPyramid is null)
                return;

            // Mip 0 init.
            _hiZInitProgram!.Use();
            _hiZInitProgram.Uniform("mipLevelSize", new IVector2((int)_hiZDepthPyramid.Mipmaps[0].Width, (int)_hiZDepthPyramid.Mipmaps[0].Height));
            _hiZInitProgram.Sampler("depthTexture", depthTexture, 0);
            _hiZInitProgram.BindImageTexture(1u, _hiZDepthPyramid, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA32F);

            uint gx = (uint)Math.Max(1, ((int)_hiZDepthPyramid.Mipmaps[0].Width + 15) / 16);
            uint gy = (uint)Math.Max(1, ((int)_hiZDepthPyramid.Mipmaps[0].Height + 15) / 16);
            _hiZInitProgram.DispatchCompute(gx, gy, 1, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);

            // Generate remaining mips using reduction.
            // Normal Z: depth increases with distance -> Hi-Z should store MAX depth per region.
            // Reversed Z: depth decreases with distance -> Hi-Z should store MIN depth per region.
            uint useMinReduction = isReverseZ ? 1u : 0u;

            _hiZGenProgram!.Use();
            _hiZGenProgram.Sampler("depthTexture", _hiZDepthPyramid, 0);
            _hiZGenProgram.Uniform("UseMinReduction", useMinReduction);

            for (int dstMip = 1; dstMip <= _hiZMaxMip; ++dstMip)
            {
                var mip = _hiZDepthPyramid.Mipmaps[dstMip];
                _hiZGenProgram.Uniform("SrcMip", dstMip - 1);
                _hiZGenProgram.Uniform("mipLevelSize", new IVector2((int)mip.Width, (int)mip.Height));
                _hiZGenProgram.BindImageTexture(1u, _hiZDepthPyramid, dstMip, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA32F);

                uint mx = (uint)Math.Max(1, ((int)mip.Width + 15) / 16);
                uint my = (uint)Math.Max(1, ((int)mip.Height + 15) / 16);
                _hiZGenProgram.DispatchCompute(mx, my, 1, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            }
        }

        private void ApplyHiZOcclusionRefine(XRCamera camera)
        {
            if (_hiZDepthPyramid is null)
                return;

            if (_copyCount3Program is null)
                return;

            // Reset output counters (scratch output for occlusion stage)
            WriteUints(_cullCountScratchBuffer!, 0u, 0u, 0u);
            WriteUInt(_occlusionOverflowFlagBuffer!, 0u);

            // Prepare uniforms
            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            Matrix4x4 viewProj = camera.ProjectionMatrix * view;
            uint reversed = camera.IsReversedDepth ? 1u : 0u;

            _hiZOcclusionProgram!.Use();
            _hiZOcclusionProgram.Uniform("ViewProj", viewProj);
            _hiZOcclusionProgram.Uniform("HiZMaxMip", _hiZMaxMip);
            _hiZOcclusionProgram.Uniform("IsReversedDepth", reversed);
            _hiZOcclusionProgram.Uniform("MaxOutputCommands", (int)CulledSceneToRenderBuffer!.ElementCount);

            // Bind pyramid and buffers
            _hiZOcclusionProgram.Sampler("HiZDepth", _hiZDepthPyramid, 0);
            _hiZOcclusionProgram.BindBuffer(CulledSceneToRenderBuffer!, 0);
            _hiZOcclusionProgram.BindBuffer(_occlusionCulledBuffer!, 1);
            _hiZOcclusionProgram.BindBuffer(_culledCountBuffer!, 2);
            _hiZOcclusionProgram.BindBuffer(_cullCountScratchBuffer!, 3);
            _hiZOcclusionProgram.BindBuffer(_occlusionOverflowFlagBuffer!, 4);
            if (_statsBuffer is not null)
                _hiZOcclusionProgram.BindBuffer(_statsBuffer, 8);

            // Dispatch: conservatively for capacity when CPU readback is disabled.
            uint dispatchCount = IndirectDebug.DisableCpuReadbackCount
                ? CulledSceneToRenderBuffer!.ElementCount
                : VisibleCommandCount;

            uint groups = Math.Max(1u, (dispatchCount + 255u) / 256u);
            _hiZOcclusionProgram.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            // Forward occlusion output counts into the primary count buffer for indirect build.
            _copyCount3Program.Use();
            _copyCount3Program.BindBuffer(_cullCountScratchBuffer!, 0);
            _copyCount3Program.BindBuffer(_culledCountBuffer!, 1);
            _copyCount3Program.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            // Update VisibleCommandCount/InstanceCount from final count buffer in debug/readback mode.
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
        }

        private void SwapCulledBufferAfterOcclusion()
        {
            if (_occlusionCulledBuffer is null || _culledSceneToRenderBuffer is null)
                return;

            // After occlusion, the refined buffer becomes the active culled buffer.
            (_culledSceneToRenderBuffer, _occlusionCulledBuffer) = (_occlusionCulledBuffer, _culledSceneToRenderBuffer);
        }

        private void ApplyCpuQueryAsyncOcclusionScaffold(GPUScene scene, XRCamera? camera, uint candidates)
        {
            ApplyCpuQueryAsyncOcclusion(scene, camera, candidates);
        }

        private void ApplyCpuQueryAsyncOcclusion(GPUScene scene, XRCamera? camera, uint candidates)
        {
            if (camera is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            uint temporalOverrides = 0u;
            if (scene.TotalCommandCount != _cpuOcclusionLastSceneCommandCount)
            {
                _cpuOcclusionLastSceneCommandCount = scene.TotalCommandCount;
                temporalOverrides += (uint)_temporalOcclusion.Count;
                ResetTemporalOcclusionState();
            }

            if (HasSignificantCameraChange(camera))
            {
                temporalOverrides += (uint)_temporalOcclusion.Count;
                ResetTemporalOcclusionState();
            }

            ResolveCpuOcclusionQueryResults();
            SubmitCpuOcclusionQueryBatch(candidates);

            uint falsePositiveRecoveries = 0u;
            uint occluded = ApplyTemporalCpuOcclusionFilter(candidates, ref temporalOverrides, ref falsePositiveRecoveries);
            RecordOcclusionFrameStats(candidates, occluded, falsePositiveRecoveries, temporalOverrides);

            if (!_loggedCpuQueryAsyncScaffold)
            {
                _loggedCpuQueryAsyncScaffold = true;
                Log(LogCategory.Culling, LogLevel.Info,
                    "Occlusion mode {0} active for pass {1}: async query submission/resolution enabled with previous-frame-only policy.",
                    EOcclusionCullingMode.CpuQueryAsync,
                    RenderPass);
            }
        }

        private bool HasSignificantCameraChange(XRCamera camera)
        {
            Vector3 position = camera.Transform.WorldTranslation;
            Matrix4x4 projection = camera.ProjectionMatrix;

            if (!_cpuOcclusionHasCameraState)
            {
                _cpuOcclusionHasCameraState = true;
                _cpuOcclusionLastCameraPosition = position;
                _cpuOcclusionLastProjection = projection;
                return false;
            }

            bool movedFar = Vector3.DistanceSquared(_cpuOcclusionLastCameraPosition, position) > (TemporalCameraJumpDistance * TemporalCameraJumpDistance);

            float projDelta =
                MathF.Abs(_cpuOcclusionLastProjection.M11 - projection.M11) +
                MathF.Abs(_cpuOcclusionLastProjection.M22 - projection.M22);
            bool projectionChanged = projDelta > TemporalProjectionDeltaThreshold;

            _cpuOcclusionLastCameraPosition = position;
            _cpuOcclusionLastProjection = projection;
            return movedFar || projectionChanged;
        }

        private void ResetTemporalOcclusionState()
        {
            _temporalOcclusion.Clear();
            _cpuOcclusionLastResolved.Clear();
        }

        private void SubmitCpuOcclusionQueryBatch(uint candidates)
        {
            if (CulledSceneToRenderBuffer is null)
                return;

            uint inputCount = ReadUIntAt(_culledCountBuffer!, GPUScene.VisibleCountDrawIndex);
            if (inputCount == 0u)
                return;

            uint submitCount = Math.Min(Math.Min(candidates, inputCount), CpuOcclusionMaxQueriesPerFrame);
            for (uint i = 0; i < submitCount; ++i)
            {
                var cmd = CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                uint sourceIndex = cmd.Reserved1;

                bool alreadyPending = false;
                for (int p = 0; p < _cpuOcclusionPending.Count; ++p)
                {
                    if (_cpuOcclusionPending[p].SourceCommandIndex == sourceIndex)
                    {
                        alreadyPending = true;
                        break;
                    }
                }

                if (alreadyPending)
                    continue;

                XRRenderQuery query = s_cpuOcclusionQueryManager.Acquire(EQueryTarget.AnySamplesPassedConservative);
                _cpuOcclusionPending.Add((sourceIndex, query));
            }
        }

        private uint ApplyTemporalCpuOcclusionFilter(uint candidates, ref uint temporalOverrides, ref uint falsePositiveRecoveries)
        {
            if (CulledSceneToRenderBuffer is null || _culledCountBuffer is null)
                return 0u;

            uint inputCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            if (inputCount == 0u)
                return 0u;

            ulong frameId = Engine.Rendering.State.RenderFrameId;
            uint writeIndex = 0u;
            uint visibleInstances = 0u;
            uint occludedAccepted = 0u;

            for (uint i = 0; i < inputCount; ++i)
            {
                var cmd = CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                uint sourceIndex = cmd.Reserved1;

                bool resolved = _cpuOcclusionLastResolved.TryGetValue(sourceIndex, out bool anySamplesPassed);
                bool keepVisible = true;

                if (resolved)
                {
                    ref TemporalOcclusionState state = ref CollectionsMarshal.GetValueRefOrAddDefault(_temporalOcclusion, sourceIndex, out bool exists);
                    if (!exists)
                        state = default;

                    state.LastTouchedFrame = frameId;

                    if (anySamplesPassed)
                    {
                        if (state.ConsecutiveOccludedFrames > 0)
                        {
                            temporalOverrides++;
                            falsePositiveRecoveries++;
                        }
                        state.ConsecutiveOccludedFrames = 0;
                    }
                    else
                    {
                        state.ConsecutiveOccludedFrames++;
                        if (state.ConsecutiveOccludedFrames >= TemporalOcclusionHysteresisFrames)
                        {
                            keepVisible = false;
                            occludedAccepted++;
                        }
                        else
                        {
                            temporalOverrides++;
                        }
                    }
                }

                if (!keepVisible)
                    continue;

                if (writeIndex != i)
                    CulledSceneToRenderBuffer.SetDataRawAtIndex(writeIndex, cmd);
                writeIndex++;
                visibleInstances += cmd.InstanceCount;
            }

            if (writeIndex != inputCount)
            {
                uint byteCount = writeIndex * CulledSceneToRenderBuffer.ElementSize;
                CulledSceneToRenderBuffer.PushSubData(0, byteCount);
            }

            WriteUints(_culledCountBuffer, writeIndex, visibleInstances, 0u);
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            return occludedAccepted;
        }

        private void ResolveCpuOcclusionQueryResults()
        {
            ulong frameId = Engine.Rendering.State.RenderFrameId;
            if (_cpuOcclusionLastResolveFrameId == frameId)
                return;

            _cpuOcclusionLastResolveFrameId = frameId;

            for (int i = _cpuOcclusionPending.Count - 1; i >= 0; --i)
            {
                (uint sourceIndex, XRRenderQuery query) = _cpuOcclusionPending[i];
                if (!s_cpuOcclusionQueryManager.TryGetAnySamplesPassed(query, out bool anySamplesPassed))
                    continue;

                _cpuOcclusionLastResolved[sourceIndex] = anySamplesPassed;
                s_cpuOcclusionQueryManager.Release(query);
                _cpuOcclusionPending.RemoveAt(i);
            }
        }
    }
}
