#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex; //HDR scene color
uniform sampler2D BloomBlurTexture; //Bloom
uniform sampler2D DepthView; //Depth
uniform usampler2D StencilView; //Stencil

// 1x1 R32F texture containing the current exposure value (GPU-driven auto exposure)
uniform sampler2D AutoExposureTex;
// Volumetric fog scatter texture. rgb = in-scattered radiance, a = transmittance.
// Produced by VolumetricFogScatter.fs; early-outs to (0,0,0,1) when the effect
// is disabled so the composite becomes a no-op.
uniform sampler2D VolumetricFogColor;
uniform bool UseGpuAutoExposure;
uniform vec3 CameraPosition;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform int DirLightCount;
uniform float RenderTime;

uniform vec3 HoverOutlineColor = vec3(1.0f, 1.0f, 0.0f);
uniform vec3 SelectionOutlineColor = vec3(0.0f, 1.0f, 0.0f);
uniform bool OutputHDR;

struct VignetteStruct
{
    vec3 Color;
    float Intensity;
    float Power;
};
uniform VignetteStruct Vignette;

struct ColorGradeStruct
{
    vec3 Tint;

    float Exposure;
    float Contrast;
    float Gamma;

    float Hue; //1.0f = no change, 0.0f = red
    float Saturation; //1.0f = no change, 0.0f = grayscale
    float Brightness; //1.0f = no change, 0.0f = black
};
uniform ColorGradeStruct ColorGrade;

float GetExposure()
{
  if (UseGpuAutoExposure)
  {
    float e = texelFetch(AutoExposureTex, ivec2(0, 0), 0).r;
    if (!(isnan(e) || isinf(e)) && e > 0.0)
      return e;
  }
  return ColorGrade.Exposure;
}

uniform float ChromaticAberrationIntensity;

struct DepthFogStruct
{
    float Intensity; //0.0f = no fog, 1.0f = full fog
    float Start; //Start distance of fog
    float End; //End distance of fog
    vec3 Color; //Color of fog
};
uniform DepthFogStruct DepthFog;

// Lens distortion mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini, 4=BrownConrady
uniform int LensDistortionMode;
uniform float LensDistortionIntensity;
uniform vec2 LensDistortionCenter;
uniform float PaniniDistance;
uniform float PaniniCrop;
uniform vec2 PaniniViewExtents; // tan(fov/2) * aspect, tan(fov/2)

// Brown-Conrady coefficients
uniform vec3 BrownConradyRadial;     // k1,k2,k3
uniform vec2 BrownConradyTangential; // p1,p2

// Bloom combine controls
uniform float BloomStrength = 0.15;
uniform int BloomStartMip = 1;
uniform int BloomEndMip = 1;
uniform float BloomLodWeights[5] = float[](0.0, 1.0, 0.0, 0.0, 0.0);
uniform bool DebugBloomOnly = false;

uniform int DepthMode;

float XRENGINE_ResolveDepth(float depth)
{
  return DepthMode == 1 ? (1.0f - depth) : depth;
}

vec3 XRENGINE_WorldPosFromDepthRaw(float depth, vec2 uv, mat4 invProj, mat4 invView)
{
  vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
  vec4 viewSpacePosition = invProj * clipSpacePosition;
  viewSpacePosition /= viewSpacePosition.w;
  return (invView * viewSpacePosition).xyz;
}

vec3 RGBtoHSV(vec3 c)
{
    vec4 K = vec4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10f;
    return vec3(abs(q.z + (q.w - q.y) / (6.0f * d + e)), d / (q.x + e), q.x);
}
vec3 HSVtoRGB(vec3 c)
{
    vec4 K = vec4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0f - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0f, 1.0f), c.y);
}
vec3 ApplyHsvColorGrade(vec3 sceneColor)
{
  if (ColorGrade.Hue == 1.0f && ColorGrade.Saturation == 1.0f && ColorGrade.Brightness == 1.0f)
    return sceneColor;

  vec3 hsv = RGBtoHSV(max(sceneColor, vec3(0.0f)));
  hsv.x = fract(hsv.x * ColorGrade.Hue);
  hsv.y = clamp(hsv.y * ColorGrade.Saturation, 0.0f, 1.0f);
  hsv.z = max(hsv.z * ColorGrade.Brightness, 0.0f);
  return HSVtoRGB(hsv);
}
float rand(vec2 coord)
{
    return fract(sin(dot(coord, vec2(12.9898f, 78.233f))) * 43758.5453f);
}
float interleavedGradientNoise(vec2 pixelCoord)
{
  return fract(52.9829189f * fract(dot(pixelCoord, vec2(0.06711056f, 0.00583715f))));
}
float saturate(float value)
{
  return clamp(value, 0.0f, 1.0f);
}
vec3 ApplyVignette(vec3 sceneColor, vec2 uv)
{
  if (Vignette.Intensity <= 0.0f)
    return sceneColor;

  vec2 centeredUv = (uv - LensDistortionCenter) * 2.0f;
  float radius = saturate(length(centeredUv) * 0.70710678f);
  float vignetteFactor = pow(radius, max(Vignette.Power, 0.0001f)) * saturate(Vignette.Intensity);
  return mix(sceneColor, Vignette.Color, vignetteFactor);
}
vec2 ApplyLensDistortionByMode(vec2 uv);
float GetStencilMaskBit(uint stencilValue, uint bit)
{
  return (stencilValue & bit) != 0u ? 1.0f : 0.0f;
}

