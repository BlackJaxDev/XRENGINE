// Octahedral impostor sheet composition
// Based on the approach outlined by shaderbits: https://shaderbits.com/blog/octahedral-impostors

#version 450 core

layout(location = 0) in vec2 TexCoord;
layout(location = 0) out vec4 FragColor;

layout(binding = 0) uniform sampler2D ViewX;
layout(binding = 1) uniform sampler2D ViewY;
layout(binding = 2) uniform sampler2D ViewZ;

// Decode octahedral direction from the normalized texcoord domain.
vec3 decodeOctahedral(vec2 uv)
{
    vec2 f = uv * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
        n.xy = (1.0 - abs(n.yx)) * sign(n.xy);
    return normalize(vec3(n.x, n.z, n.y));
}

vec3 weights(vec3 dir)
{
    vec3 adir = abs(dir);
    float sum = adir.x + adir.y + adir.z + 1e-5;
    return adir / sum;
}

void main()
{
    vec3 dir = decodeOctahedral(TexCoord);
    vec3 w = weights(dir);

    vec2 uvX = dir.zy * 0.5 + 0.5; // project to YZ plane
    vec2 uvY = dir.xz * 0.5 + 0.5; // project to XZ plane
    vec2 uvZ = dir.xy * 0.5 + 0.5; // project to XY plane

    vec4 sampleX = texture(ViewX, uvX);
    vec4 sampleY = texture(ViewY, uvY);
    vec4 sampleZ = texture(ViewZ, uvZ);

    FragColor = sampleX * w.x + sampleY * w.y + sampleZ * w.z;
}
