// Uber Shader - Uniform Declarations
// Uses engine-standard uniform names for compatibility

#ifndef TOON_UNIFORMS_GLSL
#define TOON_UNIFORMS_GLSL

// ============================================
// Transform Matrices (Provided by engine - matches EEngineUniform names)
// Engine sets vertex-stage camera uniforms with a "_VTX" suffix.
// ============================================
uniform mat4 ModelMatrix;
uniform mat4 ViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;
uniform mat4 ViewProjectionMatrix_VTX;

// Camera (matches EEngineUniform.CameraPosition)
uniform vec3 CameraPosition;

// Time for animations (provided by engine when RenderTime is requested)
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
//@property(name="_MainTex", display="Albedo Map", slot=texture)
//@tooltip("Primary base-color texture sampled for the material surface.")
uniform sampler2D _MainTex;
uniform vec4 _MainTex_ST;           // xy: tiling, zw: offset
uniform vec2 _MainTexPan;
uniform int _MainTexUV;

//@property(name="_Color", display="Tint", mode=static)
//@tooltip("Color tint multiplied into the sampled albedo.")
uniform vec4 _Color;                // Main color tint
uniform int _ColorThemeIndex;

// ============================================
// Normal Map
// ============================================
//@category("Surface")
//@property(name="_BumpMap", display="Normal Map", slot=texture)
//@tooltip("Tangent-space normal map used to perturb surface lighting.")
uniform sampler2D _BumpMap;
uniform vec4 _BumpMap_ST;
uniform vec2 _BumpMapPan;
uniform int _BumpMapUV;
//@property(name="_BumpScale", display="Normal Strength", mode=static, range=[0,2])
uniform float _BumpScale;

// ============================================
// Alpha / Transparency
// ============================================
//@category("Transparency", order=10)
//@feature(id="alpha-masks", name="Alpha Masks", default=on, cost=low)
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
//@tooltip("Threshold below which fragments are discarded in masked modes.")
uniform float _Cutoff;              // Alpha cutoff threshold
uniform int _Mode;                  // Rendering mode (opaque, cutout, fade, etc.)
uniform float _AlphaForceOpaque;
uniform float _AlphaMod;

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
//@property(name="_Saturation", display="Saturation", mode=static, range=[0,2])
uniform float _Saturation;
//@property(name="_MainBrightness", display="Brightness", mode=static, range=[0,4])
uniform float _MainBrightness;
uniform float _MainHueShiftToggle;
//@property(name="_MainHueShift", display="Hue Shift", mode=static, range=[-180,180])
uniform float _MainHueShift;
//@property(name="_MainHueShiftSpeed", display="Hue Shift Speed", mode=animated, range=[-10,10])
uniform float _MainHueShiftSpeed;
//@property(name="_MainHueShiftColorSpace", display="Hue Space", mode=static, enum="0:OKLab|1:HSV")
uniform int _MainHueShiftColorSpace;    // 0: OKLab, 1: HSV
//@property(name="_MainHueShiftReplace", display="Hue Replace", mode=static, range=[0,1], toggle=true)
uniform float _MainHueShiftReplace;
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
//@property(name="_LightingMapMode", display="Light Map Mode", mode=static, enum="0:Poiyomi|1:Normalized|2:Saturated|3:Shadow Only")
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
//@property(name="_ShadowOffset", display="Shadow Offset", mode=static, range=[-1,1])
uniform float _ShadowOffset;

// Flat shading (Mode 5)
//@property(name="_ForceFlatRampedLightmap", display="Binary Flat Lightmap", mode=static, range=[0,1], toggle=true)
//@tooltip("When flat lighting mode is active, forces a hard 0.5 threshold instead of using the continuous remapped light value.")
uniform float _ForceFlatRampedLightmap;

// Multilayer Math (Mode 1)
//@property(name="_ShadowColorTex", display="Shadow Color Map", slot=texture)
uniform sampler2D _ShadowColorTex;
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
#endif

