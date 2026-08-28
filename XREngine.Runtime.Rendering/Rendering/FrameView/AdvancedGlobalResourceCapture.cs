using System.Numerics;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;

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
    ReadOnlyMemory<AdvancedProbeRecord> Probes,
    ReadOnlyMemory<AdvancedEnvironmentRecord> Environments,
    ReadOnlyMemory<AdvancedDecalRecord> Decals,
    ReadOnlyMemory<AdvancedGiResourceRecord> GiResources)
{
    public static AdvancedGlobalResourceCapture Empty(ulong frameId)
        => new(frameId, default, default, default, default, default, default, default);

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
            lights[index++] = CreateLight(light, EAdvancedLightType.Directional, 0.0f, 0.0f, 0.0f);
        }
        foreach (PointLightComponent light in world.Lights.DynamicPointLights)
        {
            lightSources[index] = light;
            lights[index++] = CreateLight(light, EAdvancedLightType.Point, light.Radius, 0.0f, light.Brightness);
        }
        foreach (SpotLightComponent light in world.Lights.DynamicSpotLights)
        {
            lightSources[index] = light;
            lights[index++] = CreateLight(light, EAdvancedLightType.Spot, light.Distance, light.OuterCutoffAngleDegrees, light.Brightness);
        }

        // Probe texture references require the shared resource transaction, so
        // this boundary capture retains only numeric influence state for now.
        return new(frameId, lightSources, lights, default, default, default, default, default);
    }

    private static AdvancedLightRecord CreateLight(
        LightComponent light,
        EAdvancedLightType type,
        float radius,
        float outerConeDegrees,
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
        };
    }
}
