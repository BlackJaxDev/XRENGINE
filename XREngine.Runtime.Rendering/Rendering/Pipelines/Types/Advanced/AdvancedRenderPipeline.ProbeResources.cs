using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Stages a complete probe refresh before replacing the last usable publication.
    /// Allocations here occur only on structural/capture-version changes, not sampling.
    /// </summary>
    private void BuildProbeResources(IList<LightProbeComponent> readyProbes, bool deferredByBatchCapture = false)
    {
        if (readyProbes.Count == 0)
        {
            ClearProbeResources();
            return;
        }

        if (!RuntimeEngine.IsRenderThread || AbstractRenderer.Current is not { } renderer)
        {
            DeferProbeResourceRefresh();
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        var retained = new List<LightProbeIblOutputGeneration>(readyProbes.Count);
        var irradianceSources = new XRTexture2D[readyProbes.Count];
        var prefilterSources = new XRTexture2D[readyProbes.Count];
        var positions = new List<ProbePositionData>(readyProbes.Count);
        var parameters = new List<ProbeParamData>(readyProbes.Count);
        XRTexture2DArray? irradiance = null;
        XRTexture2DArray? prefilter = null;
        XRDataBuffer? positionBuffer = null;
        XRDataBuffer? parameterBuffer = null;
        bool committed = false;
        try
        {
            for (int index = 0; index < readyProbes.Count; index++)
            {
                LightProbeComponent probe = readyProbes[index];
                if (!probe.TryGetActiveIblOutput(out LightProbeIblOutputGeneration generation) ||
                    !generation.TryRetainPublication())
                    throw new InvalidOperationException("A completed probe generation retired before array assembly.");
                retained.Add(generation);
                irradianceSources[index] = generation.Irradiance;
                prefilterSources[index] = generation.PrefilteredRadiance;
                positions.Add(new ProbePositionData { Position = new Vector4(probe.Transform.RenderTranslation, 1.0f) });
                parameters.Add(new ProbeParamData
                {
                    InfluenceInner = new Vector4(probe.InfluenceBoxInnerExtents, probe.InfluenceSphereInnerRadius),
                    InfluenceOuter = new Vector4(probe.InfluenceBoxOuterExtents, probe.InfluenceSphereOuterRadius),
                    InfluenceOffsetShape = new Vector4(probe.InfluenceOffset, probe.InfluenceShape == LightProbeComponent.EInfluenceShape.Box ? 1.0f : 0.0f),
                    ProxyCenterEnable = new Vector4(probe.ProxyBoxCenterOffset, probe.ParallaxCorrectionEnabled ? 1.0f : 0.0f),
                    ProxyHalfExtents = new Vector4(probe.ProxyBoxHalfExtents, probe.NormalizationScale),
                    ProxyRotation = new Vector4(probe.ProxyBoxRotation.X, probe.ProxyBoxRotation.Y, probe.ProxyBoxRotation.Z, probe.ProxyBoxRotation.W),
                });
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

            positionBuffer = new XRDataBuffer(LightProbePositionBufferName, EBufferTarget.ShaderStorageBuffer,
                (uint)positions.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbePositionData>(), false, false)
            {
                BindingIndexOverride = 0,
            };
            positionBuffer.SetDataRaw<ProbePositionData>(positions);
            positionBuffer.PushData();
            parameterBuffer = new XRDataBuffer(LightProbeParamBufferName, EBufferTarget.ShaderStorageBuffer,
                (uint)parameters.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeParamData>(), false, false)
            {
                BindingIndexOverride = 2,
            };
            parameterBuffer.SetDataRaw<ProbeParamData>(parameters);
            parameterBuffer.PushData();

            // No ordinary copy/descriptor rejection after this point: both candidate
            // arrays are ready, and old native images retire through their last-use tickets.
            ClearProbeResources();
            _probeIrradianceArray = irradiance;
            _probePrefilterArray = prefilter;
            _probePositionBuffer = positionBuffer;
            _probeParamBuffer = parameterBuffer;
            committed = true;
            RegisterProbeTextureArrays();
            RegisterProbeBuffer(positionBuffer);
            RegisterProbeBuffer(parameterBuffer);
            for (int index = 0; index < readyProbes.Count; index++)
            {
                Guid id = readyProbes[index].ID;
                Vector4 position = positions[index].Position;
                _cachedProbePositions[id] = new Vector3(position.X, position.Y, position.Z);
                _cachedProbeTextures[id] = (irradianceSources[index], prefilterSources[index]);
                _observedProbeCaptureVersions[id] = retained[index].Generation;
            }
            _cachedProbePositionData = [.. positions];
            _cachedProbeParamData = [.. parameters];
            if (_useProbeGridAcceleration)
                BuildProbeGrid(_cachedProbePositionData, _cachedProbeParamData, null);
            _lastProbeCount = positions.Count;
            _pendingProbeRefresh = false;
            _pendingProbeRefreshDeferredByBatchCapture = false;
            StartTetrahedralizationJob(readyProbes);
            stopwatch.Stop();
            ReportProbeResourceRefresh(true, readyProbes.Count, stopwatch.Elapsed, deferredByBatchCapture);
        }
        catch (Exception exception)
        {
            DeferProbeResourceRefresh();
            if (committed)
                throw;
            Debug.RenderingWarningEvery("Advanced.ProbeArrayRefreshRejected", TimeSpan.FromSeconds(2),
                "[Advanced] Probe array refresh deferred; keeping the previous publication. {0}", exception.Message);
        }
        finally
        {
            if (!committed)
            {
                irradiance?.Destroy();
                prefilter?.Destroy();
                DestroyProbeBuffer(ref positionBuffer);
                DestroyProbeBuffer(ref parameterBuffer);
            }
            foreach (LightProbeIblOutputGeneration generation in retained)
                generation.ReleasePublication();
        }
    }

    private void DeferProbeResourceRefresh()
    {
        _pendingProbeRefresh = true;
        _probeRefreshEarliestFrameId = RuntimeEngine.Rendering.State.RenderFrameId + 1;
    }

    private static void PushProbeTextureArray(AbstractRenderer renderer, XRTexture2DArray texture)
    {
        if (renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is null)
            throw new InvalidOperationException("The renderer did not create the required probe array.");
        texture.PushData();
        // Unlike PushData's void event, this does not mistake a deferred/busy/lost
        // backend for a completed copy. Vulkan publishes readiness after its fence.
        if (!renderer.IsTextureReadyForShaderSampling(texture))
            throw new InvalidOperationException("The required probe array copy has not completed.");
    }
}
