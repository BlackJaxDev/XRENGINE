#ifndef XRENGINE_POIYOMI_PHASE5_7_UNIFORMS_GLSL
#define XRENGINE_POIYOMI_PHASE5_7_UNIFORMS_GLSL

// Poiyomi Toon 9.3.64 phase 5-7 runtime contract. Property names intentionally
// match the source shader so unlocked and optimizer-renamed materials share one
// semantic import path.

//@category("Poiyomi")
//@subcategory("Surface")
//@feature(id="poiyomi-surface", name="Poiyomi Surface Parity", default=off, cost=medium)
//@depends("render-time")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SURFACE
//@property(name="_MainTexRotation", display="Main Rotation", mode=animated, default="0.0")
uniform float _MainTexRotation;
//@property(name="_MainTexStochasticMode", display="Stochastic Sampling", mode=static, default="0")
uniform int _MainTexStochasticMode;
//@property(name="_MainTexDistortionStrength", display="UV Distortion", mode=animated, default="0.0")
uniform float _MainTexDistortionStrength;
//@property(name="_MainTexDistortionSpeed", display="UV Distortion Speed", mode=animated, default="vec2(0.0)")
uniform vec2 _MainTexDistortionSpeed;
//@property(name="_MainTexDistortionMap", display="UV Distortion Map", slot=texture)
uniform sampler2D _MainTexDistortionMap;
//@property(name="_MainTexDistortionMap_ST", display="UV Distortion Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _MainTexDistortionMap_ST;
//@property(name="_MainTexDistortionMapUV", display="UV Distortion UV", mode=static, default="0")
uniform int _MainTexDistortionMapUV;
//@property(name="_MainTexDistortionMask", display="UV Distortion Mask", slot=texture)
uniform sampler2D _MainTexDistortionMask;
//@property(name="_MainTexDistortionMask_ST", display="UV Distortion Mask Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _MainTexDistortionMask_ST;
//@property(name="_MainTexDistortionMaskUV", display="UV Distortion Mask UV", mode=static, default="0")
uniform int _MainTexDistortionMaskUV;

//@property(name="_MainContrast", display="Contrast", mode=static, default="1.0")
uniform float _MainContrast;
//@property(name="_ColorThemeIndex", display="Main Color Theme", mode=static, default="0")
uniform int _ColorThemeIndex;
//@property(name="_MainGrayscale", display="Grayscale", mode=static, default="0.0")
uniform float _MainGrayscale;
//@property(name="_MainColorReplace", display="Replacement Color", mode=static, default="vec4(1.0)")
uniform vec4 _MainColorReplace;
//@property(name="_MainColorReplaceStrength", display="Replacement Strength", mode=static, default="0.0")
uniform float _MainColorReplaceStrength;

//@property(name="_DetailTexUV", display="Detail UV", mode=static, default="0")
uniform int _DetailTexUV;
//@property(name="_DetailTexBlendMode", display="Detail Blend", mode=static, default="2")
uniform int _DetailTexBlendMode;
//@property(name="_DetailMaskUV", display="Detail Mask UV", mode=static, default="0")
uniform int _DetailMaskUV;
//@property(name="_DetailMaskPan", display="Detail Mask Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _DetailMaskPan;
//@property(name="_DetailNormalMapUV", display="Detail Normal UV", mode=static, default="0")
uniform int _DetailNormalMapUV;
//@property(name="_NormalCorrectStrength", display="Normal Correction", mode=static, default="0.0")
uniform float _NormalCorrectStrength;
//@property(name="_NormalCorrectVertexColor", display="Normal Correction Vertex Mask", mode=static, default="0")
uniform int _NormalCorrectVertexColor;

//@property(name="_BackFaceTexture_ST", display="Backface Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _BackFaceTexture_ST;
//@property(name="_BackFaceTexturePan", display="Backface Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _BackFaceTexturePan;
//@property(name="_BackFaceTextureUV", display="Backface UV", mode=static, default="0")
uniform int _BackFaceTextureUV;
//@property(name="_BackFaceNormalMap", display="Backface Normal", slot=texture, semantic=normal)
uniform sampler2D _BackFaceNormalMap;
//@property(name="_BackFaceNormalMap_ST", display="Backface Normal Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _BackFaceNormalMap_ST;
//@property(name="_BackFaceNormalStrength", display="Backface Normal Strength", mode=static, default="0.0")
uniform float _BackFaceNormalStrength;

