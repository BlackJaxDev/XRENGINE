// Uber Shader - Uniform Declarations
// Uses engine-standard uniform names for compatibility

#ifndef TOON_UNIFORMS_GLSL
#define TOON_UNIFORMS_GLSL

// ============================================
// Transform Matrices (Provided by engine - matches EEngineUniform names)
// ============================================
uniform mat4 ModelMatrix;
uniform mat4 ViewMatrix;
uniform mat4 ProjMatrix;
uniform mat4 InverseViewMatrix;

// Camera (matches EEngineUniform.CameraPosition)
uniform vec3 CameraPosition;

// Time for animations (set by engine or custom)
uniform float Time;

// Screen parameters
uniform float ScreenWidth;
uniform float ScreenHeight;

// ============================================
// Convenience macros for compatibility with u_ prefix code
// ============================================
#define u_ModelMatrix ModelMatrix
#define u_ViewMatrix ViewMatrix
#define u_ProjectionMatrix ProjMatrix
#define u_ModelViewMatrix (ViewMatrix * ModelMatrix)
#define u_ModelViewProjectionMatrix (ProjMatrix * ViewMatrix * ModelMatrix)
#define u_CameraPosition CameraPosition
#define u_Time Time
#define u_ScreenParams vec4(ScreenWidth, ScreenHeight, 1.0 + 1.0/ScreenWidth, 1.0 + 1.0/ScreenHeight)

// ============================================
// Light Data (Forward rendering - from ForwardLighting snippet)
// Primary directional light info extracted from DirectionalLights[0]
// ============================================
struct BaseLight
{
    vec3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    mat4 WorldToLightSpaceProjMatrix;
};

struct DirLight
{
    BaseLight Base;
    vec3 Direction;
};

uniform vec3 GlobalAmbient;
uniform int DirLightCount; 
uniform DirLight DirectionalLights[2];

// ============================================
// Helper functions for uber shader light access
// ============================================
vec3 getLightDirection() {
    return DirLightCount > 0 ? DirectionalLights[0].Direction : vec3(0.0, -1.0, 0.0);
}

vec3 getLightColor() {
    return DirLightCount > 0 ? DirectionalLights[0].Base.Color : vec3(1.0);
}

float getLightIntensity() {
    return DirLightCount > 0 ? DirectionalLights[0].Base.DiffuseIntensity : 1.0;
}

mat4 getLightSpaceMatrix() {
    return DirLightCount > 0 ? DirectionalLights[0].Base.WorldToLightSpaceProjMatrix : mat4(1.0);
}

// Convenience macros that call the helper functions
#define u_LightDirection getLightDirection()
#define u_LightColor getLightColor()
#define u_LightIntensity getLightIntensity()
#define u_AmbientColor GlobalAmbient
#define u_AmbientIntensity 1.0
#define u_LightSpaceMatrix getLightSpaceMatrix()

// ============================================
// Shadow Map (Directional light shadow) - matches ForwardLighting snippet
// ============================================
layout(binding = 15) uniform sampler2D ShadowMap;
uniform bool ShadowMapEnabled;

// ============================================
// Main Texture Properties
// ============================================
uniform sampler2D _MainTex;
uniform vec4 _MainTex_ST;           // xy: tiling, zw: offset
uniform vec2 _MainTexPan;
uniform int _MainTexUV;

uniform vec4 _Color;                // Main color tint
uniform int _ColorThemeIndex;

// ============================================
// Normal Map
// ============================================
uniform sampler2D _BumpMap;
uniform vec4 _BumpMap_ST;
uniform vec2 _BumpMapPan;
uniform int _BumpMapUV;
uniform float _BumpScale;

// ============================================
// Alpha / Transparency
// ============================================
uniform sampler2D _AlphaMask;
uniform vec4 _AlphaMask_ST;
uniform vec2 _AlphaMaskPan;
uniform int _AlphaMaskUV;
uniform int _MainAlphaMaskMode;     // 0: Off, 1: Replace, 2: Multiply, 3: Add, 4: Subtract
uniform float _AlphaMaskBlendStrength;
uniform float _AlphaMaskValue;
uniform float _AlphaMaskInvert;