// Shadow Mask
//@category("Lighting")
//@subcategory("Shadowing")
//@feature(id="shadow-masks", name="Shadow Masks", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_SHADOW_MASKS
//@property(name="_LightingShadowMasks", display="Shadow Mask", slot=texture)
uniform sampler2D _LightingShadowMasks;
uniform vec4 _LightingShadowMasks_ST;
//@property(name="_LightingShadowMaskStrengthR", display="Mask Strength", mode=static, range=[0,1])
uniform float _LightingShadowMaskStrengthR;
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
//@property(name="_MatcapUVMode", display="UV Mode", mode=static, enum="0:UTS|1:Top Pinch|2:Double Sided")
uniform int _MatcapUVMode;          // 0: UTS, 1: Top Pinch, 2: Double Sided
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
//@property(name="_RimWidth", display="Rim Width", mode=static, range=[0,1], default="0.5")
uniform float _RimWidth;
//@property(name="_RimSharpness", display="Rim Sharpness", mode=static, range=[0,16], default="1.0")
uniform float _RimSharpness;
//@property(name="_RimLightColorBias", display="Light Bias", mode=static, range=[0,1], default="0.0")
uniform float _RimLightColorBias;
//@property(name="_RimEmission", display="Emission Amount", mode=static, range=[0,8], default="1.0")
uniform float _RimEmission;
//@property(name="_RimHideInShadow", display="Hide In Shadow", mode=static, range=[0,1], toggle=true, default="0.0")
uniform float _RimHideInShadow;
//@property(name="_RimStyle", display="Rim Style", mode=static, enum="0:Poiyomi|1:UTS2|2:LilToon", default="1")
uniform int _RimStyle;              // 0: Poiyomi, 1: UTS2, 2: LilToon
//@property(name="_RimBlendStrength", display="Blend Strength", mode=static, range=[0,1], default="1.0")
uniform float _RimBlendStrength;
//@property(name="_RimBlendMode", display="Blend Mode", mode=static, enum="0:Mix|1:Add|2:Multiply", default="1")
uniform int _RimBlendMode;

//@property(name="_RimMask", display="Rim Mask", slot=texture)
uniform sampler2D _RimMask;
uniform vec4 _RimMask_ST;
//@property(name="_RimMaskChannel", display="Mask Channel", mode=static, enum="0:R|1:G|2:B|3:A", default="0")
uniform int _RimMaskChannel;
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
#endif

// ============================================
// Detail Textures
// ============================================
//@category("Surface")
//@subcategory("Detail")
//@feature(id="detail-textures", name="Detail Textures", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES
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
#endif

// ============================================
// Vertex Colors
// ============================================
//@category("Surface")
//@subcategory("Vertex Colors")
//@property(name="_MainVertexColoringEnabled", display="Use Vertex Colors", mode=static, range=[0,1], toggle=true)
//@tooltip("Enables multiplication of the sampled base color by the mesh vertex colors.")
uniform float _MainVertexColoringEnabled;
//@property(name="_MainVertexColoringLinearSpace", display="Vertex Colors Are Linear", mode=static, range=[0,1], toggle=true)
//@tooltip("Treats incoming vertex colors as already converted to linear space before tinting the base color.")
uniform float _MainVertexColoringLinearSpace;
//@property(name="_MainVertexColoring", display="Color Blend", mode=static, range=[0,1])
//@tooltip("Controls how strongly vertex RGB contributes to the final albedo tint.")
uniform float _MainVertexColoring;
//@property(name="_MainUseVertexColorAlpha", display="Alpha Blend", mode=static, range=[0,1])
//@tooltip("Controls how strongly vertex alpha modulates the final surface alpha.")
uniform float _MainUseVertexColorAlpha;

// ============================================
// Outline (for outline pass)
// ============================================
//@category("Effects")
//@subcategory("Outline")
//@feature(id="outline", name="Outline", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_OUTLINE
//@property(name="_OutlineColor", display="Outline Color", mode=static)
uniform vec4 _OutlineColor;
//@property(name="_OutlineWidth", display="Outline Width", mode=static, range=[0,0.1])
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
#endif

// ============================================
// Back Face
// ============================================
//@category("Effects")
//@subcategory("Backface")
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
//@property(name="_BackFaceAlpha", display="Backface Alpha", mode=static, range=[0,1])
uniform float _BackFaceAlpha;
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
//@property(name="_GlitterDensity", display="Density", mode=static, range=[0,10])
uniform float _GlitterDensity;
//@property(name="_GlitterSize", display="Size", mode=static, range=[0,1])
uniform float _GlitterSize;
//@property(name="_GlitterSpeed", display="Scroll Speed", mode=animated, range=[0,10])
uniform float _GlitterSpeed;
//@property(name="_GlitterBrightness", display="Brightness", mode=static, range=[0,16])
uniform float _GlitterBrightness;
//@property(name="_GlitterMinAngle", display="Min View Angle", mode=static, range=[0,1])
uniform float _GlitterMinAngle;
//@property(name="_GlitterMaxAngle", display="Max View Angle", mode=static, range=[0,1])
uniform float _GlitterMaxAngle;
//@property(name="_GlitterRainbow", display="Rainbow Blend", mode=static, range=[0,1])
uniform float _GlitterRainbow;
//@property(name="_GlitterMask", display="Glitter Mask", slot=texture)
uniform sampler2D _GlitterMask;
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
//@property(name="_DissolveType", display="Dissolve Mode", mode=static, enum="0:Linear|1:Spherical|2:Directional")
uniform float _DissolveType;
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

#endif // TOON_UNIFORMS_GLSL