//@property(name="_AlphaSource", display="Alpha Source", mode=static, default="0")
uniform int _AlphaSource;
//@property(name="_AlphaMaskChannel", display="Alpha Mask Channel", mode=static, default="0")
uniform int _AlphaMaskChannel;
//@property(name="_AlphaPremultiply", display="Premultiply Alpha", mode=static, default="0.0")
uniform float _AlphaPremultiply;
//@property(name="_AlphaDither", display="Alpha Dither", mode=static, default="0.0")
uniform float _AlphaDither;
//@property(name="_AlphaDitherGradient", display="Object Dither Blend", mode=static, default="0.0")
uniform float _AlphaDitherGradient;
//@property(name="_AlphaDitherSpeed", display="Dither Speed", mode=animated, default="0.0")
uniform float _AlphaDitherSpeed;
//@property(name="_DistanceFade", display="Distance Fade", mode=static, default="0.0")
uniform float _DistanceFade;
//@property(name="_DistanceFadeMin", display="Distance Fade Start", mode=static, default="0.0")
uniform float _DistanceFadeMin;
//@property(name="_DistanceFadeMax", display="Distance Fade End", mode=static, default="25.0")
uniform float _DistanceFadeMax;
//@property(name="_AlphaFresnel", display="Fresnel Alpha", mode=static, default="0.0")
uniform float _AlphaFresnel;
//@property(name="_AlphaFresnelAlpha", display="Fresnel Alpha Intensity", mode=static, default="0.0")
uniform float _AlphaFresnelAlpha;
//@property(name="_AlphaFresnelSharpness", display="Fresnel Sharpness", mode=static, default="0.5")
uniform float _AlphaFresnelSharpness;
//@property(name="_AlphaFresnelWidth", display="Fresnel Width", mode=static, default="0.5")
uniform float _AlphaFresnelWidth;
//@property(name="_AlphaFresnelInvert", display="Invert Fresnel", mode=static, default="0.0")
uniform float _AlphaFresnelInvert;
//@property(name="_AlphaAngular", display="Angular Alpha", mode=static, default="0.0")
uniform float _AlphaAngular;
//@property(name="_AngleForwardDirection", display="Alpha Forward", mode=static, default="vec3(0.0, 0.0, 1.0)")
uniform vec3 _AngleForwardDirection;
//@property(name="_CameraAngleMin", display="Camera Angle Min", mode=static, default="45.0")
uniform float _CameraAngleMin;
//@property(name="_CameraAngleMax", display="Camera Angle Max", mode=static, default="90.0")
uniform float _CameraAngleMax;
//@property(name="_AngleMinAlpha", display="Angular Minimum Alpha", mode=static, default="0.0")
uniform float _AngleMinAlpha;
#endif

//@category("Poiyomi")
//@subcategory("Masks And Themes")
//@feature(id="poiyomi-masks-themes", name="Poiyomi Global Masks And Themes", default=off, cost=medium)
//@depends("poiyomi-surface")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_MASKS_THEMES
//@property(name="_GlobalThemeColor0", display="Theme 0", mode=animated, default="vec4(1.0)")
uniform vec4 _GlobalThemeColor0;
//@property(name="_GlobalThemeColor1", display="Theme 1", mode=animated, default="vec4(1.0)")
uniform vec4 _GlobalThemeColor1;
//@property(name="_GlobalThemeColor2", display="Theme 2", mode=animated, default="vec4(1.0)")
uniform vec4 _GlobalThemeColor2;
//@property(name="_GlobalThemeColor3", display="Theme 3", mode=animated, default="vec4(1.0)")
uniform vec4 _GlobalThemeColor3;
//@property(name="_GlobalThemeAdjust0", display="Theme 0 HSV", mode=animated, default="vec3(0.0)")
uniform vec3 _GlobalThemeAdjust0;
//@property(name="_GlobalThemeAdjust1", display="Theme 1 HSV", mode=animated, default="vec3(0.0)")
uniform vec3 _GlobalThemeAdjust1;
//@property(name="_GlobalThemeAdjust2", display="Theme 2 HSV", mode=animated, default="vec3(0.0)")
uniform vec3 _GlobalThemeAdjust2;
//@property(name="_GlobalThemeAdjust3", display="Theme 3 HSV", mode=animated, default="vec3(0.0)")
uniform vec3 _GlobalThemeAdjust3;

