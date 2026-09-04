using System.Numerics;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering;

/// <summary>
/// Immutable numeric global-resource input captured at the world-swap boundary.
/// Texture ownership remains deliberately external until the shared resource
/// publisher preflights its single aggregate acquire transaction.
/// </summary>
public readonly record struct AdvancedGlobalResourceCapture(
    ulong FrameId,
    ReadOnlyMemory<object?> LightSources,
    ReadOnlyMemory<AdvancedLightRecord> Lights,
    ReadOnlyMemory<AdvancedShadowRecord> Shadows,
    ReadOnlyMemory<AdvancedShadowCaptureRow> ShadowRows,
    ReadOnlyMemory<AdvancedProbeRecord> Probes,
    ReadOnlyMemory<AdvancedEnvironmentRecord> Environments,
    ReadOnlyMemory<AdvancedDecalRecord> Decals,
    ReadOnlyMemory<AdvancedGiResourceRecord> GiResources)
{
    public static AdvancedGlobalResourceCapture Empty(ulong frameId)
        => new(frameId, default, default, default, default, default, default, default, default);

    /// <summary>
    /// Captures only world-owned light/probe numeric state. Environment, decal,
    /// and GI collection owners are not exposed by <see cref="IRuntimeRenderWorld"/>
    /// and intentionally remain valid-empty rather than inferred from pipelines.
    /// </summary>
    public static AdvancedGlobalResourceCapture Capture(
        ulong frameId,
        IRuntimeRenderWorld? world)
    {
        if (world is null)
            return Empty(frameId);

        int lightCount = world.Lights.DynamicDirectionalLights.Count +
            world.Lights.DynamicPointLights.Count + world.Lights.DynamicSpotLights.Count;
        object?[] lightSources = lightCount == 0 ? [] : new object?[lightCount];
        AdvancedLightRecord[] lights = lightCount == 0 ? [] : new AdvancedLightRecord[lightCount];
        int index = 0;
        foreach (DirectionalLightComponent light in world.Lights.DynamicDirectionalLights)
        {
            lightSources[index] = light;
            lights[index++] = CreateLight(light, EAdvancedLightType.Directional, 0.0f, 0.0f, 0.0f, 0.0f);
        }
        foreach (PointLightComponent light in world.Lights.DynamicPointLights)
        {
            lightSources[index] = light;
            lights[index++] = CreateLight(light, EAdvancedLightType.Point, light.Radius, 0.0f, 0.0f, light.Brightness);
        }
        foreach (SpotLightComponent light in world.Lights.DynamicSpotLights)
        {
            lightSources[index] = light;
            lights[index++] = CreateLight(
                light,
                EAdvancedLightType.Spot,
                light.Distance,
                light.OuterCutoffAngleDegrees,
                light.InnerCutoffAngleDegrees,
                light.Brightness);
        }

        List<AdvancedShadowCaptureRow> shadowRows = [];
        for (int lightIndex = 0; lightIndex < index; ++lightIndex)
        {
            int groupStart = shadowRows.Count;
            if (lightSources[lightIndex] is DirectionalLightComponent directional)
                CaptureDirectionalShadows(world.Lights, directional, lightIndex, shadowRows);
            else if (lightSources[lightIndex] is PointLightComponent point)
                CapturePointAtlasShadows(world.Lights, point, lightIndex, shadowRows);
            else if (lightSources[lightIndex] is SpotLightComponent spot)
                CaptureSpotAtlasShadow(world.Lights, spot, lightIndex, shadowRows);
            int groupCount = shadowRows.Count - groupStart;
            for (int shadowIndex = groupStart; shadowIndex < shadowRows.Count; ++shadowIndex)
            {
                AdvancedShadowCaptureRow row = shadowRows[shadowIndex];
                AdvancedShadowRecord record = row.Record;
                record.CascadeCount = checked((uint)groupCount);
                shadowRows[shadowIndex] = new(row.LightIndex, record, row.Texture);
            }
        }

        // Probe texture references require the shared resource transaction, so
        // this boundary capture retains only numeric influence state for now.
        return new(frameId, lightSources, lights, default, shadowRows.ToArray(), default, default, default, default);
    }

    private static void CaptureDirectionalShadows(Lights3DCollection lights, DirectionalLightComponent light, int lightIndex, List<AdvancedShadowCaptureRow> rows)
    {
        DirectionalShadowGpuRecord[] records = new DirectionalShadowGpuRecord[8];
        CaptureDirectionalSource(lights, light, lightIndex, rows, records, ShadowRequestSource.Desktop, false);
        CaptureDirectionalSource(lights, light, lightIndex, rows, records, ShadowRequestSource.Hmd, true);
    }

    private static void CaptureDirectionalSource(Lights3DCollection lights, DirectionalLightComponent light, int lightIndex, List<AdvancedShadowCaptureRow> rows, DirectionalShadowGpuRecord[] records, ShadowRequestSource source, bool hmd)
    {
            light.CopyPublishedDirectionalShadowRecords(source, true, records, out int count);
            for (int cascade = 0; cascade < count; ++cascade)
            {
                if (!lights.TryGetDirectionalCascadeShadowAtlasAllocation(light, source, cascade, out ShadowAtlasAllocation allocation, out _) ||
                    !lights.ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Directional, light.ShadowMapEncoding, allocation.PageIndex, out XRTexture2DArray texture))
                    continue;
                ref readonly DirectionalShadowGpuRecord published = ref records[cascade];
                bool resident = allocation.IsResident && allocation.LastRenderedFrame != 0u;
                EAdvancedShadowRecordFlags flags = EAdvancedShadowRecordFlags.DepthZeroToOne |
                    (RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown ? EAdvancedShadowRecordFlags.FramebufferTextureYDown : EAdvancedShadowRecordFlags.None) |
                    (resident ? EAdvancedShadowRecordFlags.Resident : EAdvancedShadowRecordFlags.None) |
                    (hmd ? EAdvancedShadowRecordFlags.HmdSource : EAdvancedShadowRecordFlags.None) |
                    (allocation.ActiveFallback == ShadowFallbackMode.StaleTile ? EAdvancedShadowRecordFlags.StaleFallback : EAdvancedShadowRecordFlags.None) |
                    (light.ShadowMapEncoding == EShadowMapEncoding.Depth ? EAdvancedShadowRecordFlags.None : EAdvancedShadowRecordFlags.MomentEncoded);
                rows.Add(new(lightIndex, new AdvancedShadowRecord
                {
                    Type = EAdvancedShadowType.DirectionalCascade, Flags = flags,
                    WorldToShadow = published.RenderedWorldToLight,
                    PreviousWorldToShadow = published.RenderedWorldToLight,
                    UvScaleBias = allocation.UvScaleBias,
                    DepthBiasAndFilter = new(published.RenderedSplitBlendBias.Z, published.RenderedSplitBlendBias.W, published.ReceiverOffsetsAge.Y, 1.0f),
                    MomentParameters = new(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, light.ShadowMomentPositiveExponent, light.ShadowMomentNegativeExponent),
                    DepthRangeAndCascade = new(published.AtlasDepthParams.X, published.AtlasDepthParams.Y, published.RenderedSplitBlendBias.X, published.RenderedSplitBlendBias.Y),
                    TextureLayer = checked((uint)Math.Max(0, allocation.PageIndex)), Encoding = (uint)light.ShadowMapEncoding,
                    CascadeCount = (uint)count, LastRenderedFrameLo = (uint)allocation.LastRenderedFrame, LastRenderedFrameHi = (uint)(allocation.LastRenderedFrame >> 32),
                }, texture));
            }
    }

    private static void CapturePointAtlasShadows(Lights3DCollection lights, PointLightComponent light, int lightIndex, List<AdvancedShadowCaptureRow> rows)
    {
        for (int face = 0; face < PointLightComponent.ShadowFaceCount; ++face)
        {
            if (!lights.TryGetPointShadowAtlasFaceAllocation(light, face, out ShadowAtlasAllocation allocation, out _) ||
                !lights.ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Point, light.ShadowMapEncoding, allocation.PageIndex, out XRTexture2DArray texture) ||
                !light.TryGetRenderedShadowAtlasFaceSnapshot(face, allocation, out PointLightComponent.PointShadowAtlasRenderSnapshot snapshot))
            {
                AddMissingShadowRow(lightIndex, EAdvancedShadowType.PointFace, light.ShadowMapEncoding, PointLightComponent.ShadowFaceCount, rows);
                continue;
            }

            uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
            bool resident = allocation.IsResident && allocation.LastRenderedFrame != 0u;
            EAdvancedShadowRecordFlags flags = EAdvancedShadowRecordFlags.DepthZeroToOne |
                (RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown ? EAdvancedShadowRecordFlags.FramebufferTextureYDown : EAdvancedShadowRecordFlags.None) |
                (snapshot.ReversedDepth ? EAdvancedShadowRecordFlags.ReversedDepth : EAdvancedShadowRecordFlags.None) |
                (resident ? EAdvancedShadowRecordFlags.Resident : EAdvancedShadowRecordFlags.None) |
                (light.ShadowMapEncoding == EShadowMapEncoding.Depth ? EAdvancedShadowRecordFlags.None : EAdvancedShadowRecordFlags.MomentEncoded);
            rows.Add(new(lightIndex, new AdvancedShadowRecord
            {
                Type = EAdvancedShadowType.PointFace, Flags = flags,
                WorldToShadow = snapshot.WorldToShadow, PreviousWorldToShadow = snapshot.WorldToShadow, UvScaleBias = allocation.UvScaleBias,
                DepthBiasAndFilter = new(light.ShadowMinBias, light.ShadowMaxBias, 0.0f, light.FilterRadius * sampleResolution),
                MomentParameters = new(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, light.ShadowMomentPositiveExponent, light.ShadowMomentNegativeExponent),
                DepthRangeAndCascade = new(snapshot.NearPlane, snapshot.FarPlane, 0.0f, 0.0f),
                RenderedLightPositionAndFar = snapshot.RenderedLightPositionAndFar,
                TextureLayer = checked((uint)Math.Max(0, allocation.PageIndex)), Encoding = (uint)light.ShadowMapEncoding,
                CascadeCount = PointLightComponent.ShadowFaceCount, LastRenderedFrameLo = (uint)allocation.LastRenderedFrame, LastRenderedFrameHi = (uint)(allocation.LastRenderedFrame >> 32),
            }, texture));
        }
    }
    private static void CaptureSpotAtlasShadow(Lights3DCollection lights, SpotLightComponent light, int lightIndex, List<AdvancedShadowCaptureRow> rows)
    {
        if (!lights.TryGetSpotShadowAtlasAllocation(light, out ShadowAtlasAllocation allocation, out _) ||
            !lights.ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Spot, light.ShadowMapEncoding, allocation.PageIndex, out XRTexture2DArray texture) ||
            !light.TryGetRenderedShadowAtlasSnapshot(allocation, out SpotLightComponent.SpotShadowAtlasRenderSnapshot snapshot))
        {
            AddMissingShadowRow(lightIndex, EAdvancedShadowType.Spot, light.ShadowMapEncoding, 1u, rows);
            return;
        }

        uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
        bool resident = allocation.IsResident && allocation.LastRenderedFrame != 0u;
        EAdvancedShadowRecordFlags flags = EAdvancedShadowRecordFlags.DepthZeroToOne |
            (RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown ? EAdvancedShadowRecordFlags.FramebufferTextureYDown : EAdvancedShadowRecordFlags.None) |
            (snapshot.ReversedDepth ? EAdvancedShadowRecordFlags.ReversedDepth : EAdvancedShadowRecordFlags.None) |
            (resident ? EAdvancedShadowRecordFlags.Resident : EAdvancedShadowRecordFlags.None) |
            (light.ShadowMapEncoding == EShadowMapEncoding.Depth ? EAdvancedShadowRecordFlags.None : EAdvancedShadowRecordFlags.MomentEncoded) |
            (light.ShadowMapEncoding == EShadowMapEncoding.Depth ? EAdvancedShadowRecordFlags.None : EAdvancedShadowRecordFlags.LinearizedPerspectiveMoments);
        rows.Add(new(lightIndex, new AdvancedShadowRecord
        {
            Type = EAdvancedShadowType.Spot, Flags = flags,
            WorldToShadow = snapshot.WorldToShadow, PreviousWorldToShadow = snapshot.WorldToShadow,
            UvScaleBias = allocation.UvScaleBias, TextureLayer = checked((uint)Math.Max(0, allocation.PageIndex)), Encoding = (uint)light.ShadowMapEncoding,
            DepthBiasAndFilter = new(light.ShadowMinBias, light.ShadowMaxBias, 0.0f, light.FilterRadius * sampleResolution),
            MomentParameters = new(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, light.ShadowMomentPositiveExponent, light.ShadowMomentNegativeExponent),
            DepthRangeAndCascade = new(snapshot.NearPlane, snapshot.FarPlane, 0.0f, 0.0f),
            RenderedLightPositionAndFar = snapshot.RenderedLightPositionAndFar,
            LastRenderedFrameLo = (uint)allocation.LastRenderedFrame, LastRenderedFrameHi = (uint)(allocation.LastRenderedFrame >> 32),
        }, texture));
    }

    private static void AddMissingShadowRow(
        int lightIndex,
        EAdvancedShadowType type,
        EShadowMapEncoding encoding,
        uint cascadeCount,
        List<AdvancedShadowCaptureRow> rows)
        => rows.Add(new(lightIndex, new AdvancedShadowRecord
        {
            Type = type,
            Encoding = (uint)encoding,
            CascadeCount = cascadeCount,
        }, null));
    private static AdvancedLightRecord CreateLight(
        LightComponent light,
        EAdvancedLightType type,
        float radius,
        float outerConeDegrees,
        float innerConeDegrees,
        float localBrightness)
    {
        Matrix4x4 matrix = light.LightMeshMatrix;
        Vector3 direction = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, matrix));
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z))
            direction = -Vector3.UnitZ;
        float intensity = light.DiffuseIntensity * (localBrightness == 0.0f ? 1.0f : localBrightness);
        return new AdvancedLightRecord
        {
            Type = type,
            Flags = EAdvancedLightRecordFlags.Enabled |
                (light.CastsShadows ? EAdvancedLightRecordFlags.CastsShadow : EAdvancedLightRecordFlags.None),
            PositionAndRadius = new(matrix.M41, matrix.M42, matrix.M43, radius),
            DirectionAndOuterCone = new(direction, MathF.Cos(outerConeDegrees * (MathF.PI / 180.0f))),
            ColorAndIntensity = new(light.Color.R, light.Color.G, light.Color.B, intensity),
            ShapeAndInnerCone = new(0.0f, 0.0f, 0.0f, MathF.Cos(innerConeDegrees * (MathF.PI / 180.0f))),
        };
    }
}

/// <summary>
/// One immutable, light-owned shadow row captured at the world-swap boundary.
/// The texture identity is retained separately from the ABI record so resource
/// handles are allocated only by the canonical publisher transaction.
/// </summary>
public readonly record struct AdvancedShadowCaptureRow(
    int LightIndex,
    AdvancedShadowRecord Record,
    XRTexture? Texture);
