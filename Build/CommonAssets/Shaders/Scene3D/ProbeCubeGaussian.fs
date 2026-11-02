#version 450
#pragma snippet "ProbeCubeFace"
layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
uniform samplerCube Texture0;
uniform int Face;
uniform float SourceLevel;

void main()
{
    vec2 plane = XR_ProbeRasterFacePlane(FragPos.xy);
    float jacobian = pow(1.0 + dot(plane, plane), -1.5);
    float spacing = 2.0 / float(textureSize(Texture0, int(SourceLevel)).x);
    vec3 normal = XR_ProbeFaceDirection(Face, vec2(0.0));
    vec3 radiance = vec3(0.0);
    float weightSum = 0.0;
    for (int y = 0; y < 6; y++)
    {
        for (int x = 0; x < 6; x++)
        {
            vec2 offset = vec2(x, y) - 2.5;
            float weight = exp(-4.0 * dot(offset, offset) * jacobian * jacobian);
            vec3 extended = XR_ProbeFaceDirection(Face, plane + spacing * offset);
            vec3 boundary = clamp(extended, -1.0, 1.0);
            vec3 overflow = abs(extended - boundary);
            // Continue on the cube surface across an edge, including corner taps.
            vec3 direction = boundary - normal * max(overflow.x, max(overflow.y, overflow.z));
            radiance += max(textureLod(Texture0, direction, SourceLevel).rgb, vec3(0.0)) * weight;
            weightSum += weight;
        }
    }
    OutColor = vec4(radiance / max(weightSum, 1e-6), 1.0);
}
