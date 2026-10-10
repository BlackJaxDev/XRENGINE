// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    uint UseGpuAutoExposure;
    float3 HoverOutlineColor;
    float3 SelectionOutlineColor;
    uint EnableEditorOutline;
    uint OutputHDR;
    float3 VignetteColor;
    float VignetteIntensity;
    float VignettePower;
    float3 ColorGradeTint;
    float ColorGradeExposure;
    float ColorGradeContrast;
    float ColorGradeGamma;
    float ColorGradeHue;
    float ColorGradeSaturation;
    float ColorGradeBrightness;
    float ChromaticAberrationIntensity;
    float DepthFogIntensity;
    float DepthFogStart;
    float DepthFogEnd;
    float3 DepthFogColor;
    float2 LensDistortionCenter;
    float BloomStrength;
    int BloomStartMip;
    int BloomEndMip;
    float BloomLodWeight0;
    float BloomLodWeight1;
    float BloomLodWeight2;
    float BloomLodWeight3;
    float BloomLodWeight4;
    uint DebugBloomOnly;
    int TonemapType;
    float MobiusTransition;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> HDRSceneTex;
[[vk::binding(1, 1)]] SamplerState HDRSceneTexSampler;
[[vk::binding(2, 1)]] Texture2D<float4> BloomBlurTexture;
[[vk::binding(3, 1)]] SamplerState BloomBlurTextureSampler;
[[vk::binding(4, 1)]] DepthTexture2D DepthView;
[[vk::binding(5, 1)]] Texture2D<uint4> StencilView;
[[vk::binding(6, 1)]] Texture2D<float4> AutoExposureTex;
[[vk::binding(8, 1)]] Texture2D<float4> AtmosphereColor;
[[vk::binding(9, 1)]] SamplerState AtmosphereColorSampler;
[[vk::binding(10, 1)]] Texture2D<float4> VolumetricFogColor;
[[vk::binding(11, 1)]] SamplerState VolumetricFogColorSampler;
[[vk::binding(12, 1)]] Texture2D<uint4> AdvancedVisibilityMetadata;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output; output.position = float4(input.position.xy, 0.0, 1.0); return output;
}
#define ScreenWidth outputUniforms.ScreenWidth
#define ScreenHeight outputUniforms.ScreenHeight
#define ScreenOrigin outputUniforms.ScreenOrigin
#define UseGpuAutoExposure (outputUniforms.UseGpuAutoExposure != 0u)
#define HoverOutlineColor outputUniforms.HoverOutlineColor
#define SelectionOutlineColor outputUniforms.SelectionOutlineColor
#define EnableEditorOutline (outputUniforms.EnableEditorOutline != 0u)
#define OutputHDR (outputUniforms.OutputHDR != 0u)
#define VignetteColor outputUniforms.VignetteColor
#define VignetteIntensity outputUniforms.VignetteIntensity
#define VignettePower outputUniforms.VignettePower
#define ColorGradeTint outputUniforms.ColorGradeTint
#define ColorGradeExposure outputUniforms.ColorGradeExposure
#define ColorGradeContrast outputUniforms.ColorGradeContrast
#define ColorGradeGamma outputUniforms.ColorGradeGamma
#define ColorGradeHue outputUniforms.ColorGradeHue
#define ColorGradeSaturation outputUniforms.ColorGradeSaturation
#define ColorGradeBrightness outputUniforms.ColorGradeBrightness
#define ChromaticAberrationIntensity outputUniforms.ChromaticAberrationIntensity
#define DepthFogIntensity outputUniforms.DepthFogIntensity
#define DepthFogStart outputUniforms.DepthFogStart
#define DepthFogEnd outputUniforms.DepthFogEnd
#define DepthFogColor outputUniforms.DepthFogColor
#define LensDistortionCenter outputUniforms.LensDistortionCenter
#define BloomStrength outputUniforms.BloomStrength
#define BloomStartMip outputUniforms.BloomStartMip
#define BloomEndMip outputUniforms.BloomEndMip
#define BloomLodWeight0 outputUniforms.BloomLodWeight0
#define BloomLodWeight1 outputUniforms.BloomLodWeight1
#define BloomLodWeight2 outputUniforms.BloomLodWeight2
#define BloomLodWeight3 outputUniforms.BloomLodWeight3
#define BloomLodWeight4 outputUniforms.BloomLodWeight4
#define DebugBloomOnly (outputUniforms.DebugBloomOnly != 0u)
#define TonemapType outputUniforms.TonemapType
#define MobiusTransition outputUniforms.MobiusTransition

