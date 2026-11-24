// Uber Shader - Uniform Declarations
// Uses engine-standard uniform names for compatibility

#ifndef TOON_UNIFORMS_GLSL
#define TOON_UNIFORMS_GLSL
//@category("Surface")
//@subcategory("Normals")
//@property(name="_BackfaceNormalControl", display="Backface Normal Control", mode=static, range=[0,1], toggle=true)
//@tooltip("When on, Flip Backface Normals selects whether back faces flip the normal and the tangent frame. When off, back faces flip only the normal.")
uniform int _BackfaceNormalControl;
//@property(name="_FlipBackfaceNormals", display="Flip Backface Normals", mode=static, range=[0,1], toggle=true, visible="_BackfaceNormalControl > 0")
uniform int _FlipBackfaceNormals;
//@category("Lighting")
//@subcategory("Light Model")
//@property(name="_LightModel", display="Light Model", mode=static, enum="0:Stylized|1:Toon")
//@tooltip("Stylized uses the engine light response. Toon uses the toon light pipeline with the settings below.")
uniform int _LightModel;
//@property(name="_WorldAmbientOcclusionStrength", display="World AO Strength", mode=static, range=[0,1])
uniform float _WorldAmbientOcclusionStrength;
//@property(name="_ToonLightSettings", display="Toon Light", mode=static, components="Unlit Intensity|Cast Shadows|Light Multiplier|Light Addition", visible="_LightModel > 0")
uniform vec4 _ToonLightSettings;
//@property(name="_AdditiveLightSettings", display="Additive Lights", mode=static, components="Enabled:toggle|Ignore Directional:toggle|Limit Brightness:toggle|Brightness Limit", visible="_LightModel > 0")
uniform vec4 _AdditiveLightSettings;
//@property(name="_AdditiveLightColor", display="Additive Light Color", mode=static, components="Monochromatic|Passthrough|Type (0 Lambert, 1 Gradient, 2 Ramp, 3 Match Base)|Cast Shadows", visible="_LightModel > 0")
uniform vec4 _AdditiveLightColor;
//@property(name="_AdditiveLightGradient", display="Additive Gradient", mode=static, components="Start|End", visible="_LightModel > 0")
uniform vec4 _AdditiveLightGradient;
//@property(name="_WrappedLightColor", display="Wrapped Light Color", mode=static, visible="_LightModel > 0")
uniform vec4 _WrappedLightColor;

// ============================================
// Transform Matrices (Provided by engine - matches EEngineUniform names)
// Engine sets vertex-stage camera uniforms with a "_VTX" suffix.
// ============================================
uniform mat4 ModelMatrix;
uniform vec4 u_SourceFlatDirection;
uniform mat4 ViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;
uniform mat4 ViewProjectionMatrix_VTX;

// Camera (matches EEngineUniform.CameraPosition)
uniform vec3 CameraPosition;

// The inverse-hull outline expands vertices in screen space. Fragment-stage
// lighting snippets declare these separately, so keep this declaration scoped
// to the vertex variants that consume u_ScreenParams.
#if !defined(XRENGINE_UBER_MVP_FRAGMENT) || defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING)
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif

// Time for animations (provided by engine when RenderTime is requested)
//@feature(id="render-time", name="Render Time", default=on, cost=none)
#ifndef XRENGINE_UBER_DISABLE_RENDER_TIME
uniform float RenderTime;
#endif

// ============================================
// Convenience macros for compatibility with u_ prefix code
// ============================================
#define u_ModelMatrix ModelMatrix
#define u_ViewMatrix ViewMatrix_VTX
#define u_ProjectionMatrix ProjMatrix_VTX
#define u_ModelViewMatrix (ViewMatrix_VTX * ModelMatrix)
#define u_ModelViewProjectionMatrix (ViewProjectionMatrix_VTX * ModelMatrix)
#define u_CameraPosition CameraPosition
#ifndef XRENGINE_UBER_DISABLE_RENDER_TIME
#define u_Time RenderTime
#else
#define u_Time 0.0
#endif
#define u_ScreenParams vec4(ScreenWidth, ScreenHeight, 1.0 + 1.0/ScreenWidth, 1.0 + 1.0/ScreenHeight)

// Forward-lighting uniforms, shadows, Forward+, and probe bindings are declared
// by the shared engine snippets included by fragment-stage Uber shaders.

// ============================================
// Adjoint (classical adjugate) of a mat4's upper-left 3x3
// ============================================
// Produces a direction-transform matrix equivalent to inverse-transpose up to
// a non-negative scalar (the determinant). Because the vertex stage follows
// every adjoint-transform with normalize(), that scalar drops out \u2014 giving the
// same visual result as transpose(inverse(mat3(m))) at a fraction of the cost
// (9 muls + 6 subs vs. a full 3x3 inverse + transpose).
//
// This matches the engine's DefaultVertexShaderGenerator/MeshDeformVertex-
// ShaderGenerator paths which already use adjoint(ModelMatrix). Safe for the
// non-uniform-scale case that motivates inverse-transpose in the first place.
mat3 adjoint(mat4 m) {
    return mat3(
        m[1].y * m[2].z - m[1].z * m[2].y,
        m[1].z * m[2].x - m[1].x * m[2].z,
        m[1].x * m[2].y - m[1].y * m[2].x,
        m[0].z * m[2].y - m[0].y * m[2].z,
        m[0].x * m[2].z - m[0].z * m[2].x,
        m[0].y * m[2].x - m[0].x * m[2].y,
        m[0].y * m[1].z - m[0].z * m[1].y,
        m[0].z * m[1].x - m[0].x * m[1].z,
        m[0].x * m[1].y - m[0].y * m[1].x);
}
#define u_NormalMatrix adjoint(u_ModelMatrix)

// ============================================
// Main Texture Properties
// ============================================
//@category("Surface", order=0)
//@property(name="_MainTex", display="Albedo Map", slot=texture, indirect=texture, semantic=albedo)
//@tooltip("Primary base-color texture sampled for the material surface.")
uniform sampler2D _MainTex;
uniform vec4 _MainTex_ST;           // xy: tiling, zw: offset
uniform vec2 _MainTexPan;
uniform int _MainTexUV;

//@property(name="_Color", display="Tint", mode=static, indirect=field, semantic=baseColorOpacity, default="vec4(1.0, 1.0, 1.0, 1.0)")
//@tooltip("Color tint multiplied into the sampled albedo.")
uniform vec4 _Color;                // Main color tint
//@property(name="_ColorThemeIndex", display="Main Color Theme", mode=static, default="0")
uniform int _ColorThemeIndex;

// ============================================
// Normal Map
// ============================================
//@category("Surface")
//@feature(id="normal-map", name="Normal Map", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_NORMAL_MAP
//@property(name="_BumpMap", display="Normal Map", slot=texture, indirect=texture, semantic=normal)
//@tooltip("Tangent-space normal map used to perturb surface lighting.")
uniform sampler2D _BumpMap;
uniform vec4 _BumpMap_ST;
uniform vec2 _BumpMapPan;
uniform int _BumpMapUV;
//@property(name="_BumpScale", display="Normal Strength", mode=static, range=[0,2], indirect=static, semantic=normalStrength, default="1.0")
uniform float _BumpScale;
//@property(name="_BumpMapFlipGreen", display="Normal Green Inversion", mode=static, default="0")
uniform int _BumpMapFlipGreen;
uniform int NormalMapMode;
uniform float HeightMapScale;
#endif

// ============================================
// Alpha / Transparency
// ============================================
//@category("Transparency", order=10)
//@feature(id="alpha-masks", name="Alpha Masks", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_ALPHA_MASKS
//@property(name="_AlphaMask", display="Alpha Mask", slot=texture)
//@tooltip("Texture that remaps surface alpha before transparency or cutoff decisions.")
uniform sampler2D _AlphaMask;
uniform vec4 _AlphaMask_ST;
uniform vec2 _AlphaMaskPan;
uniform int _AlphaMaskUV;
//@property(name="_MainAlphaMaskMode", display="Mask Mode", mode=static, enum="0:Off|1:Replace|2:Multiply|3:Add|4:Subtract")
uniform int _MainAlphaMaskMode;     // 0: Off, 1: Replace, 2: Multiply, 3: Add, 4: Subtract
//@property(name="_AlphaMaskBlendStrength", display="Mask Blend", mode=static, range=[0,1])
uniform float _AlphaMaskBlendStrength;
//@property(name="_AlphaMaskValue", display="Mask Value", mode=static, range=[0,1])
uniform float _AlphaMaskValue;
//@property(name="_AlphaMaskInvert", display="Invert Mask", mode=static, range=[0,1], toggle=true)
uniform float _AlphaMaskInvert;
#endif

