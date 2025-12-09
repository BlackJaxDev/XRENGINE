#version 450

layout(location = 0) out vec3 BloomColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D BloomBlurTexture;

uniform float Ping;
uniform int LOD; // Source mip to sample; target mip is set by FBO attachment.
uniform float Radius = 1.0f;
uniform float BloomThreshold = 0.4f;
uniform bool UseThreshold = true;
uniform float BloomSoftKnee = 1.0f;

// Soft knee helper for bloom thresholding
float BloomSoftThreshold(in vec3 col)
{
    float brightness = max(max(col.r, col.g), col.b);
    float knee = BloomSoftKnee + 1e-6f;
    float weight = clamp((brightness - BloomThreshold + knee) / knee, 0.0f, 1.0f);
    return weight * weight;
}

//We can use 5 texture lookups instead of 9 by using linear filtering and averaging the offsets and weights
uniform float Offset[3] = float[](0.0f, 1.3846153846f, 3.2307692308f);
uniform float Weight[3] = float[](0.2270270270f, 0.3162162162f, 0.0702702703f);

void main()
{
      // Transform clip-space coords to UV and clamp edges
      vec2 clipUV = clamp(FragPos.xy, -1.0f, 1.0f);
      vec2 uv = clipUV * 0.5f + 0.5f;
      uv = clamp(uv, 0.0f, 1.0f);

      vec2 scale = vec2(Ping, 1.0f - Ping);
    vec2 texelSize = 1.0f / textureSize(BloomBlurTexture, LOD) * scale;
    float lodf = float(LOD) + 0.5f; // slight bias to smooth transitions between mips
      // Sample center and apply soft-knee bloom threshold weight
      vec3 centerCol = textureLod(BloomBlurTexture, uv, lodf).rgb;
      float centerW = UseThreshold ? BloomSoftThreshold(centerCol) : 1.0f;
      vec3 result = centerCol * Weight[0] * centerW;

       for (int i = 1; i <= 2; ++i)
       {
          float weight = Weight[i];
          float offset = Offset[i] * Radius;
          vec2 uvOffset = texelSize * offset;

          vec3 sampleCol = textureLod(BloomBlurTexture, uv + uvOffset, lodf).rgb;
          float sampleW = UseThreshold ? BloomSoftThreshold(sampleCol) : 1.0f;
          result += sampleCol * weight * sampleW;
          sampleCol = textureLod(BloomBlurTexture, uv - uvOffset, lodf).rgb;
          sampleW = UseThreshold ? BloomSoftThreshold(sampleCol) : 1.0f;
          result += sampleCol * weight * sampleW;
       }

       BloomColor = result;
}
