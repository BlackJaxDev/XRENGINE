#version 450
#extension GL_EXT_multiview : require

layout(triangles) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in vec3 InWorldPosition[];
layout(location = 1) in vec3 InWorldNormal[];
layout(location = 4) in vec2 InUv[];
layout(location = 20) in vec3 InLocalPosition[];
layout(location = 23) in vec3 InLocalNormal[];

layout(location = 24) out vec2 FinUv;
layout(location = 25) out vec2 SurfaceUv;

uniform mat4 ModelMatrix;
uniform mat4 RootInvModelMatrix;
uniform mat4 ViewProjectionMatrix;
uniform mat4 LeftEyeViewProjectionMatrix;
uniform mat4 RightEyeViewProjectionMatrix;
uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform vec3 CameraPosition;
uniform bool VRMode;
uniform float _Thickness;
uniform float _MaxOutlineSize;
uniform float _OutlineLimitBreak;
uniform float _AttenuateFinalEdge;

vec3 cameraCenter()
{
    if (!VRMode)
        return CameraPosition;
    vec3 left = LeftEyeInverseViewMatrix[3].xyz;
    vec3 right = RightEyeInverseViewMatrix[3].xyz;
    return 0.5 * (left + right);
}

mat4 eyeProjection()
{
    if (!VRMode)
        return ViewProjectionMatrix;
    if (gl_ViewIndex == 0)
        return LeftEyeViewProjectionMatrix;
    return RightEyeViewProjectionMatrix;
}

void writeFinVertex(vec3 localPosition, vec3 localNormal, vec2 uv,
    vec2 finUv, float expansion, mat4 projection)
{
    vec3 worldPosition = (ModelMatrix * vec4(localPosition, 1.0)).xyz;
    vec3 cameraDirectionLocal = (RootInvModelMatrix *
        vec4(normalize(cameraCenter() - worldPosition), 0.0)).xyz;
    vec3 adjustedNormal = normalize(localNormal +
        cameraDirectionLocal * _AttenuateFinalEdge);
    vec3 position = localPosition + adjustedNormal * expansion;
    gl_Position = projection * ModelMatrix * vec4(position, 1.0);
    FinUv = finUv;
    SurfaceUv = uv;
    EmitVertex();
}

void main()
{
    vec3 center = cameraCenter();
    float facing[3];
    for (int index = 0; index < 3; ++index)
        facing[index] = dot(normalize(InWorldNormal[index]),
            normalize(center - InWorldPosition[index]));

    int odd = -1;
    for (int index = 0; index < 3; ++index)
    {
        int next = (index + 1) % 3;
        int last = (index + 2) % 3;
        if ((facing[index] < 0.0) != (facing[next] < 0.0) &&
            (facing[index] < 0.0) != (facing[last] < 0.0))
        {
            odd = index;
            break;
        }
    }
    if (odd < 0)
        return;

    int a = (odd + 1) % 3;
    int b = (odd + 2) % 3;
    float weightA = facing[odd] / (facing[odd] - facing[a]);
    float weightB = facing[odd] / (facing[odd] - facing[b]);
    vec3 edgeA = mix(InLocalPosition[odd], InLocalPosition[a], weightA);
    vec3 edgeB = mix(InLocalPosition[odd], InLocalPosition[b], weightB);
    vec3 normalA = normalize(mix(InLocalNormal[odd], InLocalNormal[a], weightA));
    vec3 normalB = normalize(mix(InLocalNormal[odd], InLocalNormal[b], weightB));
    vec2 uvA = mix(InUv[odd], InUv[a], weightA);
    vec2 uvB = mix(InUv[odd], InUv[b], weightB);
    float width = min(_MaxOutlineSize, _OutlineLimitBreak + _Thickness) / 550.0;
    mat4 projection = eyeProjection();

    if (facing[odd] < 0.0)
    {
        writeFinVertex(edgeA, normalA, uvA, vec2(0.0, 0.0), 0.0, projection);
        writeFinVertex(edgeB, normalB, uvB, vec2(1.0, 0.0), 0.0, projection);
        writeFinVertex(edgeA, normalA, uvA, vec2(0.0, 1.0), width, projection);
        writeFinVertex(edgeB, normalB, uvB, vec2(1.0, 1.0), width, projection);
    }
    else
    {
        writeFinVertex(edgeB, normalB, uvB, vec2(1.0, 0.0), 0.0, projection);
        writeFinVertex(edgeA, normalA, uvA, vec2(0.0, 0.0), 0.0, projection);
        writeFinVertex(edgeB, normalB, uvB, vec2(1.0, 1.0), width, projection);
        writeFinVertex(edgeA, normalA, uvA, vec2(0.0, 1.0), width, projection);
    }
    EndPrimitive();
}
