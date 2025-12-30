#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex; //HDR scene color
uniform sampler2D BloomBlurTexture; //Bloom
uniform sampler2D DepthView; //Depth
uniform usampler2D StencilView; //Stencil

// 1x1 R32F texture containing the current exposure value (GPU-driven auto exposure)
uniform sampler2D AutoExposureTex;
uniform bool UseGpuAutoExposure;

uniform vec3 HighlightColor = vec3(1.0f, 0.0f, 1.0f); // Bright magenta for visibility
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

// Lens distortion mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini
uniform int LensDistortionMode;
uniform float LensDistortionIntensity;
uniform float PaniniDistance;
uniform float PaniniCrop;
uniform vec2 PaniniViewExtents; // tan(fov/2) * aspect, tan(fov/2)

// Bloom combine controls
uniform int BloomStartMip = 0;
uniform int BloomEndMip = 4;
uniform float BloomLodWeights[5] = float[](0.6, 0.5, 0.35, 0.2, 0.1);

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
float rand(vec2 coord)
{
    return fract(sin(dot(coord, vec2(12.9898f, 78.233f))) * 43758.5453f);
}
float GetStencilHighlightIntensity(vec2 uv)
{
    int outlineSize = 3; // Increased from 1 to make outline more visible
    ivec2 texSize = textureSize(HDRSceneTex, 0);
    vec2 texelSize = 1.0f / texSize;
    vec2 texelX = vec2(texelSize.x, 0.0f);
    vec2 texelY = vec2(0.0f, texelSize.y);
    uint stencilCurrent = texture(StencilView, uv).r;
    uint selectionBits = stencilCurrent & 1;
    uint diff = 0;
    vec2 zero = vec2(0.0f);

    //Check neighboring stencil texels that indicate highlighted/selected
    for (int i = 1; i <= outlineSize; ++i)
    {
          vec2 yPos = clamp(uv + texelY * i, zero, uv);
          vec2 yNeg = clamp(uv - texelY * i, zero, uv);
          vec2 xPos = clamp(uv + texelX * i, zero, uv);
          vec2 xNeg = clamp(uv - texelX * i, zero, uv);
          diff += (texture(StencilView, yPos).r & 1) - selectionBits;
          diff += (texture(StencilView, yNeg).r & 1) - selectionBits;
          diff += (texture(StencilView, xPos).r & 1) - selectionBits;
          diff += (texture(StencilView, xNeg).r & 1) - selectionBits;
    }
    return clamp(float(diff), 0.0f, 1.0f);
}

// Tonemapping selector
uniform int TonemapType = 3; //Default to Reinhard

vec3 LinearTM(vec3 c)
{
  return c * GetExposure();
}
vec3 GammaTM(vec3 c)
{
  return pow(c * GetExposure(), vec3(1.0 / ColorGrade.Gamma));
}
vec3 ClipTM(vec3 c)
{
  return clamp(c * GetExposure(), 0.0, 1.0);
}
vec3 ReinhardTM(vec3 c)
{
  vec3 x = c * GetExposure();
  return x / (x + vec3(1.0));
}
vec3 HableTM(vec3 c)
{
  const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
  vec3 x = max(c * GetExposure() - E, vec3(0.0));
  return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}
vec3 MobiusTM(vec3 c)
{
  float a = 0.6;
  vec3 x = c * GetExposure();
  return (x * (a + 1.0)) / (x + a);
}
vec3 ACESTM(vec3 c)
{
  vec3 x = c * GetExposure();
  return (x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f);
}
vec3 FilmicTM(vec3 c)
{
  vec3 x = c * GetExposure();
  return (x * (x + 0.0245786f)) / (x * (0.983729f * x + 0.432951f) + 0.238081f);
}
vec3 NeutralTM(vec3 c)
{
  vec3 x = c * GetExposure();
  return (x * (x + 0.0245786f)) / (x * (0.983729f * x + 0.432951f) + 0.238081f);
}