//@property(name="_GlobalMaskTexture0", display="Global Mask 0", slot=texture, semantic=mask)
uniform sampler2D _GlobalMaskTexture0;
//@property(name="_GlobalMaskTexture1", display="Global Mask 1", slot=texture, semantic=mask)
uniform sampler2D _GlobalMaskTexture1;
//@property(name="_GlobalMaskTexture2", display="Global Mask 2", slot=texture, semantic=mask)
uniform sampler2D _GlobalMaskTexture2;
//@property(name="_GlobalMaskTexture3", display="Global Mask 3", slot=texture, semantic=mask)
uniform sampler2D _GlobalMaskTexture3;
//@property(name="_GlobalMaskTexture0_ST", display="Global Mask 0 Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _GlobalMaskTexture0_ST;
//@property(name="_GlobalMaskTexture1_ST", display="Global Mask 1 Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _GlobalMaskTexture1_ST;
//@property(name="_GlobalMaskTexture2_ST", display="Global Mask 2 Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _GlobalMaskTexture2_ST;
//@property(name="_GlobalMaskTexture3_ST", display="Global Mask 3 Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _GlobalMaskTexture3_ST;
//@property(name="_GlobalMaskTexture0Pan", display="Global Mask 0 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _GlobalMaskTexture0Pan;
//@property(name="_GlobalMaskTexture1Pan", display="Global Mask 1 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _GlobalMaskTexture1Pan;
//@property(name="_GlobalMaskTexture2Pan", display="Global Mask 2 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _GlobalMaskTexture2Pan;
//@property(name="_GlobalMaskTexture3Pan", display="Global Mask 3 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _GlobalMaskTexture3Pan;
//@property(name="_GlobalMaskTexture0UV", display="Global Mask 0 UV", mode=static, default="0")
uniform int _GlobalMaskTexture0UV;
//@property(name="_GlobalMaskTexture1UV", display="Global Mask 1 UV", mode=static, default="0")
uniform int _GlobalMaskTexture1UV;
//@property(name="_GlobalMaskTexture2UV", display="Global Mask 2 UV", mode=static, default="0")
uniform int _GlobalMaskTexture2UV;
//@property(name="_GlobalMaskTexture3UV", display="Global Mask 3 UV", mode=static, default="0")
uniform int _GlobalMaskTexture3UV;
//@property(name="_GlobalMaskMin", display="Global Mask Min", mode=static, default="vec4(0.0)")
uniform vec4 _GlobalMaskMin;
//@property(name="_GlobalMaskMax", display="Global Mask Max", mode=static, default="vec4(1.0)")
uniform vec4 _GlobalMaskMax;
//@property(name="_GlobalMaskInvert", display="Global Mask Invert", mode=static, default="vec4(0.0)")
uniform vec4 _GlobalMaskInvert;
//@property(name="_GlobalMaskDistance", display="Global Mask Distance", mode=static, default="vec4(0.0)")
uniform vec4 _GlobalMaskDistance;
//@property(name="_GlobalMaskDistanceMin", display="Global Mask Distance Start", mode=static, default="0.0")
uniform float _GlobalMaskDistanceMin;
//@property(name="_GlobalMaskDistanceMax", display="Global Mask Distance End", mode=static, default="25.0")
uniform float _GlobalMaskDistanceMax;

//@property(name="_ColorMask", display="RGBA Color Mask", slot=texture, semantic=mask)
uniform sampler2D _ColorMask;
//@property(name="_ColorMask_ST", display="Color Mask Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _ColorMask_ST;
//@property(name="_ColorMaskUV", display="Color Mask UV", mode=static, default="0")
uniform int _ColorMaskUV;
//@property(name="_ColorMaskColor0", display="Mask Color R", mode=animated, default="vec4(1.0)")
uniform vec4 _ColorMaskColor0;
//@property(name="_ColorMaskColor1", display="Mask Color G", mode=animated, default="vec4(1.0)")
uniform vec4 _ColorMaskColor1;
//@property(name="_ColorMaskColor2", display="Mask Color B", mode=animated, default="vec4(1.0)")
uniform vec4 _ColorMaskColor2;
//@property(name="_ColorMaskColor3", display="Mask Color A", mode=animated, default="vec4(1.0)")
uniform vec4 _ColorMaskColor3;
//@property(name="_ColorMaskEmission", display="Mask Emission", mode=static, default="vec4(0.0)")
uniform vec4 _ColorMaskEmission;
//@property(name="_ColorMaskMetallic", display="Mask Metallic", mode=static, default="vec4(0.0)")
uniform vec4 _ColorMaskMetallic;
//@property(name="_ColorMaskSmoothness", display="Mask Smoothness", mode=static, default="vec4(0.5)")
uniform vec4 _ColorMaskSmoothness;
//@property(name="_ColorMaskNormalStrength", display="Mask Normal Strengths", mode=static, default="vec4(0.0)")
uniform vec4 _ColorMaskNormalStrength;
//@property(name="_ColorMaskBlendModes", display="Mask Blend Modes", mode=static, default="vec4(0.0)")
uniform vec4 _ColorMaskBlendModes;
//@property(name="_ColorMaskThemeIndices", display="Mask Theme Indices", mode=static, default="vec4(0.0)")
uniform vec4 _ColorMaskThemeIndices;
//@property(name="_GlobalMaskModifiers", display="Global Mask Modifiers", mode=animated, default="vec4(0.0)")
uniform vec4 _GlobalMaskModifiers;
#endif

