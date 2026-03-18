// OctahedralMapping snippet
// Usage: #pragma snippet "OctahedralMapping"

vec2 XRENGINE_EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0)
    {
        vec2 signDir = vec2(octDir.x >= 0.0 ? 1.0 : -1.0, octDir.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signDir;
    }

    return uv * 0.5 + 0.5;
}

vec3 XRENGINE_DecodeOcta(vec2 uv)
{
    vec2 f = uv * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));

    if (n.z < 0.0)
    {
        vec2 signDir = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signDir;
    }

    return normalize(vec3(n.x, n.z, n.y));
}

vec3 XRENGINE_SampleOcta(sampler2D tex, vec3 dir)
{
    return texture(tex, XRENGINE_EncodeOcta(dir)).rgb;
}

vec3 XRENGINE_SampleOctaLod(sampler2D tex, vec3 dir, float lod)
{
    return textureLod(tex, XRENGINE_EncodeOcta(dir), lod).rgb;
}

vec3 XRENGINE_SampleOctaArray(sampler2DArray tex, vec3 dir, float layer)
{
    return texture(tex, vec3(XRENGINE_EncodeOcta(dir), layer)).rgb;
}

vec3 XRENGINE_SampleOctaArrayLod(sampler2DArray tex, vec3 dir, float layer, float lod)
{
    return textureLod(tex, vec3(XRENGINE_EncodeOcta(dir), layer), lod).rgb;
}