//@property(name="_Cutoff", display="Alpha Cutoff", mode=static, range=[0,1])
//@tooltip("Threshold below which fragments are discarded in the cutout, transparent cutout, and fade presets, or when Clip Alpha In All Modes is on.")
uniform float _Cutoff;              // Alpha cutoff threshold
//@property(name="_AlphaClipAllModes", display="Clip Alpha In All Modes", mode=static, default="0.0")
uniform float _AlphaClipAllModes;
uniform int _Mode;                  // Shader blend mode. The render preset sets it.
uniform float _AlphaForceOpaque;
uniform float _AlphaMod;
//@property(name="_AlphaPremultiply", display="Premultiply Alpha", mode=static, range=[0,1], toggle=true, default="0.0")
//@tooltip("Multiplies the color by alpha before blending. The Transparent preset turns it on.")
uniform float _AlphaPremultiply;

//@feature(id="source-scene-color", name="Source Scene Color", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_SOURCE_SCENE_COLOR
// The importer sets the scene-color values from the source GrabPass. The inspector does not show them.
//@property(name="_SceneColorTexture", slot=texture, hidden=true)
uniform sampler2DArray _SceneColorTexture;
//@property(name="_SceneColorMode", mode=static, hidden=true)
uniform float _SceneColorMode;
//@property(name="_SceneGrabColor", mode=static, hidden=true)
uniform vec4 _SceneGrabColor;
//@property(name="_SceneGrabBrightness", mode=static, hidden=true)
uniform float _SceneGrabBrightness;
//@property(name="_SceneGrabAddBrightness", mode=static, hidden=true)
uniform float _SceneGrabAddBrightness;
//@property(name="_SceneGrabBlendFactor", mode=static, hidden=true)
uniform float _SceneGrabBlendFactor;
//@property(name="_SceneGrabUseAlpha", mode=static, hidden=true)
uniform float _SceneGrabUseAlpha;
// Source shader blend factor-table indices (0 to 10) for the surface and scene-color terms.
//@property(name="_SceneGrabSrcBlend", mode=static, hidden=true)
uniform float _SceneGrabSrcBlend;
//@property(name="_SceneGrabDstBlend", mode=static, hidden=true)
uniform float _SceneGrabDstBlend;
// Source toon GrabPass refraction: on/off, index of refraction (0 to 3), chromatic
// aberration (0 to 1), and the facing falloff power (0 disables the falloff).
uniform float _SceneRefractionEnabled;
//@property(name="_SceneRefractionIndex", mode=static, hidden=true)
uniform float _SceneRefractionIndex;
//@property(name="_SceneRefractionChromaticAberration", mode=static, hidden=true)
uniform float _SceneRefractionChromaticAberration;
//@property(name="_SceneRefractionFresnelPower", mode=static, hidden=true)
uniform float _SceneRefractionFresnelPower;
#endif

// ============================================
// Color Adjustments
// ============================================
//@category("Surface")
//@feature(id="color-adjustments", name="Color Adjustments", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_COLOR_ADJUSTMENTS
//@property(name="_MainColorAdjustTexture", display="Adjustment Map", slot=texture)
uniform sampler2D _MainColorAdjustTexture;
uniform vec4 _MainColorAdjustTexture_ST;
uniform vec2 _MainColorAdjustTexturePan;
uniform int _MainColorAdjustTextureUV;
//@property(name="_Saturation", display="Saturation", mode=static, range=[-1,10])
uniform float _Saturation;
//@property(name="_MainBrightness", display="Brightness", mode=static, range=[-1,2])
uniform float _MainBrightness;
//@property(name="_MainHueShiftToggle", display="Hue Shift", mode=static, range=[0,1], toggle=true)
uniform float _MainHueShiftToggle;
//@property(name="_MainHueShift", display="Hue Shift Amount", mode=static, range=[0,1], visible="_MainHueShiftToggle > 0")
uniform float _MainHueShift;
//@property(name="_MainHueShiftSpeed", display="Hue Shift Speed", mode=animated, range=[-10,10], visible="_MainHueShiftToggle > 0")
uniform float _MainHueShiftSpeed;
//@property(name="_MainHueShiftColorSpace", display="Hue Space", mode=static, enum="0:OKLab|1:HSV", visible="_MainHueShiftToggle > 0")
uniform int _MainHueShiftColorSpace;    // 0: OKLab, 1: HSV
//@property(name="_MainHueShiftReplace", display="Hue Replace", mode=static, range=[0,1], toggle=true, visible="_MainHueShiftToggle > 0")
uniform float _MainHueShiftReplace;
//@property(name="_MainHueShiftSelectOrShift", display="Hue Select Or Shift", mode=static, enum="0:Select|1:Shift", default="1", visible="_MainHueShiftToggle > 0")
uniform int _MainHueShiftSelectOrShift;
//@property(name="_MainGamma", display="Gamma", mode=static, range=[0.01,5], default="1.0")
uniform float _MainGamma;
//@property(name="_MainChromatize", display="Chromatize", mode=static, range=[-1,10], default="0.0")
uniform float _MainChromatize;
//@property(name="_MainColorAdjustClamp", display="Clamp Adjusted Color", mode=static, range=[0,1], toggle=true, default="1.0")
uniform float _MainColorAdjustClamp;
//@property(name="_ColorGradingToggle", display="Gradation Map", mode=static, range=[0,1], toggle=true)
uniform float _ColorGradingToggle;
//@property(name="_MainGradationTex", display="Gradation Map", slot=texture, visible="_ColorGradingToggle > 0")
uniform sampler2D _MainGradationTex;
//@property(name="_MainGradationStrength", display="Gradation Strength", mode=static, range=[0,1], visible="_ColorGradingToggle > 0")
uniform float _MainGradationStrength;
//@property(name="_MainGradationMaskChannel", display="Gradation Mask Channel", mode=static, enum="0:None|1:R|2:G|3:B|4:A", visible="_ColorGradingToggle > 0")
uniform int _MainGradationMaskChannel;
//@property(name="_MainTintColor", display="Tint Color", mode=static, default="vec4(1.0, 1.0, 1.0, 0.0)")
uniform vec4 _MainTintColor;
//@property(name="_MainTintTexture", display="Tint Texture", slot=texture)
uniform sampler2D _MainTintTexture;
uniform vec4 _MainTintTexture_ST;
uniform vec2 _MainTintTexturePan;
uniform int _MainTintTextureUV;
//@property(name="_MainHueGlobalMask", display="Hue Global Mask", mode=static, range=[0,16])
uniform int _MainHueGlobalMask;
//@property(name="_MainHueGlobalMaskBlendType", display="Hue Global Mask Blend", mode=static, default="2")
uniform int _MainHueGlobalMaskBlendType;
//@property(name="_MainSaturationGlobalMask", display="Saturation Global Mask", mode=static, range=[0,16])
uniform int _MainSaturationGlobalMask;
//@property(name="_MainSaturationGlobalMaskBlendType", display="Saturation Global Mask Blend", mode=static, default="2")
uniform int _MainSaturationGlobalMaskBlendType;
//@property(name="_MainBrightnessGlobalMask", display="Brightness Global Mask", mode=static, range=[0,16])
uniform int _MainBrightnessGlobalMask;
//@property(name="_MainBrightnessGlobalMaskBlendType", display="Brightness Global Mask Blend", mode=static, default="2")
uniform int _MainBrightnessGlobalMaskBlendType;
//@property(name="_MainGammaGlobalMask", display="Gamma Global Mask", mode=static, range=[0,16])
uniform int _MainGammaGlobalMask;
//@property(name="_MainGammaGlobalMaskBlendType", display="Gamma Global Mask Blend", mode=static, default="2")
uniform int _MainGammaGlobalMaskBlendType;
#endif

