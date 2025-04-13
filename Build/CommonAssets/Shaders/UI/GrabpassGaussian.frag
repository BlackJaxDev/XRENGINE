#version 450
layout (location = 0) out vec4 OutColor;

uniform sampler2D Texture0;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int SampleCount;
uniform vec4 MatColor;
uniform float BlurStrength;

const float PI = 3.14159265359;

float gaussian(float x, float sigma)
{
    return exp(-((x * x) / (2.0 * sigma * sigma))) / (sqrt(2.0 * PI) * sigma);
}

void main()
{
    float xOffset = 1.0 / ScreenWidth;
    float yOffset = 1.0 / ScreenHeight;
    vec2 vTexCoord = vec2(gl_FragCoord.x * xOffset, gl_FragCoord.y * yOffset);
    
    vec3 col = texture(Texture0, vTexCoord).rgb * gaussian(0.0, BlurStrength);
    float totalWeight = gaussian(0.0, BlurStrength);
    
    // Use more directions with importance sampling
    int directions = min(SampleCount, 16); // Cap directions for performance
    int samplesPerDirection = max(1, SampleCount / directions);
    
    for (int d = 0; d < directions; d++)
    {
        float angle = 2.0 * PI * float(d) / float(directions);
        vec2 dir = vec2(cos(angle), sin(angle));
        
        for (int i = 1; i <= samplesPerDirection; i++)
        {
            // Use importance sampling - samples closer to center have higher probability
            float distance = float(i) / float(samplesPerDirection) * BlurStrength;
            float weight = gaussian(distance, BlurStrength);
            
            vec2 offset = dir * distance;
            vec2 sampleCoord = vTexCoord + vec2(offset.x * xOffset, offset.y * yOffset);
            
            col += texture(Texture0, sampleCoord).rgb * weight;
            totalWeight += weight;
        }
    }
    
    col /= totalWeight; // Normalize by the total weight
    OutColor = vec4(col, 1.0) * MatColor;
}