//@category("Poiyomi")
//@subcategory("Lighting")
//@feature(id="poiyomi-lighting-parity", name="Poiyomi Lighting Parity", default=off, cost=high)
//@depends("stylized-shading")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_LIGHTING_PARITY
//@property(name="_LightingForceColor", display="Force Light Color", mode=static, default="0.0")
uniform float _LightingForceColor;
//@property(name="_LightingForcedColor", display="Forced Light Color", mode=animated, default="vec3(1.0)")
uniform vec3 _LightingForcedColor;
//@property(name="_LightingForceDirection", display="Force Light Direction", mode=static, default="0.0")
uniform float _LightingForceDirection;
//@property(name="_LightingForcedDirection", display="Forced Light Direction", mode=animated, default="vec3(0.0, 1.0, 0.0)")
uniform vec3 _LightingForcedDirection;
//@property(name="_LightingDetailShadowMap", display="Detail Shadow", slot=texture, semantic=mask)
uniform sampler2D _LightingDetailShadowMap;
//@property(name="_LightingDetailShadowMap_ST", display="Detail Shadow Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _LightingDetailShadowMap_ST;
//@property(name="_LightingDetailShadowStrength", display="Detail Shadow Strength", mode=static, default="0.0")
uniform float _LightingDetailShadowStrength;
//@property(name="_LightDataAOStrengths", display="RGBA AO Strengths", mode=static, default="vec4(1.0)")
uniform vec4 _LightDataAOStrengths;
//@property(name="_LightingShadowMaskStrengths", display="RGBA Shadow Strengths", mode=static, default="vec4(1.0)")
uniform vec4 _LightingShadowMaskStrengths;

//@property(name="_LightingSDFMap", display="SDF Lighting Map", slot=texture, semantic=mask)
uniform sampler2D _LightingSDFMap;
//@property(name="_LightingSDFMap_ST", display="SDF Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _LightingSDFMap_ST;
//@property(name="_LightingSDFMapUV", display="SDF UV", mode=static, default="0")
uniform int _LightingSDFMapUV;
//@property(name="_LightingSDFDirection", display="SDF Direction", mode=animated, default="vec2(1.0, 0.0)")
uniform vec2 _LightingSDFDirection;
//@property(name="_LightingSDFThreshold", display="SDF Threshold", mode=static, default="0.5")
uniform float _LightingSDFThreshold;
//@property(name="_LightingSDFFeather", display="SDF Feather", mode=static, default="0.05")
uniform float _LightingSDFFeather;
//@property(name="_LightingSkinColor", display="Skin Scatter Tint", mode=static, default="vec3(1.0, 0.72, 0.58)")
uniform vec3 _LightingSkinColor;
//@property(name="_LightingSkinScatter", display="Skin Scatter", mode=static, default="0.35")
uniform float _LightingSkinScatter;
//@property(name="_LightingClothSheen", display="Cloth Sheen", mode=static, default="0.15")
uniform float _LightingClothSheen;
//@property(name="_LightingClothRoughness", display="Cloth Roughness", mode=static, default="0.65")
uniform float _LightingClothRoughness;
//@property(name="_Shadow2Color", display="Second Shade Color", mode=static, default="vec4(0.5, 0.5, 0.5, 1.0)")
uniform vec4 _Shadow2Color;
//@property(name="_Shadow2Border", display="Second Shade Border", mode=static, default="0.25")
uniform float _Shadow2Border;
//@property(name="_Shadow2Blur", display="Second Shade Blur", mode=static, default="0.05")
uniform float _Shadow2Blur;
#endif

//@category("Poiyomi")
//@subcategory("PBR And Reflections")
//@feature(id="poiyomi-pbr-parity", name="Poiyomi PBR And Reflections", default=off, cost=high)
//@depends("poiyomi-lighting-parity", "advanced-specular")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_PBR_PARITY
//@property(name="_Specular2Color", display="Second Specular Color", mode=animated, default="vec4(1.0)")
uniform vec4 _Specular2Color;
//@property(name="_Specular2Strength", display="Second Specular Strength", mode=static, default="0.0")
uniform float _Specular2Strength;
//@property(name="_Specular2Smoothness", display="Second Specular Smoothness", mode=static, default="0.5")
uniform float _Specular2Smoothness;
//@property(name="_Anisotropy", display="Anisotropy", mode=static, default="0.0")
uniform float _Anisotropy;
//@property(name="_AnisotropyRotation", display="Anisotropy Rotation", mode=animated, default="0.0")
uniform float _AnisotropyRotation;
//@property(name="_ClearCoat", display="Clear Coat", mode=static, default="0.0")
uniform float _ClearCoat;
//@property(name="_ClearCoatSmoothness", display="Clear Coat Smoothness", mode=static, default="0.8")
uniform float _ClearCoatSmoothness;
//@property(name="_CubeMap", display="Material Cubemap", slot=texture, semantic=environment)
uniform samplerCube _CubeMap;
//@property(name="_CubeMapColor", display="Cubemap Tint", mode=animated, default="vec4(1.0)")
uniform vec4 _CubeMapColor;
//@property(name="_CubeMapStrength", display="Cubemap Strength", mode=static, default="0.0")
uniform float _CubeMapStrength;
//@property(name="_RimEnviroIntensity", display="Environmental Rim", mode=static, default="0.0")
uniform float _RimEnviroIntensity;
//@property(name="_RimEnviroWidth", display="Environmental Rim Width", mode=static, default="0.45")
uniform float _RimEnviroWidth;
//@property(name="_RimEnviroSharpness", display="Environmental Rim Sharpness", mode=static, default="0.0")
uniform float _RimEnviroSharpness;
//@property(name="_StylizedReflectionMode", display="Stylized Reflection", mode=static, default="0")
uniform int _StylizedReflectionMode;
//@property(name="_BacklightColor", display="Backlight Color", mode=animated, default="vec4(1.0)")
uniform vec4 _BacklightColor;
//@property(name="_BacklightStrength", display="Backlight Strength", mode=static, default="0.0")
uniform float _BacklightStrength;
//@property(name="_BacklightMask", display="Backlight Mask", slot=texture, semantic=mask)
uniform sampler2D _BacklightMask;
#endif

