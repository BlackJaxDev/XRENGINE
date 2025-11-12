#version 450 core

layout(location = 0) in vec3 Position;          // Gaussian center in model space
layout(location = 12) in vec4 Color0;           // RGB + opacity
layout(location = 13) in vec4 Color1;           // Scale.xyz and precomputed radius
layout(location = 14) in vec4 Color2;           // Rotation quaternion (x, y, z, w)

uniform mat4 ModelMatrix;
uniform mat4 InverseViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;
uniform float ScreenWidth;
uniform float ScreenHeight;

out VS_OUT
{
    vec4 color;
    mat2 invEllipseBasis;
    float pointRadiusPixels;
} vsOut;

const float kEpsilon = 1e-6;

mat3 QuaternionToMatrix(vec4 q)
{
    vec4 nq = normalize(q);
    float x = nq.x;
    float y = nq.y;
    float z = nq.z;
    float w = nq.w;

    return mat3(
        1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + w * z),     2.0 * (x * z - w * y),
        2.0 * (x * y - w * z),     1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + w * x),
        2.0 * (x * z + w * y),     2.0 * (y * z - w * x),     1.0 - 2.0 * (x * x + y * y)
    );
}

vec2 ProjectAxisToScreen(vec3 viewCenter, vec3 axisView, vec2 ndcCenter)
{
    vec4 clipOffset = ProjMatrix_VTX * vec4(viewCenter + axisView, 1.0);
    float invW = 1.0 / max(kEpsilon, clipOffset.w);
    vec2 ndcOffset = clipOffset.xy * invW;
    vec2 deltaNdc = ndcOffset - ndcCenter;
    vec2 viewport = vec2(ScreenWidth, ScreenHeight) * 0.5;
    return deltaNdc * viewport;
}

void main()
{
    vec4 worldPos = ModelMatrix * vec4(Position, 1.0);
    mat4 viewMatrix = inverse(InverseViewMatrix_VTX);
    vec4 viewPos4 = viewMatrix * worldPos;
    vec3 viewPos = viewPos4.xyz;

    vec4 packedColor = Color0;
    vec3 scale = max(vec3(0.0001), Color1.xyz);
    vec4 rotationQuat = Color2;

    vsOut.color = packedColor;
    mat3 rotation = QuaternionToMatrix(rotationQuat);
    mat3 modelMat = mat3(ModelMatrix);
    mat3 viewMat = mat3(viewMatrix);

    vec3 axisModelX = rotation * vec3(scale.x, 0.0, 0.0);
    vec3 axisModelY = rotation * vec3(0.0, scale.y, 0.0);

    vec3 axisViewX = viewMat * (modelMat * axisModelX);
    vec3 axisViewY = viewMat * (modelMat * axisModelY);

    vec4 clipCenter = ProjMatrix_VTX * viewPos4;
    float invCenterW = 1.0 / max(kEpsilon, clipCenter.w);
    vec2 ndcCenter = clipCenter.xy * invCenterW;

    vec2 axisScreenX = ProjectAxisToScreen(viewPos, axisViewX, ndcCenter);
    vec2 axisScreenY = ProjectAxisToScreen(viewPos, axisViewY, ndcCenter);

    float majorRadius = max(length(axisScreenX), length(axisScreenY));
    if (majorRadius < kEpsilon)
        majorRadius = 1.0;

    float pointSize = clamp(majorRadius * 2.0, 1.0, 2048.0);
    gl_PointSize = pointSize;
    vsOut.pointRadiusPixels = pointSize * 0.5;

    vec2 a = axisScreenX;
    vec2 b = axisScreenY;
    float det = a.x * b.y - a.y * b.x;
    if (abs(det) < kEpsilon)
    {
        a = vec2(majorRadius, 0.0);
        b = vec2(0.0, majorRadius);
        det = a.x * b.y - a.y * b.x;
    }
    float invDet = 1.0 / max(kEpsilon, det);
    vsOut.invEllipseBasis = mat2( b.y, -a.y,
                                 -b.x,  a.x) * invDet;

    gl_Position = clipCenter;
}
