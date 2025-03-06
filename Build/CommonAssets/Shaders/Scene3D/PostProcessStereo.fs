#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray HDRSceneTex; //HDR scene color
uniform sampler2DArray Texture1; //Bloom
uniform sampler2DArray Texture2; //Depth
uniform usampler2DArray Texture3; //Stencil

struct ColorGradeStruct
{
    vec3 Tint;

    float Exposure;
    float Contrast;
    float Gamma;

    float Hue;
    float Saturation;
    float Brightness;
};
uniform ColorGradeStruct ColorGrade;

float rand(vec2 coord)
{
    return fract(sin(dot(coord, vec2(12.9898f, 78.233f))) * 43758.5453f);
}

void main()
{
  vec2 uv = FragPos.xy;
  if (uv.x > 1.0f || uv.y > 1.0f)
      discard;
  //Normalize uv from [-1, 1] to [0, 1]
  uv = uv * 0.5f + 0.5f;
  vec3 uvi = vec3(uv, gl_ViewID_OVR);

	vec3 hdrSceneColor = texture(HDRSceneTex, uvi).rgb;

  //Add each blurred bloom mipmap
  //Starts at 1/2 size lod because original image is not blurred (and doesn't need to be)
  for (float lod = 1.0f; lod < 5.0f; lod += 1.0f)
    hdrSceneColor += textureLod(Texture1, uvi, lod).rgb;

  //Tone mapping
	vec3 ldrSceneColor = vec3(1.0f) - exp(-hdrSceneColor * ColorGrade.Exposure);

	//Gamma-correct
	ldrSceneColor = pow(ldrSceneColor, vec3(1.0f / ColorGrade.Gamma));
  //Fix subtle banding by applying fine noise
  ldrSceneColor += mix(-0.5f / 255.0f, 0.5f / 255.0f, rand(uv));

	OutColor = vec4(ldrSceneColor, 1.0f);
}