vec2 GetStencilOutlineIntensity(vec2 uv)
{
  int outlineSize = 3;
  ivec2 texSize = textureSize(StencilView, 0);
  vec2 texelSize = 1.0f / vec2(texSize);
    vec2 texelX = vec2(texelSize.x, 0.0f);
    vec2 texelY = vec2(0.0f, texelSize.y);

    uint stencilCurrent = texture(StencilView, uv).r;
  float currentHover = GetStencilMaskBit(stencilCurrent, 1u);
  float currentSelection = GetStencilMaskBit(stencilCurrent, 2u);

  float hoverOutline = 0.0f;
  float selectionOutline = 0.0f;

    vec2 zero = vec2(0.0f);
  vec2 one = vec2(1.0f);

    for (int i = 1; i <= outlineSize; ++i)
    {
      float step = float(i);
      vec2 yPos = clamp(uv + texelY * step, zero, one);
      vec2 yNeg = clamp(uv - texelY * step, zero, one);
      vec2 xPos = clamp(uv + texelX * step, zero, one);
      vec2 xNeg = clamp(uv - texelX * step, zero, one);

      uint sYPos = texture(StencilView, yPos).r;
      uint sYNeg = texture(StencilView, yNeg).r;
      uint sXPos = texture(StencilView, xPos).r;
      uint sXNeg = texture(StencilView, xNeg).r;

      if (currentHover == 0.0f)
      {
        hoverOutline = max(hoverOutline, GetStencilMaskBit(sYPos, 1u));
        hoverOutline = max(hoverOutline, GetStencilMaskBit(sYNeg, 1u));
        hoverOutline = max(hoverOutline, GetStencilMaskBit(sXPos, 1u));
        hoverOutline = max(hoverOutline, GetStencilMaskBit(sXNeg, 1u));
      }

      if (currentSelection == 0.0f)
      {
        selectionOutline = max(selectionOutline, GetStencilMaskBit(sYPos, 2u));
        selectionOutline = max(selectionOutline, GetStencilMaskBit(sYNeg, 2u));
        selectionOutline = max(selectionOutline, GetStencilMaskBit(sXPos, 2u));
        selectionOutline = max(selectionOutline, GetStencilMaskBit(sXNeg, 2u));
      }
    }

  return vec2(hoverOutline, selectionOutline);
}

// Tonemapping selector and shared tonemap operators
#include "../Snippets/ToneMapping.glsl"

uniform int TonemapType = XRENGINE_TONEMAP_MOBIUS;
uniform float MobiusTransition = 0.6f;

vec2 ApplyLensDistortion(vec2 uv, float intensity, vec2 center)
{
  uv -= center;
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
  return center + vec2(sin(uva), cos(uva)) * uvd;
}

vec2 ApplyBrownConrady(vec2 uvCentered)
{
  // Work in normalized coordinates around 0.
  vec2 x = uvCentered * 2.0 - 1.0;
  float r2 = dot(x, x);
  float r4 = r2 * r2;
  float r6 = r4 * r2;

  float k1 = BrownConradyRadial.x;
  float k2 = BrownConradyRadial.y;
  float k3 = BrownConradyRadial.z;
  float p1 = BrownConradyTangential.x;
  float p2 = BrownConradyTangential.y;

  float radial = 1.0 + k1 * r2 + k2 * r4 + k3 * r6;
  vec2 tangential = vec2(
    2.0 * p1 * x.x * x.y + p2 * (r2 + 2.0 * x.x * x.x),
    p1 * (r2 + 2.0 * x.y * x.y) + 2.0 * p2 * x.x * x.y);

  vec2 xd = x * radial + tangential;
  return xd * 0.5 + 0.5;
}