// ============================================
// Shading / Lighting
// ============================================
//@category("Lighting", order=20)
//@feature(id="stylized-shading", name="Stylized Lighting", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
//@property(name="_LightingMode", display="Lighting Mode", mode=static, enum="0:Ramp|1:Multilayer|2:Wrapped|3:Skin|4:ShadeMap|5:Flat|6:Realistic|7:Cloth|8:SDF")
uniform int _LightingMode;          // 0: Ramp, 1: Multilayer, 2: Wrapped, 3: Skin, 4: ShadeMap, 5: Flat, 6: Realistic, 7: Cloth, 8: SDF

// Light Data options
uniform int _LightingColorMode;
//@property(name="_LightingMapMode", display="Light Map Mode", mode=static, enum="0:Half Lambert|1:Clamped Half Lambert|2:Lambert|3:Shadow Only")
//@tooltip("Selects how NdotL is remapped into the light-map value used by stylized lighting modes.")
uniform int _LightingMapMode;
uniform int _LightingDirectionMode;
uniform float _LightingCapEnabled;
//@property(name="_LightingCap", display="Light Cap", mode=static, range=[0,4])
uniform float _LightingCap;
//@property(name="_LightingMinLightBrightness", display="Minimum Light", mode=static, range=[0,1])
uniform float _LightingMinLightBrightness;
//@property(name="_LightingMonochromatic", display="Monochromatic", mode=static, range=[0,1], toggle=true)
uniform float _LightingMonochromatic;
//@property(name="_LightingIndirectUsesNormals", display="Indirect Uses Normals", mode=static, range=[0,1], toggle=true)
uniform float _LightingIndirectUsesNormals;

// Shadow options
//@property(name="_LightingShadowColor", display="Shadow Tint", mode=static)
uniform vec3 _LightingShadowColor;
//@property(name="_ShadowStrength", display="Shadow Strength", mode=static, range=[0,1])
uniform float _ShadowStrength;
//@property(name="_LightingIgnoreAmbientColor", display="Ignore Ambient Tint", mode=static, range=[0,1], toggle=true)
uniform float _LightingIgnoreAmbientColor;

// Texture Ramp shading (Mode 0)
//@property(name="_ToonRamp", display="Ramp Texture", slot=texture)
uniform sampler2D _ToonRamp;
uniform vec4 _ToonRamp_ST;
uniform vec2 _ToonRampPan;
uniform int _ToonRampUV;
//@property(name="_ShadowOffset", display="Shadow Offset", mode=static, range=[-1,1])
uniform float _ShadowOffset;

// Flat shading (Mode 5)
//@property(name="_ForceFlatRampedLightmap", display="Binary Flat Lightmap", mode=static, range=[0,1], toggle=true)
//@tooltip("When flat lighting mode is active, forces a hard 0.5 threshold instead of using the continuous remapped light value.")
uniform float _ForceFlatRampedLightmap;

// Multilayer Math (Mode 1)
//@property(name="_ShadowColorTex", display="Shadow Color Map", slot=texture)
uniform sampler2D _ShadowColorTex;
uniform vec4 _ShadowColorTex_ST;
uniform vec2 _ShadowColorTexPan;
uniform int _ShadowColorTexUV;
//@property(name="_Shadow2ndColorTex", display="Second Shadow Color Map", slot=texture, visible="_LightingMode == 1")
uniform sampler2D _Shadow2ndColorTex;
uniform vec4 _Shadow2ndColorTex_ST;
uniform vec2 _Shadow2ndColorTexPan;
uniform int _Shadow2ndColorTexUV;
//@property(name="_Shadow3rdColorTex", display="Third Shadow Color Map", slot=texture, visible="_LightingMode == 1")
uniform sampler2D _Shadow3rdColorTex;
uniform vec4 _Shadow3rdColorTex_ST;
uniform vec2 _Shadow3rdColorTexPan;
uniform int _Shadow3rdColorTexUV;
//@property(name="_MultilayerMathBlurMap", display="Border Blur Map", slot=texture, visible="_LightingMode == 1")
uniform sampler2D _MultilayerMathBlurMap;
uniform vec4 _MultilayerMathBlurMap_ST;
uniform vec2 _MultilayerMathBlurMapPan;
uniform int _MultilayerMathBlurMapUV;
//@property(name="_ShadowStrengthMask", display="Shadow Strength Mask", slot=texture, visible="_LightingMode == 1")
uniform sampler2D _ShadowStrengthMask;
uniform vec4 _ShadowStrengthMask_ST;
uniform vec2 _ShadowStrengthMaskPan;
uniform int _ShadowStrengthMaskUV;
//@property(name="_ShadowBorderMask", display="Shadow Border Mask", slot=texture, visible="_LightingMode == 1")
uniform sampler2D _ShadowBorderMask;
// The border mask with trilinear filtering. The importer binds the same image as _ShadowBorderMask.
//@property(name="_ShadowBorderMaskTrilinear", slot=texture, hidden=true)
uniform sampler2D _ShadowBorderMaskTrilinear;
uniform vec4 _ShadowBorderMask_ST;
uniform vec2 _ShadowBorderMaskPan;
uniform int _ShadowBorderMaskUV;
//@property(name="_Shadow2ndColor", display="Second Shadow Color", mode=static, visible="_LightingMode == 1")
uniform vec4 _Shadow2ndColor;
//@property(name="_Shadow3rdColor", display="Third Shadow Color", mode=static, visible="_LightingMode == 1")
uniform vec4 _Shadow3rdColor;
//@property(name="_ShadowBorderColor", display="Border Color", mode=static, visible="_LightingMode == 1")
uniform vec4 _ShadowBorderColor;
//@property(name="_MultilayerBorders", display="Layer Borders", mode=static, components="First|Second|Third|Border Range", visible="_LightingMode == 1")
uniform vec4 _MultilayerBorders;
//@property(name="_MultilayerBlurs", display="Layer Blurs", mode=static, components="First|Second|Third|Non-Linear", visible="_LightingMode == 1")
uniform vec4 _MultilayerBlurs;
//@property(name="_MultilayerReceive", display="Layer Shadow Receive", mode=static, components="First|Second|Third|Main Strength", visible="_LightingMode == 1")
uniform vec4 _MultilayerReceive;
//@property(name="_MultilayerMix", display="Layer Mix", mode=static, components="Environment Strength|Shadow Strength|Ignore Ambient Color|Layered Light Map:toggle", visible="_LightingMode == 1")
uniform vec4 _MultilayerMix;
//@property(name="_MultilayerOptions", display="Layer Options", mode=static, components="Use Border Map:toggle|Mask Type (0 Strength, 1 Flat)|Border After AO:toggle|Border Mask LOD", visible="_LightingMode == 1")
uniform vec4 _MultilayerOptions;
//@property(name="_MultilayerFlat", display="Flat Layer", mode=static, components="Border|Blur", visible="_LightingMode == 1")
uniform vec4 _MultilayerFlat;
//@property(name="_ShadowAOShift", display="Border Map Shift", mode=static, components="First Min|First Max|Second Min|Second Max", visible="_LightingMode == 1")
uniform vec4 _ShadowAOShift;
//@property(name="_ShadowAOShift2", display="Border Map Shift 2", mode=static, components="Third Min|Third Max", visible="_LightingMode == 1")
uniform vec4 _ShadowAOShift2;
//@property(name="_FirstShadeMap", display="First Shade Map", slot=texture)
uniform sampler2D _FirstShadeMap;
uniform vec4 _FirstShadeMap_ST;
uniform vec2 _FirstShadeMapPan;
uniform int _FirstShadeMapUV;
//@property(name="_SecondShadeMap", display="Second Shade Map", slot=texture)
uniform sampler2D _SecondShadeMap;
uniform vec4 _SecondShadeMap_ST;
uniform vec2 _SecondShadeMapPan;
uniform int _SecondShadeMapUV;

//@property(name="_ShadowColor", display="Shadow Color", mode=static)
uniform vec4 _ShadowColor;
//@property(name="_ShadowBorder", display="Shadow Border", mode=static, range=[0,1])
uniform float _ShadowBorder;
//@property(name="_ShadowBlur", display="Shadow Blur", mode=static, range=[0,1])
uniform float _ShadowBlur;