//@category("Poiyomi")
//@subcategory("Repeated Matcaps And Rims")
//@feature(id="poiyomi-matcap-rim-slots", name="Poiyomi Matcap And Rim Slots", default=off, cost=high)
//@depends("poiyomi-masks-themes")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_MATCAP_RIM_SLOTS
//@property(name="_Matcap0Tex", display="Matcap 0", slot=texture)
uniform sampler2D _Matcap0Tex;
//@property(name="_Matcap1Tex", display="Matcap 1", slot=texture)
uniform sampler2D _Matcap1Tex;
//@property(name="_Matcap2Tex", display="Matcap 2", slot=texture)
uniform sampler2D _Matcap2Tex;
//@property(name="_Matcap3Tex", display="Matcap 3", slot=texture)
uniform sampler2D _Matcap3Tex;
//@property(name="_Matcap0Mask", display="Matcap 0 Mask", slot=texture, semantic=mask)
uniform sampler2D _Matcap0Mask;
//@property(name="_Matcap1Mask", display="Matcap 1 Mask", slot=texture, semantic=mask)
uniform sampler2D _Matcap1Mask;
//@property(name="_Matcap2Mask", display="Matcap 2 Mask", slot=texture, semantic=mask)
uniform sampler2D _Matcap2Mask;
//@property(name="_Matcap3Mask", display="Matcap 3 Mask", slot=texture, semantic=mask)
uniform sampler2D _Matcap3Mask;
//@property(name="_MatcapSlotColor0", display="Matcap 0 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _MatcapSlotColor0;
//@property(name="_MatcapSlotColor1", display="Matcap 1 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _MatcapSlotColor1;
//@property(name="_MatcapSlotColor2", display="Matcap 2 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _MatcapSlotColor2;
//@property(name="_MatcapSlotColor3", display="Matcap 3 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _MatcapSlotColor3;
//@property(name="_MatcapSlotParams0", display="Matcap 0 Params", mode=static, default="vec4(0.0)")
uniform vec4 _MatcapSlotParams0;
//@property(name="_MatcapSlotParams1", display="Matcap 1 Params", mode=static, default="vec4(0.0)")
uniform vec4 _MatcapSlotParams1;
//@property(name="_MatcapSlotParams2", display="Matcap 2 Params", mode=static, default="vec4(0.0)")
uniform vec4 _MatcapSlotParams2;
//@property(name="_MatcapSlotParams3", display="Matcap 3 Params", mode=static, default="vec4(0.0)")
uniform vec4 _MatcapSlotParams3;

//@property(name="_Rim2Color", display="Rim 2 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _Rim2Color;
//@property(name="_Rim2Mask", display="Rim 2 Mask", slot=texture, semantic=mask)
uniform sampler2D _Rim2Mask;
//@property(name="_Rim2Mask_ST", display="Rim 2 Mask Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _Rim2Mask_ST;
//@property(name="_Rim2Params", display="Rim 2 Params", mode=static, default="vec4(0.5, 1.0, 0.0, 1.0)")
uniform vec4 _Rim2Params;
//@property(name="_DepthRimColor", display="Depth Rim Color", mode=animated, default="vec4(1.0)")
uniform vec4 _DepthRimColor;
//@property(name="_DepthRimStrength", display="Depth Rim Strength", mode=static, default="0.0")
uniform float _DepthRimStrength;
//@property(name="_DepthRimWidth", display="Depth Rim Width", mode=static, default="1.0")
uniform float _DepthRimWidth;
#endif

