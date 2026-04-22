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
uniform sampler2D _AlphaMask;
uniform vec4 _AlphaMask_ST;
uniform vec2 _AlphaMaskPan;
uniform int _AlphaMaskUV;
uniform int _MainAlphaMaskMode;     // 0: Off, 1: Replace, 2: Multiply, 3: Add, 4: Subtract
uniform float _AlphaMaskBlendStrength;
uniform float _AlphaMaskValue;
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
uniform float _MainColorAdjustToggle;
uniform sampler2D _MainColorAdjustTexture;
uniform vec4 _MainColorAdjustTexture_ST;
uniform vec2 _MainColorAdjustTexturePan;
uniform int _MainColorAdjustTextureUV;
uniform float _Saturation;
uniform float _MainBrightness;
uniform float _MainHueShiftToggle;
uniform float _MainHueShift;
uniform float _MainHueShiftSpeed;
uniform int _MainHueShiftColorSpace;    // 0: OKLab, 1: HSV
uniform float _MainHueShiftReplace;
#endif

// ============================================
// Shading / Lighting
// ============================================
//@category("Lighting", order=20)
//@feature(id="stylized-shading", name="Stylized Lighting", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
uniform float _ShadingEnabled;
uniform int _LightingMode;          // 0: Ramp, 1: Multilayer, 2: Wrapped, 3: Skin, 4: ShadeMap, 5: Flat, 6: Realistic, 7: Cloth, 8: SDF

// Light Data options
uniform int _LightingColorMode;
uniform int _LightingMapMode;
uniform int _LightingDirectionMode;
uniform float _LightingCapEnabled;
uniform float _LightingCap;
uniform float _LightingMinLightBrightness;
uniform float _LightingMonochromatic;
uniform float _LightingIndirectUsesNormals;

// Shadow options
uniform vec3 _LightingShadowColor;
uniform float _ShadowStrength;
uniform float _LightingIgnoreAmbientColor;

// Texture Ramp shading (Mode 0)
uniform sampler2D _ToonRamp;
uniform float _ShadowOffset;

// Flat shading (Mode 5)
uniform float _ForceFlatRampedLightmap;

// Multilayer Math (Mode 1)
uniform sampler2D _ShadowColorTex;
uniform vec4 _ShadowColor;
uniform float _ShadowBorder;
uniform float _ShadowBlur;

// Wrapped shading (Mode 2)
uniform float _LightingWrappedWrap;
uniform float _LightingWrappedNormalization;
uniform float _LightingGradientStart;
uniform float _LightingGradientEnd;
#endif

// AO Maps
//@category("Lighting")
//@feature(id="material-ao", name="Material AO", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_MATERIAL_AO
uniform sampler2D _LightingAOMaps;
uniform vec4 _LightingAOMaps_ST;
uniform vec2 _LightingAOMapsPan;
uniform int _LightingAOMapsUV;
uniform float _LightDataAOStrengthR;
#endif

// Shadow Mask
//@category("Lighting")
//@feature(id="shadow-masks", name="Shadow Masks", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_SHADOW_MASKS
uniform sampler2D _LightingShadowMasks;
uniform vec4 _LightingShadowMasks_ST;
uniform float _LightingShadowMaskStrengthR;
#endif

// ============================================
// Emission
// ============================================
//@category("Effects", order=30)
//@feature(id="emission", name="Emission", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_EMISSION
uniform float _EnableEmission;
//@property(name="_EmissionMap", display="Emission Map", slot=texture)
//@tooltip("Texture sampled for emissive contribution.")
uniform sampler2D _EmissionMap;
uniform vec4 _EmissionMap_ST;
uniform vec2 _EmissionMapPan;
uniform int _EmissionMapUV;
//@property(name="_EmissionColor", display="Emission Color", mode=static)
uniform vec4 _EmissionColor;
//@property(name="_EmissionStrength", display="Emission Strength", mode=static, range=[0,8])
uniform float _EmissionStrength;

// Emission scrolling
uniform float _EmissionScrollingEnabled;
uniform vec2 _EmissionScrollingSpeed;
uniform float _EmissionScrollingVertexColor;
#endif

// ============================================
// Matcap
// ============================================
//@category("Effects")
//@feature(id="matcap", name="Matcap", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_MATCAP
uniform float _MatcapEnable;
//@property(name="_Matcap", display="Matcap Texture", slot=texture)
//@tooltip("Sphere-mapped texture layered over the lit surface.")
uniform sampler2D _Matcap;
uniform vec4 _MatcapColor;
uniform float _MatcapIntensity;
uniform float _MatcapBorder;
uniform int _MatcapUVMode;          // 0: UTS, 1: Top Pinch, 2: Double Sided
uniform float _MatcapReplace;
uniform float _MatcapMultiply;
uniform float _MatcapAdd;
uniform float _MatcapEmissionStrength;
uniform float _MatcapLightMask;
uniform float _MatcapNormal;

uniform sampler2D _MatcapMask;
uniform vec4 _MatcapMask_ST;
uniform int _MatcapMaskChannel;
uniform float _MatcapMaskInvert;
#endif

// ============================================
// Rim Lighting
// ============================================
//@category("Effects")
//@feature(id="rim-lighting", name="Rim Lighting", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
uniform float _EnableRimLighting;
//@property(name="_RimLightColor", display="Rim Color", mode=static)
uniform vec4 _RimLightColor;
uniform float _RimWidth;
uniform float _RimSharpness;
uniform float _RimLightColorBias;
uniform float _RimEmission;
uniform float _RimHideInShadow;
uniform int _RimStyle;              // 0: Poiyomi, 1: UTS2, 2: LilToon
uniform float _RimBlendStrength;
uniform int _RimBlendMode;

