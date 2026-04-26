namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static class Constants
            {
                public const string ShadowExponentBaseUniform = "ShadowBase";
                public const string ShadowExponentUniform = "ShadowMult";
                public const string ShadowBiasMinUniform = "ShadowBiasMin";
                public const string ShadowBiasMaxUniform = "ShadowBiasMax";

                public const string BoneTransformsName = "Transforms";
                //public const string BoneNrmMtxName = "BoneNrmMtx";
                public const string MorphWeightsName = "MorphWeights";
                public const string LightsStructName = "LightData";

                public const string EngineFontsCommonFolderName = "Fonts";

                public const string ShadowSamples = "ShadowSamples";
                public const string ShadowBlockerSamples = "ShadowBlockerSamples";
                public const string ShadowFilterSamples = "ShadowFilterSamples";
                public const string ShadowVogelTapCount = "ShadowVogelTapCount";
                public const string ShadowFilterRadius = "ShadowFilterRadius";
                public const string ShadowBlockerSearchRadius = "ShadowBlockerSearchRadius";
                public const string ShadowMinPenumbra = "ShadowMinPenumbra";
                public const string ShadowMaxPenumbra = "ShadowMaxPenumbra";
                public const string SoftShadowMode = "SoftShadowMode";
                public const string LightSourceRadius = "LightSourceRadius";
                public const string EnableCascadedShadows = "EnableCascadedShadows";
                public const string EnableContactShadows = "EnableContactShadows";
                public const string ContactShadowDistance = "ContactShadowDistance";
                public const string ContactShadowSamples = "ContactShadowSamples";
                public const string ContactShadowThickness = "ContactShadowThickness";
                public const string ContactShadowFadeStart = "ContactShadowFadeStart";
                public const string ContactShadowFadeEnd = "ContactShadowFadeEnd";
                public const string ContactShadowNormalOffset = "ContactShadowNormalOffset";
                public const string ContactShadowJitterStrength = "ContactShadowJitterStrength";
            }
        }
    }
}