//@category("Poiyomi")
//@subcategory("Decals")
//@feature(id="poiyomi-decals", name="Poiyomi Decals 0-3", default=off, cost=high)
//@depends("poiyomi-masks-themes", "render-time")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_DECALS
//@property(name="_DecalMask", display="Decal RGBA Mask", slot=texture, semantic=mask)
uniform sampler2D _DecalMask;
//@property(name="_DecalMask_ST", display="Decal Mask Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _DecalMask_ST;
//@property(name="_DecalMaskPan", display="Decal Mask Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _DecalMaskPan;
//@property(name="_DecalMaskUV", display="Decal Mask UV", mode=static, default="0")
uniform int _DecalMaskUV;
//@property(name="_DecalTexture", display="Decal 0", slot=texture)
uniform sampler2D _DecalTexture;
//@property(name="_DecalTexture1", display="Decal 1", slot=texture)
uniform sampler2D _DecalTexture1;
//@property(name="_DecalTexture2", display="Decal 2", slot=texture)
uniform sampler2D _DecalTexture2;
//@property(name="_DecalTexture3", display="Decal 3", slot=texture)
uniform sampler2D _DecalTexture3;
//@property(name="_DecalColor", display="Decal 0 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _DecalColor;
//@property(name="_DecalColor1", display="Decal 1 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _DecalColor1;
//@property(name="_DecalColor2", display="Decal 2 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _DecalColor2;
//@property(name="_DecalColor3", display="Decal 3 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _DecalColor3;
//@property(name="_DecalPosition", display="Decal 0 Position", mode=animated, default="vec2(0.5)")
uniform vec2 _DecalPosition;
//@property(name="_DecalPosition1", display="Decal 1 Position", mode=animated, default="vec2(0.5)")
uniform vec2 _DecalPosition1;
//@property(name="_DecalPosition2", display="Decal 2 Position", mode=animated, default="vec2(0.5)")
uniform vec2 _DecalPosition2;
//@property(name="_DecalPosition3", display="Decal 3 Position", mode=animated, default="vec2(0.5)")
uniform vec2 _DecalPosition3;
//@property(name="_DecalScale", display="Decal 0 Scale", mode=animated, default="vec2(1.0)")
uniform vec2 _DecalScale;
//@property(name="_DecalScale1", display="Decal 1 Scale", mode=animated, default="vec2(1.0)")
uniform vec2 _DecalScale1;
//@property(name="_DecalScale2", display="Decal 2 Scale", mode=animated, default="vec2(1.0)")
uniform vec2 _DecalScale2;
//@property(name="_DecalScale3", display="Decal 3 Scale", mode=animated, default="vec2(1.0)")
uniform vec2 _DecalScale3;
//@property(name="_DecalTexturePan", display="Decal 0 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _DecalTexturePan;
//@property(name="_DecalTexturePan1", display="Decal 1 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _DecalTexturePan1;
//@property(name="_DecalTexturePan2", display="Decal 2 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _DecalTexturePan2;
//@property(name="_DecalTexturePan3", display="Decal 3 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _DecalTexturePan3;
//@property(name="_DecalRotation", display="Decal 0 Rotation", mode=animated, default="0.0")
uniform float _DecalRotation;
//@property(name="_DecalRotation1", display="Decal 1 Rotation", mode=animated, default="0.0")
uniform float _DecalRotation1;
//@property(name="_DecalRotation2", display="Decal 2 Rotation", mode=animated, default="0.0")
uniform float _DecalRotation2;
//@property(name="_DecalRotation3", display="Decal 3 Rotation", mode=animated, default="0.0")
uniform float _DecalRotation3;
//@property(name="_DecalRotationSpeed", display="Decal 0 Rotation Speed", mode=animated, default="0.0")
uniform float _DecalRotationSpeed;
//@property(name="_DecalRotationSpeed1", display="Decal 1 Rotation Speed", mode=animated, default="0.0")
uniform float _DecalRotationSpeed1;
//@property(name="_DecalRotationSpeed2", display="Decal 2 Rotation Speed", mode=animated, default="0.0")
uniform float _DecalRotationSpeed2;
//@property(name="_DecalRotationSpeed3", display="Decal 3 Rotation Speed", mode=animated, default="0.0")
uniform float _DecalRotationSpeed3;
//@property(name="_DecalBlendParams0", display="Decal 0 Blend", mode=static, default="vec4(0.0, 1.0, 1.0, 0.0)")
uniform vec4 _DecalBlendParams0;
//@property(name="_DecalBlendParams1", display="Decal 1 Blend", mode=static, default="vec4(0.0, 1.0, 1.0, 0.0)")
uniform vec4 _DecalBlendParams1;
//@property(name="_DecalBlendParams2", display="Decal 2 Blend", mode=static, default="vec4(0.0, 1.0, 1.0, 0.0)")
uniform vec4 _DecalBlendParams2;
//@property(name="_DecalBlendParams3", display="Decal 3 Blend", mode=static, default="vec4(0.0, 1.0, 1.0, 0.0)")
uniform vec4 _DecalBlendParams3;
//@property(name="_DecalUvMode0", display="Decal 0 UV", mode=static, default="0")
uniform int _DecalUvMode0;
//@property(name="_DecalUvMode1", display="Decal 1 UV", mode=static, default="0")
uniform int _DecalUvMode1;
//@property(name="_DecalUvMode2", display="Decal 2 UV", mode=static, default="0")
uniform int _DecalUvMode2;
//@property(name="_DecalUvMode3", display="Decal 3 UV", mode=static, default="0")
uniform int _DecalUvMode3;
//@property(name="_DecalSlotModifiers0", display="Decal 0 Visibility", mode=static, default="vec4(0.0)")
uniform vec4 _DecalSlotModifiers0;
//@property(name="_DecalSlotModifiers1", display="Decal 1 Visibility", mode=static, default="vec4(0.0)")
uniform vec4 _DecalSlotModifiers1;
//@property(name="_DecalSlotModifiers2", display="Decal 2 Visibility", mode=static, default="vec4(0.0)")
uniform vec4 _DecalSlotModifiers2;
//@property(name="_DecalSlotModifiers3", display="Decal 3 Visibility", mode=static, default="vec4(0.0)")
uniform vec4 _DecalSlotModifiers3;
//@property(name="_DecalSlotFx0", display="Decal 0 Effects", mode=animated, default="vec4(0.0)")
uniform vec4 _DecalSlotFx0;
//@property(name="_DecalSlotFx1", display="Decal 1 Effects", mode=animated, default="vec4(0.0)")
uniform vec4 _DecalSlotFx1;
//@property(name="_DecalSlotFx2", display="Decal 2 Effects", mode=animated, default="vec4(0.0)")
uniform vec4 _DecalSlotFx2;
//@property(name="_DecalSlotFx3", display="Decal 3 Effects", mode=animated, default="vec4(0.0)")
uniform vec4 _DecalSlotFx3;
#endif

