// Octahedral normal encoding helpers.
// Usage: #pragma snippet "NormalEncoding"

vec2 XRENGINE_EncodeNormal(vec3 normal)
{
    vec3 n = normalize(normal);
    float invL1Norm = 1.0f / max(abs(n.x) + abs(n.y) + abs(n.z), 1e-6f);
    n *= invL1Norm;

    vec2 oct = n.xy;
    if (n.z < 0.0f)
    {
        vec2 signDir = vec2(n.x >= 0.0f ? 1.0f : -1.0f, n.y >= 0.0f ? 1.0f : -1.0f);
        oct = (1.0f - abs(oct.yx)) * signDir;
    }

    return oct * 0.5f + 0.5f;
}

vec3 XRENGINE_DecodeNormal(vec2 encoded)
{
    vec2 f = encoded * 2.0f - 1.0f;
    vec3 n = vec3(f, 1.0f - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0f, 1.0f);
    n.xy -= vec2(n.x >= 0.0f ? t : -t, n.y >= 0.0f ? t : -t);
    return normalize(n);
}

vec3 XRENGINE_ReadNormal(sampler2D normalTexture, vec2 uv)
{
    return XRENGINE_DecodeNormal(texture(normalTexture, uv).rg);
}

vec3 XRENGINE_ReadNormal(sampler2DArray normalTexture, vec3 uvw)
{
    return XRENGINE_DecodeNormal(texture(normalTexture, uvw).rg);
}

vec3 XRENGINE_ReadNormalMS(sampler2DMS normalTexture, ivec2 coord, int sampleIndex)
{
    return XRENGINE_DecodeNormal(texelFetch(normalTexture, coord, sampleIndex).rg);
}