uniform sampler2D _RimMask;
uniform vec4 _RimMask_ST;
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

//@feature(id="advanced-specular", name="Advanced Specular", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
uniform float _StylizedSpecular;
uniform sampler2D _SpecularMap;
uniform vec4 _SpecularMap_ST;
uniform vec4 _SpecularTint;
uniform int _SpecularType;          // 0: Realistic, 1: Toon, 2: Anisotropic
#endif

// ============================================
// Detail Textures
// ============================================
//@category("Surface")
//@feature(id="detail-textures", name="Detail Textures", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES
uniform float _DetailEnabled;
uniform sampler2D _DetailMask;
uniform vec4 _DetailMask_ST;
uniform sampler2D _DetailTex;
uniform vec4 _DetailTex_ST;
uniform vec2 _DetailTexPan;
uniform vec3 _DetailTint;
uniform float _DetailTexIntensity;
uniform float _DetailBrightness;

uniform sampler2D _DetailNormalMap;
uniform vec4 _DetailNormalMap_ST;
uniform vec2 _DetailNormalMapPan;
uniform float _DetailNormalMapScale;
#endif

// ============================================
// Vertex Colors
// ============================================
//@category("Surface")
uniform float _MainVertexColoringEnabled;
uniform float _MainVertexColoringLinearSpace;
uniform float _MainVertexColoring;
uniform float _MainUseVertexColorAlpha;

// ============================================
// Outline (for outline pass)
// ============================================
//@category("Effects")
//@feature(id="outline", name="Outline", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_OUTLINE
uniform float _EnableOutline;
uniform vec4 _OutlineColor;
uniform float _OutlineWidth;
uniform sampler2D _OutlineMask;
uniform float _OutlineEmission;
uniform float _OutlineLit;
uniform float _OutlineDistanceFadeStart;
uniform float _OutlineDistanceFadeEnd;
uniform float _OutlineTextureTint;
uniform float _OutlineVertexColorTint;
#endif

// ============================================
// Back Face
// ============================================
//@category("Effects")
//@feature(id="backface", name="Backface Effects", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_BACKFACE
uniform float _EnableBackFace;
uniform vec4 _BackFaceColor;
uniform float _BackFaceBlendMode;
uniform sampler2D _BackFaceTexture;
uniform float _BackFaceEmission;
uniform float _BackFaceAlpha;
#endif

// ============================================
// Glitter / Sparkle
// ============================================
//@category("Effects")
//@feature(id="glitter", name="Glitter", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_GLITTER
uniform float _EnableGlitter;
uniform vec4 _GlitterColor;
uniform float _GlitterDensity;
uniform float _GlitterSize;
uniform float _GlitterSpeed;
uniform float _GlitterBrightness;
uniform float _GlitterMinAngle;
uniform float _GlitterMaxAngle;
uniform float _GlitterRainbow;
uniform sampler2D _GlitterMask;
#endif

// ============================================
// Flipbook Animation
// ============================================
//@category("Effects")
//@feature(id="flipbook", name="Flipbook", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_FLIPBOOK
uniform float _EnableFlipbook;
uniform sampler2D _FlipbookTexture;
uniform float _FlipbookColumns;
uniform float _FlipbookRows;
uniform float _FlipbookFrameRate;
uniform float _FlipbookFrame;
uniform float _FlipbookManualFrame;
uniform float _FlipbookBlendMode;
uniform float _FlipbookCrossfade;
#endif

// ============================================
// Subsurface Scattering
// ============================================
//@category("Lighting")
//@feature(id="subsurface", name="Subsurface", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_SUBSURFACE
uniform float _EnableSSS;
uniform vec4 _SSSColor;
uniform float _SSSPower;
uniform float _SSSDistortion;
uniform float _SSSScale;
uniform sampler2D _SSSThicknessMap;
uniform float _SSSAmbient;
#endif

// ============================================
// Dissolve
// ============================================
//@category("Effects")
//@feature(id="dissolve", name="Dissolve", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_DISSOLVE
uniform float _EnableDissolve;
uniform float _DissolveType;
uniform float _DissolveProgress;
uniform vec4 _DissolveEdgeColor;
uniform float _DissolveEdgeWidth;
uniform float _DissolveEdgeEmission;
uniform sampler2D _DissolveNoiseTexture;
uniform vec4 _DissolveNoiseTexture_ST;
uniform float _DissolveNoiseStrength;
uniform vec3 _DissolveStartPoint;
uniform vec3 _DissolveEndPoint;
uniform float _DissolveInvert;
uniform float _DissolveCutoff;
#endif

// ============================================
// Parallax / Height Mapping
// ============================================
//@category("Surface")
//@feature(id="parallax", name="Parallax", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_PARALLAX
uniform float _EnableParallax;
uniform float _ParallaxMode;
//@property(name="_ParallaxMap", display="Height Map", slot=texture)
//@tooltip("Height texture used for parallax or relief sampling.")
uniform sampler2D _ParallaxMap;
uniform vec4 _ParallaxMap_ST;
uniform float _ParallaxStrength;
uniform float _ParallaxMinSamples;
uniform float _ParallaxMaxSamples;
uniform float _ParallaxOffset;
uniform float _ParallaxMapChannel;
#endif

#endif // TOON_UNIFORMS_GLSL