// Panini projection - preserves vertical lines while compressing horizontal periphery
// Based on Unity's implementation from the Stockholm demo team
// d = 1.0 is "unit distance" (simplified), d != 1.0 is "generic" panini
vec2 ApplyPaniniProjection(vec2 view_pos, float d)
{
    // Generic Panini projection
    // Given a point on the image plane, project it onto a cylinder
    // then back onto the image plane from a different viewpoint
    
    float view_dist = 1.0 + d;
    float view_hyp_sq = view_pos.x * view_pos.x + view_dist * view_dist;
    
    float isect_D = view_pos.x * d;
    float isect_discrim = view_hyp_sq - isect_D * isect_D;
    
    float cyl_dist_minus_d = (-isect_D * view_pos.x + view_dist * sqrt(max(isect_discrim, 0.0))) / view_hyp_sq;
    float cyl_dist = cyl_dist_minus_d + d;
    
    vec2 cyl_pos = view_pos * (cyl_dist / view_dist);
    return cyl_pos / (cyl_dist - d);
}

vec2 ApplyLensDistortionByMode(vec2 uv)
{
  // Recenter so principal point maps to UV 0.5,0.5 for distortion models.
  vec2 uvCentered = uv - LensDistortionCenter + vec2(0.5);

    // Mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini, 4=BrownConrady
    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
      return ApplyLensDistortion(uvCentered, LensDistortionIntensity, vec2(0.5));
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            // Convert UV [0,1] to view position using view extents
            // PaniniViewExtents contains (tan(fov/2) * aspect, tan(fov/2))
            // PaniniCrop is the scale factor for crop-to-fit
            vec2 view_pos = (2.0 * uvCentered - 1.0) * PaniniViewExtents * PaniniCrop;
            vec2 proj_pos = ApplyPaniniProjection(view_pos, PaniniDistance);
            // Convert back to UV
            vec2 proj_ndc = proj_pos / PaniniViewExtents;
            vec2 outCentered = proj_ndc * 0.5 + 0.5;
            return outCentered - vec2(0.5) + LensDistortionCenter;
        }
    }
    else if (LensDistortionMode == 4)
    {
      vec2 outCentered = ApplyBrownConrady(uvCentered);
      return outCentered - vec2(0.5) + LensDistortionCenter;
    }
    return uv;
}

vec3 SampleHDR(vec2 uv)
{
  vec2 duv = ApplyLensDistortionByMode(uv);
  return texture(HDRSceneTex, duv).rgb;
}
vec3 SampleBloom(vec2 uv, float lod)
{
  vec2 duv = ApplyLensDistortionByMode(uv);
  return textureLod(BloomBlurTexture, duv, lod).rgb;
}

