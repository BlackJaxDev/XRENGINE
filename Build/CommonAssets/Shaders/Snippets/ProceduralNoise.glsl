// Sample Custom Snippet - Procedural Noise Functions
// Usage: #pragma snippet "ProceduralNoise"

// Hash function for pseudo-random values
float XRENGINE_Hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float XRENGINE_Hash3D(vec3 p)
{
    return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

// Value noise
float XRENGINE_ValueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f); // smoothstep
    
    float a = XRENGINE_Hash(i);
    float b = XRENGINE_Hash(i + vec2(1.0, 0.0));
    float c = XRENGINE_Hash(i + vec2(0.0, 1.0));
    float d = XRENGINE_Hash(i + vec2(1.0, 1.0));
    
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Gradient noise (Perlin-like)
vec2 XRENGINE_GradientHash(vec2 p)
{
    p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
    return -1.0 + 2.0 * fract(sin(p) * 43758.5453123);
}

float XRENGINE_GradientNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    
    return mix(mix(dot(XRENGINE_GradientHash(i + vec2(0.0, 0.0)), f - vec2(0.0, 0.0)),
                   dot(XRENGINE_GradientHash(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0)), u.x),
               mix(dot(XRENGINE_GradientHash(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)),
                   dot(XRENGINE_GradientHash(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0)), u.x), u.y);
}

// Fractal Brownian Motion
float XRENGINE_FBM(vec2 p, int octaves, float lacunarity, float gain)
{
    float sum = 0.0;
    float amp = 0.5;
    float freq = 1.0;
    
    for (int i = 0; i < octaves; i++)
    {
        sum += amp * XRENGINE_GradientNoise(p * freq);
        freq *= lacunarity;
        amp *= gain;
    }
    
    return sum;
}

// Worley/Cellular noise
float XRENGINE_WorleyNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    
    float minDist = 1.0;
    
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            vec2 neighbor = vec2(float(x), float(y));
            vec2 point = XRENGINE_GradientHash(i + neighbor) * 0.5 + 0.5;
            vec2 diff = neighbor + point - f;
            float dist = length(diff);
            minDist = min(minDist, dist);
        }
    }
    
    return minDist;
}