int2 LoadCoordinate(float2 uv)
{
    uint width, height; StencilView.GetDimensions(width, height);
    return min(int2(saturate(uv) * float2(width, height)), int2(width, height) - 1);
}
float GetBloomWeight(int level)
{
    if (level == 0) return BloomLodWeight0;
    if (level == 1) return BloomLodWeight1;
    if (level == 2) return BloomLodWeight2;
    if (level == 3) return BloomLodWeight3;
    return BloomLodWeight4;
}
float GetExposure()
{
    if (UseGpuAutoExposure)
    {
        float e = AutoExposureTex.Load(int3(0, 0, 0)).r;
        if (!(isnan(e) || isinf(e)) && e > 0.0) return e;
    }
    float e = ColorGradeExposure;
    return (isnan(e) || isinf(e) || e <= 0.0) ? 1.0 : e;
}
float3 RGBtoHSV(float3 c)
{
    float4 K = float4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10f;
    return float3(abs(q.z + (q.w - q.y) / (6.0f * d + e)), d / (q.x + e), q.x);
}
float3 HSVtoRGB(float3 c)
{
    float4 K = float4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0f - K.www);
    return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0f, 1.0f), c.y);
}
float3 ApplyHsvColorGrade(float3 sceneColor)
{
  if (ColorGradeHue == 1.0f && ColorGradeSaturation == 1.0f && ColorGradeBrightness == 1.0f)
    return sceneColor;

  float3 hsv = RGBtoHSV(max(sceneColor, float3(0.0f)));
  hsv.x = frac(hsv.x * ColorGradeHue);
  hsv.y = clamp(hsv.y * ColorGradeSaturation, 0.0f, 1.0f);
  hsv.z = max(hsv.z * ColorGradeBrightness, 0.0f);
  return HSVtoRGB(hsv);
}
float rand(float2 coord)
{
    return frac(sin(dot(coord, float2(12.9898f, 78.233f))) * 43758.5453f);
}
float interleavedGradientNoise(float2 pixelCoord)
{
  return frac(52.9829189f * frac(dot(pixelCoord, float2(0.06711056f, 0.00583715f))));
}

bool IsFiniteVec3(float3 value)
{
  return !any(isnan(value)) && !any(isinf(value));
}
bool IsFiniteVec4(float4 value)
{
  return !any(isnan(value)) && !any(isinf(value));
}
float3 SafeColor(float3 value)
{
  return IsFiniteVec3(value) ? max(value, float3(0.0f)) : float3(0.0f);
}
float4 SafeCompositeSample(float4 value, float4 fallback)
{
  return IsFiniteVec4(value) ? value : fallback;
}
float3 ApplyVignette(float3 sceneColor, float2 uv)
{
  if (VignetteIntensity <= 0.0f)
    return sceneColor;

  float2 centeredUv = (uv - LensDistortionCenter) * 2.0f;
  float radius = saturate(length(centeredUv) * 0.70710678f);
  float vignetteFactor = pow(radius, max(VignettePower, 0.0001f)) * saturate(VignetteIntensity);
  return lerp(sceneColor, VignetteColor, vignetteFactor);
}


float2 SceneSourceUv(float2 uv)
{
  return uv;
}

