using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Rendering;

/// <summary>
/// Publishes a directional light's soft-shadow and contact-shadow settings into an
/// <see cref="AdvancedShadowRecord"/>, so native shading filters a cascade the same
/// way the forward receivers do.
/// </summary>
internal static class AdvancedShadowRecordDirectionalFilter
{
    /// <summary>
    /// Each farther cascade widens the filter by this fraction of the authored
    /// radius. The value matches the forward cascade receivers.
    /// </summary>
    private const float CascadeFilterGrowth = 0.35f;

    /// <summary>
    /// Returns <paramref name="record"/> with the filter and contact fields set from
    /// <paramref name="light"/>. Radii stay in tile-local UV, like the forward
    /// receivers; the texel size follows the atlas resolution-scale convention.
    /// </summary>
    internal static AdvancedShadowRecord WithDirectionalFilter(
        this AdvancedShadowRecord record,
        LightComponent light,
        in DirectionalShadowGpuRecord published,
        int cascadeIndex)
    {
        float cascadeScale = 1.0f + Math.Max(cascadeIndex, 0) * CascadeFilterGrowth;
        float resolutionScale = MathF.Max(published.AtlasDepthParams.W, 1.0f);
        float localTexelSize = MathF.Max(published.AtlasDepthParams.Z / resolutionScale, 0.0f);

        record.SoftShadowParameters = new(
            MathF.Max(light.FilterRadius, 0.0f) * cascadeScale,
            MathF.Max(light.BlockerSearchRadius, 0.0f),
            MathF.Max(light.MinPenumbra, 0.0f),
            MathF.Max(light.MaxPenumbra, 0.0f));
        record.ContactParameters = new(
            MathF.Max(light.EffectiveLightSourceRadius, 0.0f),
            MathF.Max(light.ContactShadowDistance, 0.0f),
            MathF.Max(light.ContactShadowThickness, 0.0f),
            MathF.Max(light.ContactShadowNormalOffset, 0.0f));
        record.ContactFadeAndTexel = new(
            light.ContactShadowFadeStart,
            light.ContactShadowFadeEnd,
            Math.Clamp(light.ContactShadowJitterStrength, 0.0f, 1.0f),
            localTexelSize);
        record.SoftShadowMode = (uint)light.SoftShadowMode;
        record.FilterSamples = (uint)Math.Max(light.FilterSamples, 1);
        record.BlockerSamples = (uint)Math.Max(light.BlockerSamples, 1);
        record.VogelTapCount = (uint)Math.Max(light.VogelTapCount, 1);
        record.ContactSamples = light.EnableContactShadows && light.ContactShadowDistance > 0.0f
            ? (uint)Math.Max(light.ContactShadowSamples, 0)
            : 0u;
        return record;
    }
}