// Wrapped shading (Mode 2)
//@property(name="_LightingWrappedWrap", display="Wrap Amount", mode=static, range=[0,1])
uniform float _LightingWrappedWrap;
//@property(name="_LightingWrappedNormalization", display="Wrap Normalize", mode=static, range=[0,1], toggle=true)
uniform float _LightingWrappedNormalization;
//@property(name="_LightingGradientStart", display="Gradient Start", mode=static, range=[0,1])
uniform float _LightingGradientStart;
//@property(name="_LightingGradientEnd", display="Gradient End", mode=static, range=[0,1])
uniform float _LightingGradientEnd;
#endif

// AO Maps
//@category("Lighting")
//@subcategory("Ambient Occlusion")
//@feature(id="material-ao", name="Material AO", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_MATERIAL_AO
//@property(name="_LightingAOMaps", display="AO Map", slot=texture)
uniform sampler2D _LightingAOMaps;
uniform vec4 _LightingAOMaps_ST;
uniform vec2 _LightingAOMapsPan;
uniform int _LightingAOMapsUV;
//@property(name="_LightDataAOStrengthR", display="AO Strength", mode=static, range=[0,1])
uniform float _LightDataAOStrengthR;
//@property(name="_LightDataAOGlobalMaskR", display="AO Global Mask", mode=static, range=[0,16])
uniform int _LightDataAOGlobalMaskR;
//@property(name="_LightDataAOGlobalMaskBlendTypeR", display="AO Global Mask Blend", mode=static, default="2")
uniform int _LightDataAOGlobalMaskBlendTypeR;
#endif

// Shadow Mask
//@category("Lighting")
//@subcategory("Shadowing")
//@feature(id="shadow-masks", name="Shadow Masks", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_SHADOW_MASKS
//@property(name="_LightingShadowMasks", display="Shadow Mask", slot=texture)
uniform sampler2D _LightingShadowMasks;
uniform vec4 _LightingShadowMasks_ST;
uniform vec2 _LightingShadowMasksPan;
uniform int _LightingShadowMasksUV;
//@property(name="_LightingShadowMaskStrengthR", display="Mask Strength", mode=static, range=[0,1])
uniform float _LightingShadowMaskStrengthR;
//@property(name="_LightDataShadowMaskGlobalMaskR", display="Shadow Mask Global Mask", mode=static, range=[0,16])
uniform int _LightDataShadowMaskGlobalMaskR;
//@property(name="_LightDataShadowMaskGlobalMaskBlendTypeR", display="Shadow Mask Global Mask Blend", mode=static, default="2")
uniform int _LightDataShadowMaskGlobalMaskBlendTypeR;
#endif

// ============================================
// Emission
// ============================================
//@category("Effects", order=30)
//@subcategory("Emission")
//@feature(id="emission", name="Emission", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_EMISSION
//@property(name="_EmissionMap", display="Emission Map", slot=texture)
//@tooltip("Texture sampled for emissive contribution.")
uniform sampler2D _EmissionMap;
uniform vec4 _EmissionMap_ST;
uniform vec2 _EmissionMapPan;
uniform int _EmissionMapUV;
//@property(name="_EmissionColor", display="Emission Color", mode=static)
uniform vec4 _EmissionColor;
//@property(name="_EmissionColorThemeIndex", display="Emission Color Theme", mode=static, default="0")
uniform int _EmissionColorThemeIndex;
//@property(name="_EmissionStrength", display="Emission Strength", mode=static, range=[0,8], default="0.0")
uniform float _EmissionStrength;

// Emission scrolling
uniform float _EmissionScrollingEnabled;
//@property(name="_EmissionScrollingSpeed", display="Scroll Speed", mode=animated)
uniform vec2 _EmissionScrollingSpeed;
//@property(name="_EmissionScrollingVertexColor", display="Use Vertex Color", mode=static, range=[0,1], toggle=true)
uniform float _EmissionScrollingVertexColor;
#endif

// ============================================
// Matcap
// ============================================
//@category("Effects")
//@subcategory("Matcap")
//@feature(id="matcap", name="Matcap", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_MATCAP
//@property(name="_Matcap", display="Matcap Texture", slot=texture)
//@tooltip("Sphere-mapped texture layered over the lit surface.")
uniform sampler2D _Matcap;
//@property(name="_MatcapColor", display="Matcap Tint", mode=static)
uniform vec4 _MatcapColor;
//@property(name="_MatcapIntensity", display="Matcap Intensity", mode=static, range=[0,8])
uniform float _MatcapIntensity;
//@property(name="_MatcapBorder", display="Matcap Border", mode=static, range=[0,1])
uniform float _MatcapBorder;
//@property(name="_MatcapUVMode", display="UV Mode", mode=static, enum="0:View Normal|1:Top Pinch|2:Double Sided")
uniform int _MatcapUVMode;          // 0: View Normal, 1: Top Pinch, 2: Double Sided
//@property(name="_MatcapReplace", display="Replace Lit Color", mode=static, range=[0,1], toggle=true)
uniform float _MatcapReplace;
//@property(name="_MatcapMultiply", display="Multiply Lit Color", mode=static, range=[0,1], toggle=true)
uniform float _MatcapMultiply;
//@property(name="_MatcapAdd", display="Additive Blend", mode=static, range=[0,4])
uniform float _MatcapAdd;
//@property(name="_MatcapEmissionStrength", display="Emission Boost", mode=static, range=[0,8])
uniform float _MatcapEmissionStrength;
//@property(name="_MatcapLightMask", display="Hide In Shadow", mode=static, range=[0,1], toggle=true)
uniform float _MatcapLightMask;
uniform float _MatcapNormal;

//@property(name="_MatcapMask", display="Matcap Mask", slot=texture)
uniform sampler2D _MatcapMask;
uniform vec4 _MatcapMask_ST;
//@property(name="_MatcapMaskChannel", display="Mask Channel", mode=static, enum="0:R|1:G|2:B|3:A")
uniform int _MatcapMaskChannel;
//@property(name="_MatcapMaskInvert", display="Invert Mask", mode=static, range=[0,1], toggle=true)
uniform float _MatcapMaskInvert;
#endif

// ============================================
// Rim Lighting
// ============================================
//@category("Effects")
//@subcategory("Rim")
//@feature(id="rim-lighting", name="Rim Lighting", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
//@property(name="_RimLightColor", display="Rim Color", mode=static, default="vec4(1.0, 1.0, 1.0, 1.0)")
uniform vec4 _RimLightColor;
//@property(name="_RimLightColorThemeIndex", display="Rim Color Theme", mode=static, default="0")
uniform int _RimLightColorThemeIndex;
//@property(name="_RimWidth", display="Rim Width", mode=static, range=[0,1], default="0.5")
uniform float _RimWidth;
//@property(name="_RimSharpness", display="Rim Sharpness", mode=static, range=[0,16], default="1.0")
uniform float _RimSharpness;
//@property(name="_RimLightColorBias", display="Light Bias", mode=static, range=[0,1], default="0.0")
uniform float _RimLightColorBias;
//@property(name="_RimEmission", display="Emission Amount", mode=static, range=[0,8], default="1.0")
uniform float _RimEmission;
//@property(name="_RimShapeSettings", display="Rim Shape", mode=static, components="Extended Rim:toggle|Invert:toggle|Power|Use Normal Map:toggle")
uniform vec4 _RimShapeSettings;
//@property(name="_RimColorSettings", display="Rim Color Options", mode=static, components="Base Color Mix|Brightness|Blend Mode|Blur Plus One (0 Uses Sharpness)")
uniform vec4 _RimColorSettings;
//@property(name="_RimHideInShadow", display="Hide In Shadow", mode=static, range=[0,1], toggle=true, default="0.0")
uniform float _RimHideInShadow;
//@property(name="_RimStyle", display="Rim Style", mode=static, enum="0:Smooth|1:Exponential|2:Sharp", default="1")
uniform int _RimStyle;              // 0: Smooth, 1: Exponential, 2: Sharp
//@property(name="_RimBlendStrength", display="Blend Strength", mode=static, range=[0,1], default="1.0")
uniform float _RimBlendStrength;
//@property(name="_RimBlendMode", display="Blend Mode", mode=static, enum="0:Mix|1:Add|2:Multiply", default="1")
uniform int _RimBlendMode;

