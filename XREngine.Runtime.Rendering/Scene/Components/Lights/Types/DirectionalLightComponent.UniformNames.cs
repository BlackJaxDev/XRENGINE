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
            CascadeSplits = flatPrefix + "CascadeSplits";
            CascadeBlendWidths = flatPrefix + "CascadeBlendWidths";
            CascadeBiasMin = flatPrefix + "CascadeBiasMin";
            CascadeBiasMax = flatPrefix + "CascadeBiasMax";
            CascadeReceiverOffsets = flatPrefix + "CascadeReceiverOffsets";
            CascadeMatrices = flatPrefix + "CascadeMatrices";
            RenderedCascadeSplits = flatPrefix + "RenderedCascadeSplits";
            RenderedCascadeBlendWidths = flatPrefix + "RenderedCascadeBlendWidths";
            RenderedCascadeBiasMin = flatPrefix + "RenderedCascadeBiasMin";
            RenderedCascadeBiasMax = flatPrefix + "RenderedCascadeBiasMax";
            RenderedCascadeReceiverOffsets = flatPrefix + "RenderedCascadeReceiverOffsets";
            RenderedCascadeMatrices = flatPrefix + "RenderedCascadeMatrices";
            RenderedCascadeStaleAge = flatPrefix + "RenderedCascadeStaleAge";

            IndexedCascadeSplits = CreateIndexedNames(CascadeSplits);
            IndexedCascadeBlendWidths = CreateIndexedNames(CascadeBlendWidths);
            IndexedCascadeBiasMin = CreateIndexedNames(CascadeBiasMin);
            IndexedCascadeBiasMax = CreateIndexedNames(CascadeBiasMax);
            IndexedCascadeReceiverOffsets = CreateIndexedNames(CascadeReceiverOffsets);
            IndexedCascadeMatrices = CreateIndexedNames(CascadeMatrices);
            IndexedRenderedCascadeSplits = CreateIndexedNames(RenderedCascadeSplits);
            IndexedRenderedCascadeBlendWidths = CreateIndexedNames(RenderedCascadeBlendWidths);
            IndexedRenderedCascadeBiasMin = CreateIndexedNames(RenderedCascadeBiasMin);
            IndexedRenderedCascadeBiasMax = CreateIndexedNames(RenderedCascadeBiasMax);
            IndexedRenderedCascadeReceiverOffsets = CreateIndexedNames(RenderedCascadeReceiverOffsets);
            IndexedRenderedCascadeMatrices = CreateIndexedNames(RenderedCascadeMatrices);
            IndexedRenderedCascadeStaleAge = CreateIndexedNames(RenderedCascadeStaleAge);

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
        public string CascadeSplits { get; }
        public string CascadeBlendWidths { get; }
        public string CascadeBiasMin { get; }
        public string CascadeBiasMax { get; }
        public string CascadeReceiverOffsets { get; }
        public string CascadeMatrices { get; }
        public string RenderedCascadeSplits { get; }
        public string RenderedCascadeBlendWidths { get; }
        public string RenderedCascadeBiasMin { get; }
        public string RenderedCascadeBiasMax { get; }
        public string RenderedCascadeReceiverOffsets { get; }
        public string RenderedCascadeMatrices { get; }
        public string RenderedCascadeStaleAge { get; }
        public string[] IndexedCascadeSplits { get; }
        public string[] IndexedCascadeBlendWidths { get; }
        public string[] IndexedCascadeBiasMin { get; }
        public string[] IndexedCascadeBiasMax { get; }
        public string[] IndexedCascadeReceiverOffsets { get; }
        public string[] IndexedCascadeMatrices { get; }
        public string[] IndexedRenderedCascadeSplits { get; }
        public string[] IndexedRenderedCascadeBlendWidths { get; }
        public string[] IndexedRenderedCascadeBiasMin { get; }
        public string[] IndexedRenderedCascadeBiasMax { get; }
        public string[] IndexedRenderedCascadeReceiverOffsets { get; }
        public string[] IndexedRenderedCascadeMatrices { get; }
        public string[] IndexedRenderedCascadeStaleAge { get; }
        public string BaseColor { get; }
        public string BaseDiffuseIntensity { get; }
        public string BaseAmbientIntensity { get; }
        public string BaseWorldToLightSpaceProjMatrix { get; }

        private static string[] CreateIndexedNames(string arrayName)
        {
            string[] names = new string[MaxCascadeRenderCount];
            for (int index = 0; index < names.Length; index++)
                names[index] = $"{arrayName}[{index}]";
            return names;
        }
    }
}
