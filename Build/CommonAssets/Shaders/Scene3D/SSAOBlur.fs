#version 450
layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;
uniform sampler2D SSAOIntensityTexture;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    //Normalize uv from [-1, 1] to [0, 1]
    uv = uv * 0.5f + 0.5f;
    
    vec2 texelSize = 1.0f / vec2(textureSize(SSAOIntensityTexture, 0));
    float result = 0.0f;
    for (int x = -2; x < 2; ++x) 
    {
        for (int y = -2; y < 2; ++y) 
        {
            vec2 offset = vec2(float(x), float(y)) * texelSize;
            result += texture(SSAOIntensityTexture, uv + offset).r;
        }
    }
    OutIntensity = result / 16.0f;
}