//@property(name="_RimMask", display="Rim Mask", slot=texture)
uniform sampler2D _RimMask;
uniform vec4 _RimMask_ST;
//@property(name="_RimMaskChannel", display="Mask Channel", mode=static, enum="0:R|1:G|2:B|3:A", default="0")
uniform int _RimMaskChannel;
uniform int _RimMaskUV;
//@property(name="_RimColorTexture", display="Rim Color Texture", slot=texture)
uniform sampler2D _RimColorTexture;
uniform vec4 _RimColorTexture_ST;
uniform vec2 _RimColorTexturePan;
uniform int _RimColorTextureUV;

#endif

// ============================================
// Specular
// ============================================
//@category("Lighting")
//@property(name="_SpecularSmoothness", display="Specular Smoothness", mode=static, range=[0,1])
uniform float _SpecularSmoothness;
//@property(name="_SpecularStrength", display="Specular Strength", mode=static, range=[0,4])
uniform float _SpecularStrength;

//@subcategory("Specular")
//@feature(id="advanced-specular", name="Advanced Specular", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
uniform float _StylizedSpecular;
//@property(name="_SpecularMap", display="Specular Map", slot=texture)
uniform sampler2D _SpecularMap;
uniform vec4 _SpecularMap_ST;
//@property(name="_SpecularTint", display="Specular Tint", mode=static)
uniform vec4 _SpecularTint;
//@property(name="_SpecularType", display="Specular Model", mode=static, enum="0:Realistic|1:Toon|2:Anisotropic")
uniform int _SpecularType;          // 0: Realistic, 1: Toon, 2: Anisotropic
//@property(name="_PBRMetallicMaps", display="Metallic Data", slot=texture)
uniform sampler2D _PBRMetallicMaps;
uniform vec4 _PBRMetallicMaps_ST;
uniform vec2 _PBRMetallicMapsPan;
uniform int _PBRMetallicMapsUV;
//@property(name="_PBRMetallicMapsMetallicChannel", display="Metallic Channel", mode=static, enum="0:R|1:G|2:B|3:A")
uniform int _PBRMetallicMapsMetallicChannel;
//@property(name="_PBRMetallicMapInvert", display="Invert Metallic", mode=static, range=[0,1], toggle=true)
uniform float _PBRMetallicMapInvert;
//@property(name="_PBRMetallicMultiplier", display="Metallic Multiplier", mode=static, range=[0,2])
uniform float _PBRMetallicMultiplier;
//@property(name="_PBRSmoothnessMaps", display="Smoothness Data", slot=texture)
uniform sampler2D _PBRSmoothnessMaps;
uniform vec4 _PBRSmoothnessMaps_ST;
uniform vec2 _PBRSmoothnessMapsPan;
uniform int _PBRSmoothnessMapsUV;
//@property(name="_PBRSmoothnessMapsChannel", display="Smoothness Channel", mode=static, enum="0:R|1:G|2:B|3:A")
uniform int _PBRSmoothnessMapsChannel;
//@property(name="_PBRSmoothnessMapInvert", display="Invert Smoothness", mode=static, range=[0,1], toggle=true)
uniform float _PBRSmoothnessMapInvert;
//@property(name="_PBRRoughnessMultiplier", display="Roughness Multiplier", mode=static, range=[0,2])
uniform float _PBRRoughnessMultiplier;
//@property(name="_PBRPackedMasksEnabled", display="Use Packed Reflection And Specular Masks", mode=static, default="0")
uniform int _PBRPackedMasksEnabled;
//@property(name="_SpecularAASettings", display="Specular Anti-Aliasing", mode=static, components="Enabled:toggle|Variance|Threshold|Second Specular Layer:toggle")
uniform vec4 _SpecularAASettings;
//@property(name="_SecondSpecularSettings", display="Second Specular", mode=static, components="Smoothness|Strength|Ignore Cast Shadows:toggle|Extended BRDF:toggle")
uniform vec4 _SecondSpecularSettings;
//@property(name="_ReflectionResponse", display="Reflection Response", mode=static, components="Fresnel Strength|Exposure Occlusion")
uniform vec4 _ReflectionResponse;
//@property(name="_ReflectionTint", display="Reflection Tint", mode=static)
uniform vec4 _ReflectionTint;
//@property(name="_PBRReflectionStrength", display="Reflection Strength", mode=static, default="1.0")
uniform float _PBRReflectionStrength;
//@property(name="_PBRMetallicMapsReflectionMaskChannel", display="Reflection Mask Channel", mode=static, default="2")
uniform int _PBRMetallicMapsReflectionMaskChannel;
//@property(name="_PBRMetallicMapsSpecularMaskChannel", display="Specular Mask Channel", mode=static, default="3")
uniform int _PBRMetallicMapsSpecularMaskChannel;
//@property(name="_PBRReflectionMaskInvert", display="Invert Reflection Mask", mode=static, default="0")
uniform float _PBRReflectionMaskInvert;
//@property(name="_PBRSpecularMaskInvert", display="Invert Specular Mask", mode=static, default="0")
uniform float _PBRSpecularMaskInvert;
#endif


// ============================================
// Detail Textures
// ============================================
//@category("Surface")
//@subcategory("Detail")
//@feature(id="detail-textures", name="Detail Textures", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES
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
//@property(name="_DetailMask", display="Detail Mask", slot=texture)
uniform sampler2D _DetailMask;
uniform vec4 _DetailMask_ST;
//@property(name="_DetailTex", display="Detail Albedo", slot=texture)
uniform sampler2D _DetailTex;
uniform vec4 _DetailTex_ST;
uniform vec2 _DetailTexPan;
//@property(name="_DetailTint", display="Detail Tint", mode=static)
uniform vec3 _DetailTint;
//@property(name="_DetailTexIntensity", display="Detail Intensity", mode=static, range=[0,4])
uniform float _DetailTexIntensity;
//@property(name="_DetailBrightness", display="Detail Brightness", mode=static, range=[0,4])
uniform float _DetailBrightness;

//@property(name="_DetailNormalMap", display="Detail Normal", slot=texture)
uniform sampler2D _DetailNormalMap;
uniform vec4 _DetailNormalMap_ST;
uniform vec2 _DetailNormalMapPan;
//@property(name="_DetailNormalMapScale", display="Detail Normal Strength", mode=static, range=[0,2])
uniform float _DetailNormalMapScale;
//@property(name="_DetailNormalMapFlipGreen", display="Detail Normal Green Inversion", mode=static, default="0")
uniform int _DetailNormalMapFlipGreen;
//@property(name="_DetailTintScale", display="Detail Tint Scale", mode=static, default="1.0")
uniform float _DetailTintScale;
//@property(name="_DetailTintThemeIndex", display="Detail Tint Theme", mode=static, range=[0,4])
uniform int _DetailTintThemeIndex;
//@property(name="_DetailTexStochastic", display="Detail Stochastic Sampling", mode=static, range=[0,1], toggle=true)
uniform float _DetailTexStochastic;
//@property(name="_DetailMaskStochastic", display="Detail Mask Stochastic Sampling", mode=static, range=[0,1], toggle=true)
uniform float _DetailMaskStochastic;
//@property(name="_DetailNormalMapStochastic", display="Detail Normal Stochastic Sampling", mode=static, range=[0,1], toggle=true)
uniform float _DetailNormalMapStochastic;
//@property(name="_DetailTexGlobalMask", display="Detail Global Mask", mode=static, range=[0,16])
uniform int _DetailTexGlobalMask;
//@property(name="_DetailTexGlobalMaskBlendType", display="Detail Global Mask Blend", mode=static, default="2")
uniform int _DetailTexGlobalMaskBlendType;
//@property(name="_DetailNormalGlobalMask", display="Detail Normal Global Mask", mode=static, range=[0,16])
uniform int _DetailNormalGlobalMask;
//@property(name="_DetailNormalGlobalMaskBlendType", display="Detail Normal Global Mask Blend", mode=static, default="2")
uniform int _DetailNormalGlobalMaskBlendType;
#endif