//@category("Poiyomi")
//@subcategory("Emissions")
//@feature(id="poiyomi-emission-slots", name="Poiyomi Emissions 0-3", default=off, cost=high)
//@depends("poiyomi-masks-themes", "render-time")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_EMISSION_SLOTS
//@property(name="_Emission0Tex", display="Emission 0", slot=texture, semantic=emission)
uniform sampler2D _Emission0Tex;
//@property(name="_Emission1Tex", display="Emission 1", slot=texture, semantic=emission)
uniform sampler2D _Emission1Tex;
//@property(name="_Emission2Tex", display="Emission 2", slot=texture, semantic=emission)
uniform sampler2D _Emission2Tex;
//@property(name="_Emission3Tex", display="Emission 3", slot=texture, semantic=emission)
uniform sampler2D _Emission3Tex;
//@property(name="_Emission0Mask", display="Emission 0 Mask", slot=texture, semantic=mask)
uniform sampler2D _Emission0Mask;
//@property(name="_Emission1Mask", display="Emission 1 Mask", slot=texture, semantic=mask)
uniform sampler2D _Emission1Mask;
//@property(name="_Emission2Mask", display="Emission 2 Mask", slot=texture, semantic=mask)
uniform sampler2D _Emission2Mask;
//@property(name="_Emission3Mask", display="Emission 3 Mask", slot=texture, semantic=mask)
uniform sampler2D _Emission3Mask;
//@property(name="_EmissionSlotColor0", display="Emission 0 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _EmissionSlotColor0;
//@property(name="_EmissionSlotColor1", display="Emission 1 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _EmissionSlotColor1;
//@property(name="_EmissionSlotColor2", display="Emission 2 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _EmissionSlotColor2;
//@property(name="_EmissionSlotColor3", display="Emission 3 Color", mode=animated, default="vec4(1.0)")
uniform vec4 _EmissionSlotColor3;
//@property(name="_EmissionSlotParams0", display="Emission 0 Params", mode=animated, default="vec4(0.0)")
uniform vec4 _EmissionSlotParams0;
//@property(name="_EmissionSlotParams1", display="Emission 1 Params", mode=animated, default="vec4(0.0)")
uniform vec4 _EmissionSlotParams1;
//@property(name="_EmissionSlotParams2", display="Emission 2 Params", mode=animated, default="vec4(0.0)")
uniform vec4 _EmissionSlotParams2;
//@property(name="_EmissionSlotParams3", display="Emission 3 Params", mode=animated, default="vec4(0.0)")
uniform vec4 _EmissionSlotParams3;
//@property(name="_EmissionSlotPan0", display="Emission 0 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _EmissionSlotPan0;
//@property(name="_EmissionSlotPan1", display="Emission 1 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _EmissionSlotPan1;
//@property(name="_EmissionSlotPan2", display="Emission 2 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _EmissionSlotPan2;
//@property(name="_EmissionSlotPan3", display="Emission 3 Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _EmissionSlotPan3;
//@property(name="_EmissionSlotUv0", display="Emission 0 UV", mode=static, default="0")
uniform int _EmissionSlotUv0;
//@property(name="_EmissionSlotUv1", display="Emission 1 UV", mode=static, default="0")
uniform int _EmissionSlotUv1;
//@property(name="_EmissionSlotUv2", display="Emission 2 UV", mode=static, default="0")
uniform int _EmissionSlotUv2;
//@property(name="_EmissionSlotUv3", display="Emission 3 UV", mode=static, default="0")
uniform int _EmissionSlotUv3;
//@property(name="_EmissionSlotModifiers0", display="Emission 0 Modifiers", mode=static, default="vec4(0.0)")
uniform vec4 _EmissionSlotModifiers0;
//@property(name="_EmissionSlotModifiers1", display="Emission 1 Modifiers", mode=static, default="vec4(0.0)")
uniform vec4 _EmissionSlotModifiers1;
//@property(name="_EmissionSlotModifiers2", display="Emission 2 Modifiers", mode=static, default="vec4(0.0)")
uniform vec4 _EmissionSlotModifiers2;
//@property(name="_EmissionSlotModifiers3", display="Emission 3 Modifiers", mode=static, default="vec4(0.0)")
uniform vec4 _EmissionSlotModifiers3;
#endif

