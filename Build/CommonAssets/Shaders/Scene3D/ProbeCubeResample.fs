#version 450
#pragma snippet "ProbeCubeFace"
layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
uniform samplerCube Texture0;
uniform samplerCube Texture1;
uniform int Face;
uniform float SourceLevel;

vec3 ReadLevel(vec3 direction, int level)
{
    return (level & 1) == 0
        ? textureLod(Texture0, direction, float(level)).rgb
        : textureLod(Texture1, direction, float(level)).rgb;
}

void main()
{
    vec3 direction = XR_ProbeFaceDirection(Face, XR_ProbeRasterFacePlane(FragPos.xy));
    int low = int(floor(SourceLevel));
    float fraction = fract(SourceLevel);
    vec3 radiance = ReadLevel(direction, low);
    if (fraction > 0.0)
        radiance = mix(radiance, ReadLevel(direction, low + 1), fraction);
    OutColor = vec4(radiance, 1.0);
}