// ============================================
// Vertex Colors
// ============================================
//@category("Surface")
//@subcategory("Vertex Colors")
//@property(name="_MainVertexColoringEnabled", display="Use Vertex Colors", mode=static, range=[0,1], toggle=true)
//@tooltip("Enables multiplication of the sampled base color by the mesh vertex colors.")
uniform float _MainVertexColoringEnabled;
//@property(name="_MainVertexColoringLinearSpace", display="Vertex Colors Are Linear", mode=static, range=[0,1], toggle=true, visible="_MainVertexColoringEnabled > 0")
//@tooltip("Treats incoming vertex colors as already converted to linear space before tinting the base color.")
uniform float _MainVertexColoringLinearSpace;
//@property(name="_MainVertexColoring", display="Color Blend", mode=static, range=[0,1], visible="_MainVertexColoringEnabled > 0")
//@tooltip("Controls how strongly vertex RGB contributes to the final albedo tint.")
uniform float _MainVertexColoring;
//@property(name="_MainUseVertexColorAlpha", display="Alpha Blend", mode=static, range=[0,1], visible="_MainVertexColoringEnabled > 0")
//@tooltip("Controls how strongly vertex alpha modulates the final surface alpha.")
uniform float _MainUseVertexColorAlpha;

// ============================================
// Outline (for outline pass)
// ============================================
//@category("Effects")
//@subcategory("Outline")
//@feature(id="outline", name="Outline", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_OUTLINE
//@property(name="_OutlineColor", display="Outline Color", mode=animated)
uniform vec4 _OutlineColor;
//@property(name="_OutlineWidth", display="Outline Width", mode=animated, range=[0,0.1])
uniform float _OutlineWidth;
//@property(name="_OutlineMask", display="Outline Mask", slot=texture)
uniform sampler2D _OutlineMask;
//@property(name="_OutlineEmission", display="Outline Emission", mode=static, range=[0,8])
uniform float _OutlineEmission;
//@property(name="_OutlineLit", display="Receive Lighting", mode=static, range=[0,1])
uniform float _OutlineLit;
//@property(name="_OutlineDistanceFadeStart", display="Fade Start", mode=static, range=[0,100])
uniform float _OutlineDistanceFadeStart;
//@property(name="_OutlineDistanceFadeEnd", display="Fade End", mode=static, range=[0,100])
uniform float _OutlineDistanceFadeEnd;
//@property(name="_OutlineTextureTint", display="Texture Tint", mode=static, range=[0,1])
uniform float _OutlineTextureTint;
//@property(name="_OutlineVertexColorTint", display="Vertex Tint", mode=static, range=[0,1])
uniform float _OutlineVertexColorTint;
//@property(name="_OutlineExpansionMode", display="Expansion", mode=static, enum="0:Basic|1:Rim|2:Directional|3:Drop Shadow")
uniform int _OutlineExpansionMode;
//@property(name="_OutlineSpace", display="Sizing Space", mode=static, enum="0:Object|1:World|2:Screen")
uniform int _OutlineSpace;
//@property(name="_OutlinePersonaDirection", display="Persona Direction", mode=animated)
uniform vec3 _OutlinePersonaDirection;
//@property(name="_OutlineDropShadowOffset", display="Drop Shadow Offset", mode=animated)
uniform vec3 _OutlineDropShadowOffset;
//@property(name="_OutlineFixedSize", display="Fixed Screen Width", mode=animated, range=[0,1])
uniform float _OutlineFixedSize;
//@property(name="_OutlineUseVertexColors", display="Vertex Color Width", mode=animated, range=[0,1])
uniform float _OutlineUseVertexColors;
//@property(name="_OutlineZOffset", display="Depth Offset", mode=animated, range=[-1,1])
uniform float _OutlineZOffset;
// The importer sets the basic outline values from the source outline. The inspector does not show them.
//@property(name="_OutlineBasicModel", mode=static, hidden=true)
uniform int _OutlineBasicModel;
//@property(name="_OutlineFixWidth", mode=static, hidden=true)
uniform float _OutlineFixWidth;
//@property(name="_OutlinesMaxDistance", mode=static, hidden=true)
uniform float _OutlinesMaxDistance;
uniform vec4 _OutlineTexture_ST;
uniform vec2 _OutlineTexturePan;
uniform int _OutlineTextureUV;
//@property(name="_OutlineTexture", display="Outline Texture", slot=texture)
uniform sampler2D _OutlineTexture;
uniform vec4 _OutlineMask_ST;
uniform vec2 _OutlineMaskPan;
uniform int _OutlineMaskUV;
//@property(name="_OutlineHueShift", display="Hue Shift", mode=animated, range=[-1,1])
uniform float _OutlineHueShift;
//@property(name="_OutlineHueShiftSpeed", display="Hue Shift Speed", mode=animated)
uniform float _OutlineHueShiftSpeed;
//@property(name="_OutlineShadowStrength", display="Shadow Strength", mode=animated, range=[0,1])
uniform float _OutlineShadowStrength;
#endif

// ============================================
// Back Face
// ============================================
//@category("Effects")
//@subcategory("Backface")
// The backface texture and the backface normal map share this transform and UV channel. The surface
// extensions and the backface effects both read them, so they are outside both feature guards.
//@property(name="_BackFaceTexture_ST", display="Backface Transform", mode=static, default="vec4(1.0, 1.0, 0.0, 0.0)")
uniform vec4 _BackFaceTexture_ST;
//@property(name="_BackFaceTexturePan", display="Backface Pan", mode=animated, default="vec2(0.0)")
uniform vec2 _BackFaceTexturePan;
//@property(name="_BackFaceTextureUV", display="Backface UV", mode=static, default="0")
uniform int _BackFaceTextureUV;
//@feature(id="backface", name="Backface Effects", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_BACKFACE
//@property(name="_BackFaceColor", display="Backface Color", mode=static)
uniform vec4 _BackFaceColor;
//@property(name="_BackFaceBlendMode", display="Blend Mode", mode=static, enum="0:Mix|1:Add|2:Multiply")
uniform float _BackFaceBlendMode;
//@property(name="_BackFaceTexture", display="Backface Texture", slot=texture)
uniform sampler2D _BackFaceTexture;
//@property(name="_BackFaceEmission", display="Emission", mode=static, range=[0,8])
uniform float _BackFaceEmission;
//@property(name="_BackFaceAlpha", display="Backface Alpha", mode=static, range=[0,1], default="1.0")
uniform float _BackFaceAlpha;
//@property(name="_BackFaceReplaceAlpha", display="Replace Alpha", mode=static, range=[0,1], toggle=true)
uniform float _BackFaceReplaceAlpha;
//@property(name="_BackFaceColorThemeIndex", display="Backface Color Theme", mode=static, range=[0,4])
uniform int _BackFaceColorThemeIndex;
//@property(name="_BackFaceMask", display="Backface Mask", slot=texture)
uniform sampler2D _BackFaceMask;
uniform vec4 _BackFaceMask_ST;
uniform vec2 _BackFaceMaskPan;
uniform int _BackFaceMaskUV;
//@property(name="_BackFaceMaskChannel", display="Backface Mask Channel", mode=static, enum="0:R|1:G|2:B|3:A")
uniform int _BackFaceMaskChannel;
//@property(name="_BackFaceHueShiftEnabled", display="Backface Hue Shift", mode=static, range=[0,1], toggle=true)
uniform float _BackFaceHueShiftEnabled;
//@property(name="_BackFaceShiftColorSpace", display="Backface Hue Space", mode=static, enum="0:OKLab|1:HSV", visible="_BackFaceHueShiftEnabled > 0")
uniform int _BackFaceShiftColorSpace;
//@property(name="_BackFaceHueSelectOrShift", display="Backface Hue Select Or Shift", mode=static, enum="0:Select|1:Shift", default="1", visible="_BackFaceHueShiftEnabled > 0")
uniform int _BackFaceHueSelectOrShift;
//@property(name="_BackFaceHueShift", display="Backface Hue Shift Amount", mode=static, range=[0,1], visible="_BackFaceHueShiftEnabled > 0")
uniform float _BackFaceHueShift;
//@property(name="_BackFaceHueShiftSpeed", display="Backface Hue Shift Speed", mode=animated, visible="_BackFaceHueShiftEnabled > 0")
uniform float _BackFaceHueShiftSpeed;
//@property(name="_BackFaceDetailIntensity", display="Backface Detail Intensity", mode=static, range=[0,5], default="1.0")
uniform float _BackFaceDetailIntensity;
//@property(name="_BackFaceEmissionLimiter", display="Backface Emission Multiplier", mode=static, default="1.0")
uniform float _BackFaceEmissionLimiter;
#endif

