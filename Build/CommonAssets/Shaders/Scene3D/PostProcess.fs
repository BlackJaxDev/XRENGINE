#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex; //HDR scene color
uniform sampler2D Texture1; //Bloom
uniform sampler2D Texture2; //Depth
uniform usampler2D Texture3; //Stencil

uniform vec3 HighlightColor = vec3(0.92f, 1.0f, 0.086f);
uniform bool OutputHDR = false;

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

uniform float ChromaticAberrationIntensity;

struct DepthFogStruct
{
    float Intensity; //0.0f = no fog, 1.0f = full fog
    float Start; //Start distance of fog
    float End; //End distance of fog
    vec3 Color; //Color of fog
};
uniform DepthFogStruct DepthFog;

uniform float LensDistortionIntensity;

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
    int outlineSize = 1;
    ivec2 texSize = textureSize(HDRSceneTex, 0);
    vec2 texelSize = 1.0f / texSize;
    vec2 texelX = vec2(texelSize.x, 0.0f);
    vec2 texelY = vec2(0.0f, texelSize.y);
    uint stencilCurrent = texture(Texture3, uv).r;
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
          diff += (texture(Texture3, yPos).r & 1) - selectionBits;
          diff += (texture(Texture3, yNeg).r & 1) - selectionBits;
          diff += (texture(Texture3, xPos).r & 1) - selectionBits;
          diff += (texture(Texture3, xNeg).r & 1) - selectionBits;
    }
    return clamp(float(diff), 0.0f, 1.0f);
}

// Tonemapping selector
uniform int TonemapType = 3; //Default to Reinhard

vec3 LinearTM(vec3 c)
{
  return c * ColorGrade.Exposure;
}
vec3 GammaTM(vec3 c)
{
  return pow(c * ColorGrade.Exposure, vec3(1.0 / ColorGrade.Gamma));
}
vec3 ClipTM(vec3 c)
{
  return clamp(c * ColorGrade.Exposure, 0.0, 1.0);
}
vec3 ReinhardTM(vec3 c)
{
  vec3 x = c * ColorGrade.Exposure;
  return x / (x + vec3(1.0));
}
vec3 HableTM(vec3 c)
{
  const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
  vec3 x = max(c * ColorGrade.Exposure - E, vec3(0.0));
  return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}
vec3 MobiusTM(vec3 c)
{
  float a = 0.6;
  vec3 x = c * ColorGrade.Exposure;
  return (x * (a + 1.0)) / (x + a);
}
vec3 ACESTM(vec3 c)
{
  vec3 x = c * ColorGrade.Exposure;
  return (x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f);
}
vec3 FilmicTM(vec3 c)
{
  vec3 x = c * ColorGrade.Exposure;
  return (x * (x + 0.0245786f)) / (x * (0.983729f * x + 0.432951f) + 0.238081f);
}
vec3 NeutralTM(vec3 c)
{
  vec3 x = c * ColorGrade.Exposure;
  return (x * (x + 0.0245786f)) / (x * (0.983729f * x + 0.432951f) + 0.238081f);
}

vec3 Distort(sampler2D tex, vec2 uv, float intensity)
{
    //Distort UVs based on lens distortion
    uv = uv - vec2(0.5);
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd = uvd * (1.0 + intensity * uvd * uvd);
    return texture(tex, vec2(0.5) + vec2(sin(uva), cos(uva)) * uvd).rgb;
}

vec3 SampleHDR(vec2 uv)
{
  if (LensDistortionIntensity != 0.0f)
  {
      //Apply lens distortion
      return Distort(HDRSceneTex, uv, LensDistortionIntensity);
  }
  else
  {
      return texture(HDRSceneTex, uv).rgb;
  }
}
vec3 SampleBloom(vec2 uv, float lod)
{
  //Sample bloom texture at given LOD
  return textureLod(Texture1, uv, lod).rgb;
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
  
  //Apply chromatic aberration with texel‐based offsets and clamping
  if (ChromaticAberrationIntensity > 0.0f)
  {
      // Compute texel size for this texture
      ivec2 texSize = textureSize(HDRSceneTex, 0);
      vec2 texelSize = 1.0f / vec2(texSize);
  
      // Direction from center
      vec2 dir = uv - 0.5;
      // Scale by intensity and texel size
      vec2 off = dir * ChromaticAberrationIntensity * texelSize;
  
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
  
  //Add each blurred bloom mipmap
  //Starts at 1/2 size lod because original image is not blurred (and doesn't need to be)
  for (float lod = 1.0f; lod < 5.0f; lod += 1.0f)
    hdrSceneColor += SampleBloom(uv, lod);

  //Tone mapping / HDR selection
  vec3 sceneColor;
  if (OutputHDR)
  {
      sceneColor = hdrSceneColor * ColorGrade.Exposure;
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
      float depth = texture(Texture2, uv).r;
      float fogFactor = clamp((depth - DepthFog.Start) / (DepthFog.End - DepthFog.Start), 0.0f, 1.0f);
      sceneColor = mix(sceneColor, DepthFog.Color, fogFactor * DepthFog.Intensity);
  }

	//Color grading
	sceneColor *= ColorGrade.Tint;
  //if (ColorGrade.Hue != 1.0f || ColorGrade.Saturation != 1.0f || ColorGrade.Brightness != 1.0f)
  //{
  //    vec3 hsv = RGBtoHSV(ldrSceneColor);
  //    hsv.x *= ColorGrade.Hue;
  //    hsv.y *= ColorGrade.Saturation;
  //    hsv.z *= ColorGrade.Brightness;
  //    ldrSceneColor = HSVtoRGB(hsv);
  //}
	sceneColor = (sceneColor - 0.5f) * ColorGrade.Contrast + 0.5f;

  //Apply highlight color to selected objects
  float highlight = GetStencilHighlightIntensity(uv);
	sceneColor = mix(sceneColor, HighlightColor, highlight);

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