//@category("Poiyomi")
//@subcategory("Flipbook")
//@feature(id="poiyomi-flipbook-array", name="Poiyomi Texture Array Flipbook", default=off, cost=medium)
//@depends("poiyomi-masks-themes", "render-time")
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_FLIPBOOK_ARRAY
//@property(name="_FlipbookTexArray", display="Flipbook Texture Array", slot=texture)
uniform sampler2DArray _FlipbookTexArray;
//@property(name="_FlipbookMask", display="Flipbook Mask", slot=texture, semantic=mask)
uniform sampler2D _FlipbookMask;
//@property(name="_FlipbookColor", display="Flipbook Color", mode=animated, default="vec4(1.0)")
uniform vec4 _FlipbookColor;
//@property(name="_FlipbookTexArrayUV", display="Flipbook UV", mode=static, default="0")
uniform int _FlipbookTexArrayUV;
//@property(name="_FlipbookTexArrayPan", display="Flipbook Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _FlipbookTexArrayPan;
//@property(name="_FlipbookScaleOffset", display="Flipbook Scale Offset", mode=animated, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _FlipbookScaleOffset;
//@property(name="_FlipbookFPS", display="Flipbook FPS", mode=animated, default="30.0")
uniform float _FlipbookFPS;
//@property(name="_FlipbookFrameOffset", display="Flipbook Frame Offset", mode=animated, default="0.0")
uniform float _FlipbookFrameOffset;
//@property(name="_FlipbookManualFrameControl", display="Manual Frame", mode=static, default="0.0")
uniform float _FlipbookManualFrameControl;
//@property(name="_FlipbookCurrentFrame", display="Current Frame", mode=animated, default="0.0")
uniform float _FlipbookCurrentFrame;
//@property(name="_FlipbookStartFrame", display="Start Frame", mode=static, default="0.0")
uniform float _FlipbookStartFrame;
//@property(name="_FlipbookEndFrame", display="End Frame", mode=static, default="0.0")
uniform float _FlipbookEndFrame;
//@property(name="_FlipbookCrossfadeEnabled", display="Crossfade", mode=static, default="0.0")
uniform float _FlipbookCrossfadeEnabled;
//@property(name="_FlipbookCrossfadeRange", display="Crossfade Range", mode=static, default="vec2(0.75, 1.0)")
uniform vec2 _FlipbookCrossfadeRange;
//@property(name="_FlipbookBlendType", display="Flipbook Blend", mode=static, default="0")
uniform int _FlipbookBlendType;
//@property(name="_FlipbookReplace", display="Flipbook Replace", mode=static, default="1.0")
uniform float _FlipbookReplace;
//@property(name="_FlipbookEmissionStrength", display="Flipbook Emission", mode=static, default="0.0")
uniform float _FlipbookEmissionStrength;
//@property(name="_FlipbookAlphaControlsFinalAlpha", display="Flipbook Alpha Mode", mode=static, default="0")
uniform int _FlipbookAlphaControlsFinalAlpha;
//@property(name="_FlipbookHueShiftEnabled", display="Flipbook Hue", mode=static, default="0.0")
uniform float _FlipbookHueShiftEnabled;
//@property(name="_FlipbookHueShift", display="Flipbook Hue Shift", mode=animated, default="0.0")
uniform float _FlipbookHueShift;
//@property(name="_FlipbookHueShiftSpeed", display="Flipbook Hue Speed", mode=animated, default="0.0")
uniform float _FlipbookHueShiftSpeed;
#endif

#endif
