#version 450

layout(location = 0) out vec3 BloomColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D BloomBlurTexture;

uniform float Ping;
uniform int LOD;
uniform float Radius = 1.0f;
uniform int SampleCount = 3;
uniform float Sigma = 4.0f;

float Gaussian(float x)
{
    return exp(-0.5 * (x * x) / (Sigma * Sigma));
}
void main()
{
      vec2 uv = FragPos.xy;
      if (uv.x > 1.0f || uv.y > 1.0f)
         discard;
      //Normalize uv from [-1, 1] to [0, 1]
      uv = uv * 0.5f + 0.5f;

      vec2 scale = vec2(Ping, 1.0f - Ping);
      vec2 texelSize = 1.0f / textureSize(BloomBlurTexture, LOD) * scale;
      float lodf = float(LOD);

      float totalWeight = 0.0f;
      vec3 result = textureLod(BloomBlurTexture, uv, lodf).rgb * Gaussian(0);

      for (int i = -SampleCount; i <= SampleCount; ++i)
      {
         float offset = float(i);
         float weight = Gaussian(offset * Radius);
         vec2 uvOffset = texelSize * offset * Radius;

         result += textureLod(BloomBlurTexture, uv + uvOffset, lodf).rgb * weight;
         result += textureLod(BloomBlurTexture, uv - uvOffset, lodf).rgb * weight;
      }

      BloomColor = result / totalWeight;
}
