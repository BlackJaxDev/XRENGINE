// =====================================================
// XREngine Shader Snippets - README
// =====================================================
//
// This directory contains reusable shader code snippets that can be
// included in any shader using the #pragma snippet directive.
//
// Snippets are LAZY-LOADED on first use - they are not loaded until
// a shader actually requests them, minimizing startup time.
//
// USAGE:
//   #pragma snippet "SnippetName"
//   #pragma snippet <SnippetName>
//
// EXAMPLE:
//   #version 450
//   
//   // Include forward lighting functions
//   #pragma snippet "ForwardLighting"
//   
//   layout(location = 0) out vec4 FragColor;
//   layout(location = 0) in vec3 FragPos;
//   layout(location = 1) in vec3 Normal;
//   
//   uniform vec4 AlbedoColor;
//   uniform float SpecularIntensity;
//   
//   void main()
//   {
//       vec3 lighting = XRENGINE_CalculateForwardLighting(
//           normalize(Normal),
//           FragPos,
//           AlbedoColor.rgb,
//           SpecularIntensity,
//           1.0  // ambient occlusion
//       );
//       FragColor = vec4(lighting, AlbedoColor.a);
//   }
//
// ENGINE SNIPPETS (in this directory):
//   - ForwardLighting.glsl     : Complete forward lighting with Forward+ support
//   - ForwardLightingPBR.glsl  : PBR lighting functions (use with ForwardLighting)
//   - PBRFunctions.glsl        : Cook-Torrance BRDF, Fresnel, GGX, IBL helpers
//   - ParallaxMapping.glsl     : Parallax occlusion mapping (POM) functions
//   - DepthUtils.glsl          : Depth buffer linearization and reconstruction
//   - ColorConversion.glsl     : RGB/HSV, sRGB/Linear conversion
//   - MathUtils.glsl           : Common math functions and constants
//   - NormalMapping.glsl       : TBN matrix and normal map unpacking
//   - ShadowSampling.glsl      : PCF shadow sampling utilities
//   - ToneMapping.glsl         : Various tone mapping operators (Linear, Gamma,
//                                Clip, Reinhard, Hable, Mobius, ACES, Neutral, Filmic)
//
// CREATING CUSTOM SNIPPETS:
//   1. Create a .glsl or .snip file in this directory (or a subdirectory)
//   2. Name the file with the snippet name (e.g., MySnippet.glsl)
//   3. The snippet will be lazy-loaded when first requested as:
//      #pragma snippet "MySnippet"
//
// FUNCTION NAMING:
//   All engine functions are prefixed with XRENGINE_ to avoid conflicts
//   with user code.
//
// =====================================================
