using System.Collections.Concurrent;

namespace XREngine.Components.Lights;

public partial class DirectionalLightComponent
{
    private static readonly DirectionalLightUniformNames DefaultUniformNames =
        new(RuntimeEngine.Rendering.Constants.LightsStructName);
    private static readonly ConcurrentDictionary<string, DirectionalLightUniformNames> UniformNamesByPrefix =
        new(StringComparer.Ordinal);

    private static DirectionalLightUniformNames ResolveUniformNames(string? targetStructName)
    {
        string prefix = targetStructName ?? RuntimeEngine.Rendering.Constants.LightsStructName;
        return string.Equals(
            prefix,
            RuntimeEngine.Rendering.Constants.LightsStructName,
            StringComparison.Ordinal)
            ? DefaultUniformNames
            : UniformNamesByPrefix.GetOrAdd(prefix, static value => new DirectionalLightUniformNames(value));
    }

    /// <summary>
    /// Caches the complete directional-light uniform namespace so per-frame light
    /// binding never rebuilds interpolated names.
    /// </summary>
    private sealed class DirectionalLightUniformNames
    {
        public DirectionalLightUniformNames(string prefix)
        {
            string flatPrefix = prefix + ".";
            string basePrefix = prefix + ".Base.";

            Direction = flatPrefix + "Direction";
            Color = flatPrefix + "Color";
            DiffuseIntensity = flatPrefix + "DiffuseIntensity";
            WorldToLightProjMatrix = flatPrefix + "WorldToLightProjMatrix";
            WorldToLightInvViewMatrix = flatPrefix + "WorldToLightInvViewMatrix";
            WorldToLightSpaceMatrix = flatPrefix + "WorldToLightSpaceMatrix";
            CascadeCount = flatPrefix + "CascadeCount";

            BaseColor = basePrefix + "Color";
            BaseDiffuseIntensity = basePrefix + "DiffuseIntensity";
            BaseAmbientIntensity = basePrefix + "AmbientIntensity";
            BaseWorldToLightSpaceProjMatrix = basePrefix + "WorldToLightSpaceProjMatrix";
        }

        public string Direction { get; }
        public string Color { get; }
        public string DiffuseIntensity { get; }
        public string WorldToLightProjMatrix { get; }
        public string WorldToLightInvViewMatrix { get; }
        public string WorldToLightSpaceMatrix { get; }
        public string CascadeCount { get; }
        public string BaseColor { get; }
        public string BaseDiffuseIntensity { get; }
        public string BaseAmbientIntensity { get; }
        public string BaseWorldToLightSpaceProjMatrix { get; }
    }
}