uniform float _Cutoff;              // Alpha cutoff threshold
uniform int _Mode;                  // Rendering mode (opaque, cutout, fade, etc.)
uniform float _AlphaForceOpaque;
uniform float _AlphaMod;

// ============================================
// Color Adjustments
// ============================================
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

// ============================================
// Shading / Lighting
// ============================================
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

// AO Maps
uniform sampler2D _LightingAOMaps;
uniform vec4 _LightingAOMaps_ST;
uniform vec2 _LightingAOMapsPan;
uniform int _LightingAOMapsUV;
uniform float _LightDataAOStrengthR;

// Shadow Mask
uniform sampler2D _LightingShadowMasks;
uniform vec4 _LightingShadowMasks_ST;
uniform float _LightingShadowMaskStrengthR;

// ============================================
// Emission
// ============================================
uniform float _EnableEmission;
uniform sampler2D _EmissionMap;
uniform vec4 _EmissionMap_ST;
uniform vec2 _EmissionMapPan;
uniform int _EmissionMapUV;
uniform vec4 _EmissionColor;
uniform float _EmissionStrength;

// Emission scrolling
uniform float _EmissionScrollingEnabled;
uniform vec2 _EmissionScrollingSpeed;
uniform float _EmissionScrollingVertexColor;

// ============================================
// Matcap
// ============================================
uniform float _MatcapEnable;
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

// ============================================
// Rim Lighting
// ============================================
uniform float _EnableRimLighting;
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

// ============================================
// Specular
// ============================================
uniform float _StylizedSpecular;
uniform sampler2D _SpecularMap;
uniform vec4 _SpecularMap_ST;
uniform vec4 _SpecularTint;
uniform float _SpecularSmoothness;
uniform float _SpecularStrength;
uniform int _SpecularType;          // 0: Realistic, 1: Toon, 2: Anisotropic

// ============================================
// Detail Textures
// ============================================
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

// ============================================
// Vertex Colors
// ============================================
uniform float _MainVertexColoringEnabled;
uniform float _MainVertexColoringLinearSpace;
uniform float _MainVertexColoring;
uniform float _MainUseVertexColorAlpha;

// ============================================
// Outline (for outline pass)
// ============================================
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

// ============================================
// Back Face
// ============================================
uniform float _EnableBackFace;
uniform vec4 _BackFaceColor;
uniform float _BackFaceBlendMode;
uniform sampler2D _BackFaceTexture;
uniform float _BackFaceEmission;
uniform float _BackFaceAlpha;

// ============================================
// Glitter / Sparkle
// ============================================
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

// ============================================
// Flipbook Animation
// ============================================
uniform float _EnableFlipbook;
uniform sampler2D _FlipbookTexture;
uniform float _FlipbookColumns;
uniform float _FlipbookRows;
uniform float _FlipbookFrameRate;
uniform float _FlipbookFrame;
uniform float _FlipbookManualFrame;
uniform float _FlipbookBlendMode;
uniform float _FlipbookCrossfade;

// ============================================
// Subsurface Scattering
// ============================================
uniform float _EnableSSS;
uniform vec4 _SSSColor;
uniform float _SSSPower;
uniform float _SSSDistortion;
uniform float _SSSScale;
uniform sampler2D _SSSThicknessMap;
uniform float _SSSAmbient;

// ============================================
// Dissolve
// ============================================
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

// ============================================
// Parallax / Height Mapping
// ============================================
uniform float _EnableParallax;
uniform float _ParallaxMode;
uniform sampler2D _ParallaxMap;
uniform vec4 _ParallaxMap_ST;
uniform float _ParallaxStrength;
uniform float _ParallaxMinSamples;
uniform float _ParallaxMaxSamples;
uniform float _ParallaxOffset;
uniform float _ParallaxMapChannel;

#endif // TOON_UNIFORMS_GLSL
