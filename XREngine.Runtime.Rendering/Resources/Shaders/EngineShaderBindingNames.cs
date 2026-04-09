namespace XREngine.Rendering;

/// <summary>
/// Canonical shader binding names supplied by the engine outside of material-authored user parameters.
/// Core camera/time uniforms remain defined by <see cref="EEngineUniform"/>; this catalog covers the
/// supplemental renderer-provided uniform and sampler names that are expressed as raw strings in GLSL.
/// </summary>
public static class EngineShaderBindingNames
{
    public static class Uniforms
    {
        public const string DirLightCount = "DirLightCount";
        public const string PointLightCount = "PointLightCount";
        public const string SpotLightCount = "SpotLightCount";
        public const string GlobalAmbient = "GlobalAmbient";
        public const string ForwardPlusEnabled = "ForwardPlusEnabled";
        public const string ForwardPlusScreenSize = "ForwardPlusScreenSize";
        public const string ForwardPlusTileSize = "ForwardPlusTileSize";
        public const string ForwardPlusMaxLightsPerTile = "ForwardPlusMaxLightsPerTile";
        public const string ForwardPlusEyeCount = "ForwardPlusEyeCount";
        public const string AmbientOcclusionEnabled = "AmbientOcclusionEnabled";
        public const string AmbientOcclusionArrayEnabled = "AmbientOcclusionArrayEnabled";
        public const string AmbientOcclusionPower = "AmbientOcclusionPower";
        public const string AmbientOcclusionMultiBounce = "AmbientOcclusionMultiBounce";
        public const string SpecularOcclusionEnabled = "SpecularOcclusionEnabled";
        public const string DebugForwardAOPower = "DebugForwardAOPower";
        public const string ForwardPbrResourcesEnabled = "ForwardPbrResourcesEnabled";
        public const string ProbeCount = "ProbeCount";
        public const string TetraCount = "TetraCount";
        public const string ProbeGridDims = "ProbeGridDims";
        public const string ProbeGridOrigin = "ProbeGridOrigin";
        public const string ProbeGridCellSize = "ProbeGridCellSize";
        public const string UseProbeGrid = "UseProbeGrid";
        public const string ShadowMapEnabled = "ShadowMapEnabled";
        public const string UseCascadedDirectionalShadows = "UseCascadedDirectionalShadows";
        public const string PrimaryDirLightWorldToLightInvViewMatrix = "PrimaryDirLightWorldToLightInvViewMatrix";
        public const string PrimaryDirLightWorldToLightProjMatrix = "PrimaryDirLightWorldToLightProjMatrix";
        public const string ShadowBase = "ShadowBase";
        public const string ShadowMult = "ShadowMult";
        public const string ShadowBiasMin = "ShadowBiasMin";
        public const string ShadowBiasMax = "ShadowBiasMax";
        public const string ShadowSamples = "ShadowSamples";
        public const string ShadowVogelTapCount = "ShadowVogelTapCount";
        public const string ShadowFilterRadius = "ShadowFilterRadius";
        public const string SoftShadowMode = "SoftShadowMode";
        public const string LightSourceRadius = "LightSourceRadius";
        public const string EnableCascadedShadows = "EnableCascadedShadows";
        public const string EnableContactShadows = "EnableContactShadows";
        public const string ContactShadowDistance = "ContactShadowDistance";
        public const string ContactShadowSamples = "ContactShadowSamples";
        public const string LightHasShadowMap = "LightHasShadowMap";
        public const string ShadowDebugMode = "ShadowDebugMode";
        public const string DirLightData = "DirLightData";
        public const string PointLightData = "PointLightData";
        public const string SpotLightData = "SpotLightData";
        public const string DirectionalLights = "DirectionalLights";
        public const string PointLights = "PointLights";
        public const string SpotLights = "SpotLights";
        public const string PointLightShadowSlots = "PointLightShadowSlots";
        public const string PointLightShadowNearPlanes = "PointLightShadowNearPlanes";
        public const string PointLightShadowFarPlanes = "PointLightShadowFarPlanes";
        public const string PointLightShadowBase = "PointLightShadowBase";
        public const string PointLightShadowExponent = "PointLightShadowExponent";
        public const string PointLightShadowBiasMin = "PointLightShadowBiasMin";
        public const string PointLightShadowBiasMax = "PointLightShadowBiasMax";
        public const string PointLightShadowSamples = "PointLightShadowSamples";
        public const string PointLightShadowVogelTapCount = "PointLightShadowVogelTapCount";
        public const string PointLightShadowFilterRadius = "PointLightShadowFilterRadius";
        public const string PointLightShadowSoftShadowMode = "PointLightShadowSoftShadowMode";
        public const string PointLightShadowLightSourceRadius = "PointLightShadowLightSourceRadius";
        public const string PointLightShadowDebugModes = "PointLightShadowDebugModes";
        public const string SpotLightShadowSlots = "SpotLightShadowSlots";
        public const string SpotLightShadowBase = "SpotLightShadowBase";
        public const string SpotLightShadowExponent = "SpotLightShadowExponent";
        public const string SpotLightShadowBiasMin = "SpotLightShadowBiasMin";
        public const string SpotLightShadowBiasMax = "SpotLightShadowBiasMax";
        public const string SpotLightShadowSamples = "SpotLightShadowSamples";
        public const string SpotLightShadowVogelTapCount = "SpotLightShadowVogelTapCount";
        public const string SpotLightShadowFilterRadius = "SpotLightShadowFilterRadius";
        public const string SpotLightShadowSoftShadowMode = "SpotLightShadowSoftShadowMode";
        public const string SpotLightShadowLightSourceRadius = "SpotLightShadowLightSourceRadius";
        public const string SpotLightShadowDebugModes = "SpotLightShadowDebugModes";
        public const string EnvironmentMapMipLevels = "u_EnvironmentMapMipLevels";

        // Exact transparency — per-pixel linked list (PPLL)
        public const string PpllMaxNodes = "PpllMaxNodes";

        // Exact transparency — depth peeling
        public const string DepthPeelLayerIndex = "DepthPeelLayerIndex";
        public const string DepthPeelEpsilon = "DepthPeelEpsilon";
    }

    public static class Samplers
    {
        public const string AmbientOcclusionTexture = "AmbientOcclusionTexture";
        public const string AmbientOcclusionTextureArray = "AmbientOcclusionTextureArray";
        public const string BRDF = "BRDF";
        public const string IrradianceArray = "IrradianceArray";
        public const string PrefilterArray = "PrefilterArray";
        public const string ShadowMap = "ShadowMap";
        public const string ShadowMapArray = "ShadowMapArray";
        public const string PointLightShadowMaps = "PointLightShadowMaps";
        public const string SpotLightShadowMaps = "SpotLightShadowMaps";
        public const string EnvironmentMap = "u_EnvironmentMap";
        public const string PbrReflectionCube = "_PBRReflCube";
        public const string PrevPeelDepth = "PrevPeelDepth";
        public const string PpllHeadPointers = "PpllHeadPointers";
    }
}