// ============================================
// Glitter / Sparkle
// ============================================
//@category("Effects")
//@subcategory("Glitter")
//@feature(id="glitter", name="Glitter", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_GLITTER
//@property(name="_GlitterColor", display="Glitter Color", mode=static)
uniform vec4 _GlitterColor;
//@property(name="_GlitterDensity", display="Density", mode=static, range=[0,10], visible="_GlitterModel == 0")
uniform float _GlitterDensity;
//@property(name="_GlitterSize", display="Size", mode=static, range=[0,1])
uniform float _GlitterSize;
// The importer sets the glitter randomization and audio values. The inspector does not show them.
//@property(name="_GlitterRandomSize", mode=static, hidden=true)
uniform int _GlitterRandomSize;
//@property(name="_GlitterMinMaxSize", mode=static, hidden=true)
uniform vec2 _GlitterMinMaxSize;
//@property(name="_GlitterRandomColors", mode=static, hidden=true)
uniform int _GlitterRandomColors;
//@property(name="_GlitterRandomSaturation", mode=static, hidden=true)
uniform vec2 _GlitterRandomSaturation;
//@property(name="_GlitterRandomValue", mode=static, hidden=true)
uniform vec2 _GlitterRandomValue;
//@property(name="_GlitterGlobalMask", mode=static, hidden=true)
uniform vec2 _GlitterGlobalMask;
uniform int _GlitterColorThemeIndex;
//@property(name="_GlitterHueControl", mode=static, hidden=true)
uniform vec4 _GlitterHueControl;
//@property(name="_GlitterAudioBands", mode=static, hidden=true)
uniform vec4 _GlitterAudioBands;
//@property(name="_GlitterAudioSizeRange", mode=static, hidden=true)
uniform vec2 _GlitterAudioSizeRange;
//@property(name="_GlitterAudioBrightnessRange", mode=static, hidden=true)
uniform vec2 _GlitterAudioBrightnessRange;
//@property(name="_GlitterAudioMaskRange", mode=static, hidden=true)
uniform vec2 _GlitterAudioMaskRange;
//@property(name="_GlitterSpeed", display="Scroll Speed", mode=animated, range=[0,10])
uniform float _GlitterSpeed;
//@property(name="_GlitterBrightness", display="Brightness", mode=static, range=[0,16])
uniform float _GlitterBrightness;
//@property(name="_GlitterMinAngle", display="Min View Angle", mode=static, range=[0,1], visible="_GlitterModel == 0")
uniform float _GlitterMinAngle;
//@property(name="_GlitterMaxAngle", display="Max View Angle", mode=static, range=[0,1], visible="_GlitterModel == 0")
uniform float _GlitterMaxAngle;
//@property(name="_GlitterRainbow", display="Rainbow Blend", mode=static, range=[0,1], visible="_GlitterModel == 0")
uniform float _GlitterRainbow;
//@property(name="_GlitterMask", display="Glitter Mask", slot=texture)
uniform sampler2D _GlitterMask;
//@property(name="_GlitterTexture", display="Glitter Shape", slot=texture)
uniform sampler2D _GlitterTexture;
//@property(name="_GlitterColorMap", display="Glitter Color Map", slot=texture, visible="_GlitterModel > 0")
uniform sampler2D _GlitterColorMap;
//@property(name="_GlitterModel", display="Glitter Model", mode=static, enum="0:Sparkle|1:Flakes")
//@tooltip("Sparkle uses density, rainbow, and view angle. Flakes uses a cell grid of oriented flakes.")
uniform int _GlitterModel;
//@property(name="_GlitterFlakeShading", display="Flake Shading", mode=static, enum="81:Classic|92:Standard|100:Light Ramped", visible="_GlitterModel > 0")
//@tooltip("Classic uses one view-aligned layer. Standard uses layered flakes. Light Ramped also scales flakes by the light map and can reduce density with distance.")
uniform int _GlitterFlakeShading;
//@property(name="_GlitterFrequency", display="Flake Frequency", mode=static, range=[1,1000], visible="_GlitterModel > 0")
uniform float _GlitterFrequency;
//@property(name="_GlitterDistanceLOD", display="Distance Density Reduction", mode=static, range=[0,1], toggle=true, visible="_GlitterModel > 0 && _GlitterFlakeShading == 100")
uniform float _GlitterDistanceLOD;
//@property(name="_GlitterLayers", display="Flake Layers", mode=static, range=[1,8], visible="_GlitterModel > 0")
uniform int _GlitterLayers;
uniform int _GlitterUV;
//@property(name="_GlitterMode", display="Flake Response", mode=static, enum="0:View Angle|1:Pulse|2:Light Reflection", visible="_GlitterModel > 0")
uniform int _GlitterMode;
// The flake shading supports only circle flakes, so the shader does not read this value.
//@property(name="_GlitterShape", hidden=true)
uniform int _GlitterShape;
//@property(name="_GlitterAngleRange", display="Flake Angle Range", mode=static, range=[0,90], visible="_GlitterModel > 0")
uniform float _GlitterAngleRange;
//@property(name="_GlitterContrast", display="Flake Contrast", mode=static, range=[1,1000], visible="_GlitterModel > 0 && _GlitterMode != 1")
uniform float _GlitterContrast;
//@property(name="_GlitterBias", display="View Bias", mode=static, range=[0,1], visible="_GlitterModel > 0 && _GlitterMode == 0")
uniform float _GlitterBias;
//@property(name="_GlitterMinBrightness", display="Minimum Brightness", mode=static, range=[0,1], visible="_GlitterModel > 0")
uniform float _GlitterMinBrightness;
//@property(name="_GlitterCenterSize", display="Pulse Center Size", mode=static, range=[0,1], visible="_GlitterModel > 0 && _GlitterMode == 1")
uniform float _GlitterCenterSize;
//@property(name="_GlitterRandomLocation", display="Random Location", mode=static, range=[0,1], visible="_GlitterModel > 0")
uniform float _GlitterRandomLocation;
//@property(name="_GlitterUseNormals", display="Use Normal Map", mode=static, range=[0,1], visible="_GlitterModel > 0")
uniform float _GlitterUseNormals;
//@property(name="_GlitterUseSurfaceColor", display="Use Surface Color", mode=static, range=[0,1], visible="_GlitterModel > 0")
uniform float _GlitterUseSurfaceColor;
//@property(name="_GlitterHideInShadow", display="Hide In Shadow", mode=static, range=[0,1], visible="_GlitterModel > 0")
uniform float _GlitterHideInShadow;
//@property(name="_GlitterScaleWithLighting", display="Scale With Lighting", mode=static, range=[0,1], visible="_GlitterModel > 0")
uniform float _GlitterScaleWithLighting;
//@property(name="_GlitterBlendType", display="Flake Blend", mode=static, enum="0:Add Emission|1:Replace Surface", visible="_GlitterModel > 0")
uniform int _GlitterBlendType;
//@property(name="_GlitterMaskChannel", display="Mask Channel", mode=static, enum="0:R|1:G|2:B|3:A", visible="_GlitterModel > 0")
uniform int _GlitterMaskChannel;
//@property(name="_GlitterMaskInvert", display="Invert Mask", mode=static, range=[0,1], toggle=true, visible="_GlitterModel > 0")
uniform float _GlitterMaskInvert;
//@property(name="_GlitterUVPanning", display="Flake Panning", mode=static, visible="_GlitterModel > 0")
uniform vec2 _GlitterUVPanning;
uniform vec4 _GlitterMask_ST;
uniform vec2 _GlitterMaskPan;
uniform vec4 _GlitterTexture_ST;
uniform vec2 _GlitterTexturePan;
uniform int _GlitterMaskUV;
uniform vec4 _GlitterColorMap_ST;
uniform vec2 _GlitterColorMapPan;
uniform int _GlitterColorMapUV;
#endif