void main()
{
  vec2 uv = FragPos.xy;
  if (uv.x > 1.0f || uv.y > 1.0f)
      discard;
  //Normalize uv from [-1, 1] to [0, 1]
  uv = uv * 0.5f + 0.5f;
  
  //Perform HDR operations
  vec3 hdrSceneColor;
  
  //Apply chromatic aberration with screen-space offsets
  if (ChromaticAberrationIntensity > 0.0f)
  {
      // Direction from center of screen
      vec2 dir = uv - LensDistortionCenter;
      // Scale by intensity directly (0-1 range produces visible offset)
      vec2 off = dir * ChromaticAberrationIntensity * 0.1;
  
      // Clamp UVs to avoid sampling outside [0,1]
      vec2 uvR = clamp(uv + off, vec2(0.0f), vec2(1.0f));
      vec2 uvG = uv;
      vec2 uvB = clamp(uv - off, vec2(0.0f), vec2(1.0f));
  
      float r = SampleHDR(uvR).r;
      float g = SampleHDR(uvG).g;
      float b = SampleHDR(uvB).b;
      hdrSceneColor = vec3(r, g, b);
  }
  else
  {
      hdrSceneColor = SampleHDR(uv);
  }
  
  //Add bloom with configurable range/weights, scaled by overall strength
  if (DebugBloomOnly)
  {
    // Diagnostic: 2x2 grid showing bloom texture mip levels 0-3.
    //   Top-left  = mip 0 (scene copy, red border)
    //   Top-right = mip 1 (threshold-filtered downsample, green border)
    //   Bot-left  = mip 2 (further downsample, blue border)
    //   Bot-right = mip 3 (further downsample, yellow border)
    // If mip 0 and mip 1 look identical, either textureLod isn't
    // distinguishing mips (GL_TEXTURE_MAX_LEVEL issue) or the
    // downsample pass is not writing threshold-filtered content.
    int col = uv.x < 0.5 ? 0 : 1;
    int row = uv.y < 0.5 ? 0 : 1;
    int mip = row * 2 + col; // 0=TL, 1=TR, 2=BL, 3=BR
    vec2 cellUV = fract(uv * 2.0);

    // Thin colored border per quadrant for identification.
    float border = 0.005;
    bool onBorder = cellUV.x < border || cellUV.x > (1.0 - border)
                 || cellUV.y < border || cellUV.y > (1.0 - border);
    vec3 borderColors[4] = vec3[](
        vec3(1.0, 0.0, 0.0),   // mip 0: red
        vec3(0.0, 1.0, 0.0),   // mip 1: green
        vec3(0.0, 0.0, 1.0),   // mip 2: blue
        vec3(1.0, 1.0, 0.0)    // mip 3: yellow
    );

    if (onBorder)
    {
        OutColor = vec4(borderColors[mip], 1.0);
    }
    else
    {
        // Sample the bloom texture at the quadrant's mip level using the cell UV.
        vec3 mipColor = textureLod(BloomBlurTexture, cellUV, float(mip)).rgb;
        OutColor = vec4(mipColor, 1.0);
    }
    return;
  }
  else if (BloomStrength > 0.0f)
  {
    int startMip = clamp(BloomStartMip, 0, 4);
    int endMip = clamp(BloomEndMip, startMip, 4);
    for (int lod = startMip; lod <= endMip; ++lod)
    {
      float w = BloomLodWeights[lod];
      if (w > 0.0f)
        hdrSceneColor += SampleBloom(uv, float(lod)) * w * BloomStrength;
    }
  }

  // Safe composite: when the fog scatter pass is disabled, skipped, or has not yet written
  // a frame, the texture clears to (0,0,0,0). A literal `hdrSceneColor * 0 + 0` would zero out
  // the entire scene. Only apply the composite when the fog pass has written meaningful
  // transmittance/scatter data (alpha > 0 OR rgb > 0). A fully opaque fog volume that wants
  // to occlude the scene must still write a non-zero alpha (transmittance) or non-zero rgb.
  vec4 volumetricFog = texture(VolumetricFogColor, uv);
  if (volumetricFog.a > 0.0f || any(greaterThan(volumetricFog.rgb, vec3(0.0f))))
    hdrSceneColor = hdrSceneColor * volumetricFog.a + volumetricFog.rgb;

  //Tone mapping / HDR selection
  vec3 sceneColor;
  if (OutputHDR)
  {
      sceneColor = hdrSceneColor * GetExposure();
  }
  else
  {
      sceneColor = XRENGINE_ApplyToneMap(hdrSceneColor, TonemapType, GetExposure(), ColorGrade.Gamma, MobiusTransition);
  }

  //Apply depth-based fog
  if (DepthFog.Intensity > 0.0f)
  {
      float depth = texture(DepthView, uv).r;
      float fogFactor = clamp((depth - DepthFog.Start) / (DepthFog.End - DepthFog.Start), 0.0f, 1.0f);
      sceneColor = mix(sceneColor, DepthFog.Color, fogFactor * DepthFog.Intensity);
  }

	//Color grading
	sceneColor *= ColorGrade.Tint;

	sceneColor = ApplyHsvColorGrade(sceneColor);
	sceneColor = (sceneColor - 0.5f) * ColorGrade.Contrast + 0.5f;

  vec2 outline = GetStencilOutlineIntensity(uv);
  float hoverOutline = outline.x;
  float selectionOutline = outline.y;
  float outlineWeight = max(hoverOutline, selectionOutline);
  vec3 outlineColor = mix(SelectionOutlineColor, HoverOutlineColor, hoverOutline);
  sceneColor = mix(sceneColor, outlineColor, outlineWeight);

  // DEBUG: Visualize raw stencil value - uncomment to see stencil data
  // uint rawStencil = texture(StencilView, uv).r;
  // if ((rawStencil & 1) != 0) sceneColor = vec3(1.0, 0.0, 0.0); // Red where stencil bit 0 is set

	sceneColor = ApplyVignette(sceneColor, uv);

  if (!OutputHDR)
  {
	  //Gamma-correct
	  sceneColor = pow(max(sceneColor, vec3(0.0f)), vec3(1.0f / max(ColorGrade.Gamma, 0.0001f)));

    //Fix subtle banding by applying fine noise
    sceneColor += mix(-0.5f / 255.0f, 0.5f / 255.0f, rand(uv));
  }

	OutColor = vec4(sceneColor, 1.0f);
}