vec2 ApplyLensDistortion(vec2 uv, float intensity)
{
    uv -= vec2(0.5);
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
    return vec2(0.5) + vec2(sin(uva), cos(uva)) * uvd;
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
    // Mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini
    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
            return ApplyLensDistortion(uv, LensDistortionIntensity);
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            // Convert UV [0,1] to view position using view extents
            // PaniniViewExtents contains (tan(fov/2) * aspect, tan(fov/2))
            // PaniniCrop is the scale factor for crop-to-fit
            vec2 view_pos = (2.0 * uv - 1.0) * PaniniViewExtents * PaniniCrop;
            vec2 proj_pos = ApplyPaniniProjection(view_pos, PaniniDistance);
            // Convert back to UV
            vec2 proj_ndc = proj_pos / PaniniViewExtents;
            return proj_ndc * 0.5 + 0.5;
        }
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
      vec2 dir = uv - 0.5;
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
  
  //Add bloom mipmaps with configurable range/weights
  int startMip = clamp(BloomStartMip, 0, 4);
  int endMip = clamp(BloomEndMip, startMip, 4);
  for (int lod = startMip; lod <= endMip; ++lod)
  {
    float w = BloomLodWeights[lod];
    if (w > 0.0f)
      hdrSceneColor += SampleBloom(uv, float(lod)) * w;
  }

  //Tone mapping / HDR selection
  vec3 sceneColor;
  if (OutputHDR)
  {
      sceneColor = hdrSceneColor * GetExposure();
  }
  else
  {
      switch (TonemapType)
      {
          case 0:  sceneColor = LinearTM(hdrSceneColor);   break;
          case 1:  sceneColor = GammaTM(hdrSceneColor);    break;
          case 2:  sceneColor = ClipTM(hdrSceneColor);     break;
          case 3:  sceneColor = ReinhardTM(hdrSceneColor); break;
          case 4:  sceneColor = HableTM(hdrSceneColor);    break;
          case 5:  sceneColor = MobiusTM(hdrSceneColor);   break;
          case 6:  sceneColor = ACESTM(hdrSceneColor);     break;
          case 7:  sceneColor = FilmicTM(hdrSceneColor);   break;
          case 8:  sceneColor = NeutralTM(hdrSceneColor);  break;
          default: sceneColor = ReinhardTM(hdrSceneColor); break;
      }
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

  // Hue/Saturation/Brightness grading is only well-defined in the LDR path.
  if (!OutputHDR && (ColorGrade.Hue != 1.0f || ColorGrade.Saturation != 1.0f || ColorGrade.Brightness != 1.0f))
  {
      vec3 hsv = RGBtoHSV(clamp(sceneColor, vec3(0.0f), vec3(1.0f)));
      hsv.x = fract(hsv.x * ColorGrade.Hue);
      hsv.y = clamp(hsv.y * ColorGrade.Saturation, 0.0f, 1.0f);
      hsv.z = max(hsv.z * ColorGrade.Brightness, 0.0f);
      sceneColor = HSVtoRGB(hsv);
  }
	sceneColor = (sceneColor - 0.5f) * ColorGrade.Contrast + 0.5f;

  //Apply highlight color to selected objects
  float highlight = GetStencilHighlightIntensity(uv);
	sceneColor = mix(sceneColor, HighlightColor, highlight);

  // DEBUG: Visualize raw stencil value - uncomment to see stencil data
  // uint rawStencil = texture(StencilView, uv).r;
  // if ((rawStencil & 1) != 0) sceneColor = vec3(1.0, 0.0, 0.0); // Red where stencil bit 0 is set

	//Vignette
  //vec2 center = vec2(0.5f);
  //float vignetteFactor = pow(clamp(length(uv - center) / (0.5f * Vignette.Intensity), 0.0f, 1.0f), Vignette.Power);
  //ldrSceneColor = mix(ldrSceneColor, Vignette.Color, vignetteFactor);

  if (!OutputHDR)
  {
	  //Gamma-correct
	  sceneColor = pow(sceneColor, vec3(1.0f / ColorGrade.Gamma));

    //Fix subtle banding by applying fine noise
    sceneColor += mix(-0.5f / 255.0f, 0.5f / 255.0f, rand(uv));
  }

	OutColor = vec4(sceneColor, 1.0f);
}