uint SampleSceneStencilSource(float2 sourceUv)
{
  return StencilView.Load(int3(LoadCoordinate(sourceUv), 0)).r;
}

uint SampleSceneEditorHighlightSource(float2 sourceUv)
{
  uint stencilFlags = SampleSceneStencilSource(sourceUv) & 3u;

  uint metadata = AdvancedVisibilityMetadata.Load(int3(LoadCoordinate(sourceUv), 0)).r;
  uint metadataFlags = metadata == 0xFFFFFFFFu ? 0u : (metadata >> 25u) & 3u;
  return stencilFlags | metadataFlags;

}

float GetStencilMaskBit(uint stencilValue, uint bit)
{
  return (stencilValue & bit) != 0u ? 1.0f : 0.0f;
}

float2 GetStencilOutlineIntensity(float2 sourceUv)
{
  int outlineSize = 3;
  uint width, height; StencilView.GetDimensions(width, height); int2 texSize = int2(width, height);
  float2 texelSize = 1.0f / float2(texSize);
    float2 texelX = float2(texelSize.x, 0.0f);
    float2 texelY = float2(0.0f, texelSize.y);

    uint stencilCurrent = SampleSceneEditorHighlightSource(sourceUv);
  float currentHover = GetStencilMaskBit(stencilCurrent, 1u);
  float currentSelection = GetStencilMaskBit(stencilCurrent, 2u);

  float hoverOutline = 0.0f;
  float selectionOutline = 0.0f;

    float2 zero = float2(0.0f);
  float2 one = float2(1.0f);

    for (int i = 1; i <= outlineSize; ++i)
    {
      float step = float(i);
      float2 yPos = clamp(sourceUv + texelY * step, zero, one);
      float2 yNeg = clamp(sourceUv - texelY * step, zero, one);
      float2 xPos = clamp(sourceUv + texelX * step, zero, one);
      float2 xNeg = clamp(sourceUv - texelX * step, zero, one);

      uint sYPos = SampleSceneEditorHighlightSource(yPos);
      uint sYNeg = SampleSceneEditorHighlightSource(yNeg);
      uint sXPos = SampleSceneEditorHighlightSource(xPos);
      uint sXNeg = SampleSceneEditorHighlightSource(xNeg);

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

  return float2(hoverOutline, selectionOutline);
}

#include "ToneMapping.slang"

float3 SampleHDR(float2 uv)
{
  float2 duv = SceneSourceUv(uv);
  return SafeColor(HDRSceneTex.SampleLevel(HDRSceneTexSampler, duv, 0.0).rgb);
}
float3 SampleBloom(float2 uv, float lod)
{
  float2 duv = SceneSourceUv(uv);
  return SafeColor(BloomBlurTexture.SampleLevel(BloomBlurTextureSampler, duv, lod).rgb);
}

[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
  float2 uv = clamp(
      (input.position.xy - ScreenOrigin) / max(float2(ScreenWidth, ScreenHeight), float2(1.0)),
      float2(0.0f),
      float2(1.0f));
  float2 sceneUv = SceneSourceUv(uv);

  // Gizmo bypass: pixels tagged with the gizmo stencil bit (0x80) skip tonemap,
  // color grading, vignette, exposure, atmosphere, fog, and bloom add. They are
  // emitted as raw HDR scene color so editor gizmos look unprocessed.
  if ((SampleSceneStencilSource(sceneUv) & 0x80u) != 0u)
  {
      return float4(SafeColor(HDRSceneTex.SampleLevel(HDRSceneTexSampler, sceneUv, 0.0).rgb), 1.0f);
  }

  //Perform HDR operations
  float3 hdrSceneColor;

  //Apply chromatic aberration with screen-space offsets
  if (ChromaticAberrationIntensity > 0.0f)
  {
      // Direction from center of screen
      float2 dir = uv - LensDistortionCenter;
      // Scale by intensity directly (0-1 range produces visible offset)
      float2 off = dir * ChromaticAberrationIntensity * 0.1;

      // Clamp UVs to avoid sampling outside [0,1]
      float2 uvR = clamp(uv + off, float2(0.0f), float2(1.0f));
      float2 uvG = uv;
      float2 uvB = clamp(uv - off, float2(0.0f), float2(1.0f));

      float r = SampleHDR(uvR).r;
      float g = SampleHDR(uvG).g;
      float b = SampleHDR(uvB).b;
      hdrSceneColor = float3(r, g, b);
  }
  else
  {
      hdrSceneColor = SampleHDR(uv);
  }

  //Add bloom with configurable range/weights, scaled by overall strength
  if (DebugBloomOnly)
  {
    // Diagnostic: 2x2 grid showing bloom texture mip levels 0-3.
    //   Top-left  = mip 0 (scene copy, red borderWidth)
    //   Top-right = mip 1 (threshold-filtered downsample, green borderWidth)
    //   Bot-left  = mip 2 (further downsample, blue borderWidth)
    //   Bot-right = mip 3 (further downsample, yellow borderWidth)
    // If mip 0 and mip 1 look identical, either textureLod isn't
    // distinguishing mips (GL_TEXTURE_MAX_LEVEL issue) or the
    // downsample pass is not writing threshold-filtered content.
    int col = uv.x < 0.5 ? 0 : 1;
    int row = uv.y < 0.5 ? 0 : 1;
    int mip = row * 2 + col; // 0=TL, 1=TR, 2=BL, 3=BR
    float2 cellUV = frac(uv * 2.0);

    // Thin colored borderWidth per quadrant for identification.
    float borderWidth = 0.005;
    bool onBorder = (cellUV.x < borderWidth) || (cellUV.x > (1.0 - borderWidth))
                 || (cellUV.y < borderWidth) || (cellUV.y > (1.0 - borderWidth));
    float3 borderColors[4] = {
        float3(1.0, 0.0, 0.0),   // mip 0: red
        float3(0.0, 1.0, 0.0),   // mip 1: green
        float3(0.0, 0.0, 1.0),   // mip 2: blue
        float3(1.0, 1.0, 0.0)    // mip 3: yellow
    };

    if (onBorder)
    {
        return float4(borderColors[mip], 1.0);
    }
    else
    {
        // Sample the bloom texture at the quadrant's mip level using the cell UV.
        float3 mipColor = SafeColor(BloomBlurTexture.SampleLevel(BloomBlurTextureSampler, cellUV, float(mip)).rgb);
        return float4(mipColor, 1.0);
    }
  }
  else if (BloomStrength > 0.0f)
  {
    int startMip = clamp(BloomStartMip, 0, 4);
    int endMip = clamp(BloomEndMip, startMip, 4);

    if (startMip == 1 && endMip == 4)
    {
      hdrSceneColor += SampleBloom(uv, 1.0f) * BloomStrength;
    }
    else
    {
      for (int lod = startMip; lod <= endMip; ++lod)
      {
        float w = GetBloomWeight(lod);
        if (w > 0.0f)
          hdrSceneColor += SampleBloom(uv, float(lod)) * w * BloomStrength;
      }
    }
  }

  // Atmosphere composites before local volumetric fog because it represents
  // long-distance planetary air. Skip the composite when the upscale wrote a
  // neutral (rgb=0, a=1) frame, or when the texture has not been written this
  // frame at all (rgba=0). Without these gates a (0,0,0,0) sample multiplies
  // the entire HDR scene by zero, producing a black screen, and a (0,0,0,1)
  // sample is a no-op we can skip outright. Any genuinely non-neutral
  // atmosphere output still composites normally.
  float4 atmosphere = SafeCompositeSample(AtmosphereColor.SampleLevel(AtmosphereColorSampler, sceneUv, 0.0), float4(0.0f, 0.0f, 0.0f, 1.0f));
  bool atmosphereNeutral = atmosphere.a >= 0.9999f && all(abs(atmosphere.rgb) <= float3(1e-5f));
  bool atmosphereUnwritten = atmosphere.a <= 1e-5f && all(abs(atmosphere.rgb) <= float3(1e-5f));
  if (!atmosphereNeutral && !atmosphereUnwritten)
    hdrSceneColor = SafeColor(hdrSceneColor * atmosphere.a + atmosphere.rgb);

  // Safe composite: when the fog scatter pass is disabled, skipped, or has not yet written
  // a frame, the texture clears to (0,0,0,0). A literal `hdrSceneColor * 0 + 0` would zero out
  // the entire scene. Only apply the composite when the fog pass has written meaningful
  // transmittance/scatter data (alpha > 0 OR rgb > 0). A fully opaque fog volume that wants
  // to occlude the scene must still write a non-zero alpha (transmittance) or non-zero rgb.
  float4 volumetricFog = SafeCompositeSample(VolumetricFogColor.SampleLevel(VolumetricFogColorSampler, sceneUv, 0.0), float4(0.0f, 0.0f, 0.0f, 1.0f));
  if (volumetricFog.a > 0.0f || any(volumetricFog.rgb > float3(0.0f)))
    hdrSceneColor = SafeColor(hdrSceneColor * volumetricFog.a + volumetricFog.rgb);

  hdrSceneColor = SafeColor(hdrSceneColor);

  //Tone mapping / HDR selection
  float3 sceneColor;
  if (OutputHDR)
  {
      sceneColor = hdrSceneColor * GetExposure();
  }
  else
  {
      sceneColor = XRENGINE_ApplyToneMap(hdrSceneColor, TonemapType, GetExposure(), ColorGradeGamma, MobiusTransition);
  }

  //Apply depth-based fog
  if (DepthFogIntensity > 0.0f)
  {
      float depth = DepthView.Load(int3(LoadCoordinate(sceneUv), 0));
      float fogFactor = clamp((depth - DepthFogStart) / (DepthFogEnd - DepthFogStart), 0.0f, 1.0f);
      sceneColor = lerp(sceneColor, DepthFogColor, fogFactor * DepthFogIntensity);
  }

	//Color grading
	sceneColor *= ColorGradeTint;

	sceneColor = ApplyHsvColorGrade(sceneColor);
	sceneColor = (sceneColor - 0.5f) * ColorGradeContrast + 0.5f;

  float2 outline = EnableEditorOutline ? GetStencilOutlineIntensity(sceneUv) : float2(0.0f);
  float hoverOutline = outline.x;
  float selectionOutline = outline.y;
  float outlineWeight = max(hoverOutline, selectionOutline);
  float3 outlineColor = lerp(SelectionOutlineColor, HoverOutlineColor, hoverOutline);
  sceneColor = lerp(sceneColor, outlineColor, outlineWeight);

  // DEBUG: Visualize raw stencil value - uncomment to see stencil data
  // uint rawStencil = texture(StencilView, uv).r;
  // if ((rawStencil & 1) != 0) sceneColor = float3(1.0, 0.0, 0.0); // Red where stencil bit 0 is set

	sceneColor = ApplyVignette(sceneColor, uv);
  sceneColor = SafeColor(sceneColor);

  if (!OutputHDR)
  {
    // Linear pipeline: do NOT manually gamma-encode here. With
    // GL_FRAMEBUFFER_SRGB enabled, the hardware applies the linear->sRGB
    // transfer on write to sRGB-capable color attachments (the default
    // framebuffer / sRGB FBOs). Manual gamma here would double-encode and
    // wash out the image.
    //
    // Fix subtle banding by applying fine noise in linear space.
    float2 noiseCoord = floor(input.position.xy);
    sceneColor += lerp(-0.5f / 255.0f, 0.5f / 255.0f, interleavedGradientNoise(noiseCoord));
  }

  sceneColor = SafeColor(sceneColor);
	return float4(sceneColor, 1.0f);
}