// ============================================
// Flipbook Animation
// ============================================
//@category("Effects")
//@subcategory("Flipbook")
//@feature(id="flipbook", name="Flipbook", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_FLIPBOOK
//@property(name="_FlipbookTexture", display="Flipbook Texture", slot=texture)
uniform sampler2D _FlipbookTexture;
//@property(name="_FlipbookColumns", display="Columns", mode=static, range=[1,32])
uniform float _FlipbookColumns;
//@property(name="_FlipbookRows", display="Rows", mode=static, range=[1,32])
uniform float _FlipbookRows;
//@property(name="_FlipbookFrameRate", display="Frame Rate", mode=animated, range=[0,120])
uniform float _FlipbookFrameRate;
uniform float _FlipbookFrame;
//@property(name="_FlipbookManualFrame", display="Manual Frame", mode=animated, range=[0,256])
uniform float _FlipbookManualFrame;
//@property(name="_FlipbookBlendMode", display="Blend Mode", mode=static, enum="0:Step|1:Crossfade")
uniform float _FlipbookBlendMode;
//@property(name="_FlipbookCrossfade", display="Crossfade", mode=static, range=[0,1])
uniform float _FlipbookCrossfade;
#endif

// ============================================
// Subsurface Scattering
// ============================================
//@category("Lighting")
//@subcategory("Subsurface")
//@feature(id="subsurface", name="Subsurface", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_SUBSURFACE
//@property(name="_SSSColor", display="SSS Color", mode=static)
uniform vec4 _SSSColor;
//@property(name="_SSSPower", display="Power", mode=static, range=[0,16])
uniform float _SSSPower;
//@property(name="_SSSDistortion", display="Distortion", mode=static, range=[0,4])
uniform float _SSSDistortion;
//@property(name="_SSSScale", display="Scale", mode=static, range=[0,8])
uniform float _SSSScale;
//@property(name="_SSSThicknessMap", display="Thickness Map", slot=texture)
uniform sampler2D _SSSThicknessMap;
//@property(name="_SSSAmbient", display="Ambient Contribution", mode=static, range=[0,1])
uniform float _SSSAmbient;
#endif

// ============================================
// Dissolve
// ============================================
//@category("Effects")
//@subcategory("Dissolve")
//@feature(id="dissolve", name="Dissolve", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_DISSOLVE
//@property(name="_DissolveType", display="Dissolve Mode", mode=static, enum="0:Linear|1:Spherical|2:Point to Point|3:Center Out|4:UV Tile|5:Source Basic")
uniform float _DissolveType;
//@property(name="_DissolveSettings", display="Dissolve Settings", mode=static, default="vec4(0.5, 0.0, 0.0, 0.0)")
uniform vec4 _DissolveSettings;
//@property(name="_DissolveTextureColor", display="Dissolved Color", mode=static, default="vec4(1.0)")
uniform vec4 _DissolveTextureColor;
//@property(name="_DissolveToEmissionStrength", display="Dissolved Emission", mode=static, default=0)
uniform float _DissolveToEmissionStrength;
//@property(name="_DissolveProgress", display="Progress", mode=animated, range=[0,1])
uniform float _DissolveProgress;
//@property(name="_DissolveEdgeColor", display="Edge Color", mode=static)
uniform vec4 _DissolveEdgeColor;
//@property(name="_DissolveEdgeWidth", display="Edge Width", mode=static, range=[0,1])
uniform float _DissolveEdgeWidth;
//@property(name="_DissolveEdgeEmission", display="Edge Emission", mode=static, range=[0,8])
uniform float _DissolveEdgeEmission;
//@property(name="_DissolveNoiseTexture", display="Noise Texture", slot=texture)
uniform sampler2D _DissolveNoiseTexture;
uniform vec4 _DissolveNoiseTexture_ST;
//@property(name="_DissolveNoiseStrength", display="Noise Strength", mode=static, range=[0,4])
uniform float _DissolveNoiseStrength;
//@property(name="_DissolveStartPoint", display="Start Point", mode=static)
uniform vec3 _DissolveStartPoint;
//@property(name="_DissolveEndPoint", display="End Point", mode=static)
uniform vec3 _DissolveEndPoint;
//@property(name="_DissolveInvert", display="Invert", mode=static, range=[0,1], toggle=true)
uniform float _DissolveInvert;
uniform vec2 _DissolveNoiseTexturePan;
uniform int _DissolveNoiseTextureUV;
//@property(name="_DissolveDetailNoise", display="Detail Noise", slot=texture)
uniform sampler2D _DissolveDetailNoise;
uniform vec4 _DissolveDetailNoise_ST;
uniform vec2 _DissolveDetailNoisePan;
uniform int _DissolveDetailNoiseUV;
//@property(name="_DissolveDetailStrength", display="Detail Strength", mode=static, range=[0,4])
uniform float _DissolveDetailStrength;
//@property(name="_DissolveMask", display="Dissolve Mask", slot=texture)
uniform sampler2D _DissolveMask;
uniform vec4 _DissolveMask_ST;
uniform vec2 _DissolveMaskPan;
uniform int _DissolveMaskUV;
//@property(name="_DissolveMaskInvert", display="Invert Mask", mode=static, range=[0,1], toggle=true)
uniform float _DissolveMaskInvert;
//@property(name="_DissolveContinuous", display="Continuous", mode=animated, range=[0,1], toggle=true)
uniform float _DissolveContinuous;
//@property(name="_DissolveCoordinateSpace", display="Coordinate Space", mode=static, enum="0:Local|1:World|2:UV")
uniform int _DissolveCoordinateSpace;
//@property(name="_DissolveTileGrid", display="UV Tile Grid", mode=static, default="vec2(1.0)")
uniform vec2 _DissolveTileGrid;
//@property(name="_DissolveHueShift", display="Edge Hue Shift", mode=animated, range=[-1,1])
uniform float _DissolveHueShift;
//@property(name="_DissolveEdgeGradient", display="Edge Gradient", slot=texture)
uniform sampler2D _DissolveEdgeGradient;
uniform vec4 _DissolveEdgeGradient_ST;
//@property(name="_DissolveEdgeTexture", display="Edge Texture", slot=texture)
uniform sampler2D _DissolveEdgeTexture;
uniform vec4 _DissolveEdgeTexture_ST;
uniform vec2 _DissolveEdgeTexturePan;
uniform int _DissolveEdgeTextureUV;


//@property(name="_DissolveCutoff", display="Cutoff", mode=static, range=[0,1])
uniform float _DissolveCutoff;
#endif

// ============================================
// Parallax / Height Mapping
// ============================================
//@category("Surface")
//@subcategory("Parallax")
//@feature(id="parallax", name="Parallax", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_PARALLAX
//@property(name="_ParallaxMode", display="Parallax Mode", mode=static, enum="0:Offset|1:Occlusion|2:Silhouette")
uniform float _ParallaxMode;
//@property(name="_ParallaxMap", display="Height Map", slot=texture)
//@tooltip("Height texture used for parallax or relief sampling.")
uniform sampler2D _ParallaxMap;
uniform vec4 _ParallaxMap_ST;
//@property(name="_ParallaxStrength", display="Height Scale", mode=static, range=[0,0.1])
uniform float _ParallaxStrength;
//@property(name="_ParallaxMinSamples", display="Min Samples", mode=static, range=[1,64])
uniform float _ParallaxMinSamples;
//@property(name="_ParallaxMaxSamples", display="Max Samples", mode=static, range=[1,128])
uniform float _ParallaxMaxSamples;
//@property(name="_ParallaxOffset", display="Offset Bias", mode=static, range=[-1,1])
uniform float _ParallaxOffset;
//@property(name="_ParallaxMapChannel", display="Height Channel", mode=static, enum="0:R|1:G|2:B|3:A")
uniform float _ParallaxMapChannel;
#endif

#include "surface_extensions_uniforms.glsl"
#include "extended_effect_uniforms.glsl"

#endif // TOON_UNIFORMS_GLSL
