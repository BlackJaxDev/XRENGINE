using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Rendering;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Stages a complete probe refresh before replacing the last usable publication.
    /// Allocations here occur only on structural/capture-version changes, not sampling.
    /// </summary>
    private void BuildProbeResources(
        ForwardLightProbeInstanceResources state,
        IList<LightProbeComponent> readyProbes,
        IReadOnlyList<int> sourceIndices,
        object worldIdentity,
        bool deferredByBatchCapture = false)
    {
        if (readyProbes.Count == 0)
        {
            state.ClearResources();
            return;
        }

        if (!RuntimeEngine.IsRenderThread ||
            AbstractRenderer.Current is not { } renderer ||
            !ReferenceEquals(CurrentRenderingPipeline, state.Owner))
        {
            DeferProbeResourceRefresh(state);
            return;
        }

        int resourceGeneration = state.Owner.ResourceGeneration;
        IRenderApiWrapperOwner apiWrapperIdentityOwner = renderer.ApiWrapperIdentityOwner;
        LightProbeComponent[] probeSnapshot = [.. readyProbes];
        int[] sourceIndexSnapshot = [.. sourceIndices];
        ulong bindingFrameId = state.BindingStateFrameId;
        int observedBatchVersion = state.ObservedLightProbeBatchCompletedVersion;
        Stopwatch stopwatch = Stopwatch.StartNew();
        var retained = new List<LightProbeIblOutputGeneration>(probeSnapshot.Length);
        var irradianceSources = new XRTexture2D[probeSnapshot.Length];
        var prefilterSources = new XRTexture2D[probeSnapshot.Length];
        var positions = new ProbePositionData[probeSnapshot.Length];
        var parameters = new ProbeParamData[probeSnapshot.Length];
        var probeIds = new Guid[probeSnapshot.Length];
        XRTexture2DArray? irradiance = null;
        XRTexture2DArray? prefilter = null;
        XRDataBuffer? positionBuffer = null;
        XRDataBuffer? parameterBuffer = null;
        ProbeGridResourceCandidate? grid = null;
        bool committed = false;
        try
        {
            for (int index = 0; index < probeSnapshot.Length; index++)
            {
                LightProbeComponent probe = probeSnapshot[index];
                if (!probe.TryGetActiveIblOutput(out LightProbeIblOutputGeneration generation) ||
                    !generation.TryRetainPublication())
                {
                    DeferProbeResourceRefresh(state);
                    Debug.RenderingWarningEvery(
                        "Advanced.ProbeArrayRefreshRejected",
                        TimeSpan.FromSeconds(2),
                        "[Advanced] Probe array refresh deferred; an output generation became unavailable before retention. Keeping the previous publication.");
                    return;
                }

                retained.Add(generation);
                irradianceSources[index] = generation.Irradiance;
                prefilterSources[index] = generation.PrefilteredRadiance;
                probeIds[index] = probe.ID;
                positions[index] = new ProbePositionData
                {
                    Position = new Vector4(probe.Transform.RenderTranslation, 1.0f),
                };
                parameters[index] = CreateProbeParamData(probe);
            }

            irradiance = new XRTexture2DArray(irradianceSources)
            {
                Name = LightProbeIrradianceArrayName,
                CopyGpuLayerSources = true,
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
            };
            prefilter = new XRTexture2DArray(prefilterSources)
            {
                Name = LightProbePrefilterArrayName,
                CopyGpuLayerSources = true,
                MinFilter = ETexMinFilter.LinearMipmapLinear,
                MagFilter = ETexMagFilter.Linear,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
            };
            PushProbeTextureArray(renderer, irradiance);
            PushProbeTextureArray(renderer, prefilter);

            positionBuffer = new XRDataBuffer(
                LightProbePositionBufferName,
                EBufferTarget.ShaderStorageBuffer,
                (uint)positions.Length,
                EComponentType.Struct,
                (uint)Marshal.SizeOf<ProbePositionData>(),
                false,
                false)
            {
                BindingIndexOverride = 0,
            };
            positionBuffer.SetDataRaw(positions);
            positionBuffer.PushData();

            parameterBuffer = new XRDataBuffer(
                LightProbeParamBufferName,
                EBufferTarget.ShaderStorageBuffer,
                (uint)parameters.Length,
                EComponentType.Struct,
                (uint)Marshal.SizeOf<ProbeParamData>(),
                false,
                false)
            {
                BindingIndexOverride = 2,
            };
            parameterBuffer.SetDataRaw(parameters);
            parameterBuffer.PushData();

            if (_useProbeGridAcceleration)
            {
                grid = ForwardLightProbeGridBuilder.Build(
                    positions,
                    parameters,
                    null,
                    LightProbeGridCellBufferName,
                    LightProbeGridIndexBufferName);
            }

            if (!RuntimeEngine.IsRenderThread ||
                !ReferenceEquals(CurrentRenderingPipeline, state.Owner) ||
                !ReferenceEquals(AbstractRenderer.Current, renderer) ||
                !ReferenceEquals(renderer.ApiWrapperIdentityOwner, apiWrapperIdentityOwner) ||
                !ReferenceEquals(RenderingWorld, worldIdentity) ||
                state.Owner.ResourceGeneration != resourceGeneration)
            {
                DeferProbeResourceRefresh(state);
                return;
            }

            ulong layoutSignature = ForwardLightProbeInstanceResources.ComputeLayoutSignature(
                probeIds,
                sourceIndexSnapshot,
                positions,
                parameters);

            // Every candidate GPU object is ready before the old cohort is retired.
            state.ClearResources();
            state.IrradianceArray = irradiance;
            state.PrefilterArray = prefilter;
            state.PositionBuffer = positionBuffer;
            state.ParamBuffer = parameterBuffer;
            irradiance = null;
            prefilter = null;
            positionBuffer = null;
            parameterBuffer = null;
            if (grid is not null)
            {
                grid.RelinquishBuffers(out state.GridCellBuffer, out state.GridIndexBuffer);
                state.GridOrigin = grid.Origin;
                state.GridCellSize = grid.CellSize;
                state.GridDimensions = grid.Dimensions;
            }

            state.CachedReadyProbes.AddRange(probeSnapshot);
            state.CachedReadyProbeSourceIndices.AddRange(sourceIndexSnapshot);
            for (int index = 0; index < probeSnapshot.Length; index++)
            {
                LightProbeComponent probe = probeSnapshot[index];
                Vector4 position = positions[index].Position;
                state.CachedProbePositions[probe.ID] = new Vector3(position.X, position.Y, position.Z);
                state.CachedProbeTextures[probe.ID] = (irradianceSources[index], prefilterSources[index]);
                state.ObservedProbeCaptureVersions[probe.ID] = retained[index].Generation;
            }

            state.CachedProbePositionData = positions;
            state.CachedProbeParamData = parameters;
            state.CachedProbeIds = probeIds;
            state.CachedProbeSourceIndices = sourceIndexSnapshot;
            state.CachedProbeLayoutSignature = layoutSignature;
            state.ApiWrapperIdentityOwner = apiWrapperIdentityOwner;
            state.WorldIdentity = worldIdentity;
            state.PublicationResourceGeneration = resourceGeneration;
            state.LastProbeCount = positions.Length;
            state.PendingProbeRefresh = false;
            state.PendingProbeRefreshDeferredByBatchCapture = false;
            state.ObservedLightProbeBatchCompletedVersion = observedBatchVersion;
            state.BindingStateFrameId = bindingFrameId;
            committed = true;

            state.Owner.BindImportedTexture(state.IrradianceArray!);
            state.Owner.BindImportedTexture(state.PrefilterArray!);
            state.Owner.BindImportedBuffer(state.PositionBuffer!);
            state.Owner.BindImportedBuffer(state.ParamBuffer!);
            if (state.GridCellBuffer is not null)
                state.Owner.BindImportedBuffer(state.GridCellBuffer);
            if (state.GridIndexBuffer is not null)
                state.Owner.BindImportedBuffer(state.GridIndexBuffer);

            StartTetrahedralizationJob(state, apiWrapperIdentityOwner, worldIdentity);
            stopwatch.Stop();
            ReportProbeResourceRefresh(true, probeSnapshot.Length, stopwatch.Elapsed, deferredByBatchCapture);
        }
        catch (Exception exception)
        {
            DeferProbeResourceRefresh(state);
            if (committed)
                throw;

            Debug.RenderingWarningEvery(
                "Advanced.ProbeArrayRefreshRejected",
                TimeSpan.FromSeconds(2),
                "[Advanced] Probe array refresh deferred; keeping the previous publication. {0}",
                exception.Message);
        }
        finally
        {
            if (!committed)
            {
                irradiance?.Destroy();
                prefilter?.Destroy();
                ForwardLightProbeInstanceResources.DestroyBuffer(ref positionBuffer);
                ForwardLightProbeInstanceResources.DestroyBuffer(ref parameterBuffer);
            }

            grid?.Destroy();
            foreach (LightProbeIblOutputGeneration generation in retained)
                generation.ReleasePublication();
        }
    }

    private static void DeferProbeResourceRefresh(ForwardLightProbeInstanceResources state)
    {
        state.PendingProbeRefresh = true;
        state.ProbeRefreshEarliestFrameId = RuntimeEngine.Rendering.State.RenderFrameId + 1;
    }

    private static void PushProbeTextureArray(AbstractRenderer renderer, XRTexture2DArray texture)
    {
        if (renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is null)
            throw new InvalidOperationException("The renderer did not create the required probe array.");
        texture.PushData();
        if (!renderer.IsTextureReadyForShaderSampling(texture))
            throw new InvalidOperationException("The required probe array copy has not completed.");
    }
}
