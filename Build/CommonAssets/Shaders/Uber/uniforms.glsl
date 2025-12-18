// Uber Shader - Uniform Declarations

#ifndef TOON_UNIFORMS_GLSL
#define TOON_UNIFORMS_GLSL

// ============================================
// Transform Matrices (Provided by engine)
// ============================================
layout(std140) uniform TransformUBO {
    mat4 u_ModelMatrix;
    mat4 u_ViewMatrix;
    mat4 u_ProjectionMatrix;
    mat4 u_ModelViewMatrix;
    mat4 u_ModelViewProjectionMatrix;
    mat4 u_NormalMatrix;        // transpose(inverse(modelView)) or just model for world-space
    vec3 u_CameraPosition;
    float u_Time;
    vec4 u_ScreenParams;        // x: width, y: height, z: 1+1/width, w: 1+1/height
};

// ============================================
// Light Data (Forward rendering - single main light)
// ============================================
layout(std140) uniform LightUBO {
    vec3 u_LightDirection;      // Directional light direction (world space)
    float u_LightIntensity;
    vec3 u_LightColor;
    float u_AmbientIntensity;
    vec3 u_AmbientColor;
    float _padding1;
};

// ============================================
// Shadow Map (Directional light shadow)
// ============================================
layout(binding = 15) uniform sampler2D ShadowMap;
uniform bool ShadowMapEnabled;
uniform mat4 u_LightSpaceMatrix;    // World to light-space projection matrix

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
