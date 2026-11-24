// Projects sorted 3D Gaussian avatar splats to 3-sigma EWA screen quads while
// propagating tangent-plane world offsets and exact sub-splat UV coordinates.
// The material table prepends the #version line and the defines.
// With XR_SPLAT_SHADE_PER_SPLAT, this shader also shades each splat once with its source material, at the splat
// center, and passes the result to the fragment shader in FragSplatShade. A pixel of a splat quad then costs only the
// Gaussian falloff and the blend, instead of a full material evaluation for each of the overlapping quads.

layout(location = 0) in vec3 Position;

struct GaussianSplat
{
    vec4 centerOpacity;
    vec4 rotation;
    vec4 scalePad;
    vec4 linearColor;
};

struct GaussianSplatSurface
{
    vec4 uv0Uv1;
    vec4 uv2Uv3;
    vec4 dUv0LocalXY;
    vec4 worldNormalAndSign;
    vec4 worldTangentPad;
    vec4 localPosPad;
    uvec4 materialAndColor;
};

layout(std430, binding = 46) readonly buffer GaussianSplats
{
    GaussianSplat splats[];
};

layout(std430, binding = 47) readonly buffer GaussianSplatOrder
{
    uvec2 orderPairs[];
};

layout(std430, binding = 48) readonly buffer GaussianSplatVisibleCount
{
    uint visibleCount;
};

layout(std430, binding = 60) readonly buffer GaussianSplatSurfaces
{
    GaussianSplatSurface splatSurfaces[];
};

#if defined(XR_SPLAT_SHADE_PER_SPLAT)
#include "Gaussian/GaussianUberAdapter.glslinc"
#include "Advanced/Shading/ShadeNativeUber.glslinc"
#include "Gaussian/GaussianSplatUberShading.glslinc"
#endif

uniform mat4 ModelMatrix;
uniform mat4 ViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform float SplatScale;
uniform float uDepthBiasMeters;

layout(location = 0) out vec2 FragEllipseOffset;
layout(location = 1) out vec3 FragWorldPosition;
layout(location = 2) out vec3 FragLocalPosition;
layout(location = 3) out vec3 FragWorldNormal;
layout(location = 4) out vec3 FragWorldTangent;
layout(location = 5) out vec4 FragUv0Uv1;
layout(location = 6) out vec4 FragUv2Uv3;
layout(location = 7) flat out float FragTangentHandedness;
layout(location = 8) flat out float FragBaseOpacity;
layout(location = 9) flat out uvec2 FragMaterialAndColor;
layout(location = 10) flat out vec4 FragSplatShade;

const uint CulledKey = 0xFFFFFFFFu;
const float QuadExtentSigma = 2.2;
const float LowPassPixels = 0.3;
const float JacobianClampFactor = 1.3;

void Collapse()
{
    gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
    FragEllipseOffset = vec2(0.0);
    FragWorldPosition = vec3(0.0);
    FragLocalPosition = vec3(0.0);
    FragWorldNormal = vec3(0.0, 1.0, 0.0);
    FragWorldTangent = vec3(1.0, 0.0, 0.0);
    FragUv0Uv1 = vec4(0.0);
    FragUv2Uv3 = vec4(0.0);
    FragTangentHandedness = 1.0;
    FragBaseOpacity = 0.0;
    FragMaterialAndColor = uvec2(0u);
    FragSplatShade = vec4(0.0, 0.0, 0.0, -1.0);
}

mat3 QuaternionToMatrix(vec4 q)
{
    vec4 n = normalize(q);
    float x = n.x;
    float y = n.y;
    float z = n.z;
    float w = n.w;
    return mat3(
        1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + w * z), 2.0 * (x * z - w * y),
        2.0 * (x * y - w * z), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + w * x),
        2.0 * (x * z + w * y), 2.0 * (y * z - w * x), 1.0 - 2.0 * (x * x + y * y));
}

void main()
{
    uint instance = uint(gl_InstanceID);
    if (instance >= visibleCount)
    {
        Collapse();
        return;
    }

    uvec2 pair = orderPairs[instance];
    if (pair.x == CulledKey)
    {
        Collapse();
        return;
    }

    uint splatIndex = pair.y;
    GaussianSplat splat = splats[splatIndex];
    GaussianSplatSurface surface = splatSurfaces[splatIndex];

    uint packedMaterialFlags = surface.materialAndColor.x;
    uint cullMode = (packedMaterialFlags >> 16u) & 0x3u;

    mat4 modelView = ViewMatrix_VTX * ModelMatrix;
    vec3 localCenter = splat.centerOpacity.xyz;
    vec3 worldCenter = (ModelMatrix * vec4(localCenter, 1.0)).xyz;
    vec4 viewCenter = ViewMatrix_VTX * vec4(worldCenter, 1.0);
    vec4 clipCenter = ProjMatrix_VTX * viewCenter;
    if (!(clipCenter.w > 0.0))
    {
        Collapse();
        return;
    }

    mat3 localRot = QuaternionToMatrix(splat.rotation);
    mat3 worldRot = mat3(ModelMatrix) * localRot;
    vec3 geometricViewNormal = mat3(ViewMatrix_VTX) * worldRot[2];
    float facingDot = -dot(geometricViewNormal, viewCenter.xyz);
    if ((cullMode == 2u && facingDot < 0.0) || (cullMode == 1u && facingDot > 0.0))
    {
        Collapse();
        return;
    }

    vec3 sigma = max(splat.scalePad.xyz * SplatScale, vec3(1.0e-7));
    mat3 sigmaAxes = mat3(modelView) * localRot * mat3(
        sigma.x, 0.0, 0.0,
        0.0, sigma.y, 0.0,
        0.0, 0.0, sigma.z);

    vec3 jacobianPoint = viewCenter.xyz;
    if (ProjMatrix_VTX[2][3] != 0.0)
    {
        float depth = max(-jacobianPoint.z, 1.0e-6);
        vec2 limit = JacobianClampFactor / abs(vec2(ProjMatrix_VTX[0][0], ProjMatrix_VTX[1][1]));
        jacobianPoint.xy = clamp(jacobianPoint.xy / depth, -limit, limit) * depth;
    }
    vec4 jacobianClip = ProjMatrix_VTX * vec4(jacobianPoint, 1.0);

    vec3 projectionRowX = vec3(ProjMatrix_VTX[0][0], ProjMatrix_VTX[1][0], ProjMatrix_VTX[2][0]);
    vec3 projectionRowY = vec3(ProjMatrix_VTX[0][1], ProjMatrix_VTX[1][1], ProjMatrix_VTX[2][1]);
    vec3 projectionRowW = vec3(ProjMatrix_VTX[0][3], ProjMatrix_VTX[1][3], ProjMatrix_VTX[2][3]);
    float inverseW = 1.0 / jacobianClip.w;
    vec2 halfViewport = 0.5 * vec2(ScreenWidth, ScreenHeight);
    vec3 jacobianX = halfViewport.x * inverseW * (projectionRowX - jacobianClip.x * inverseW * projectionRowW);
    vec3 jacobianY = halfViewport.y * inverseW * (projectionRowY - jacobianClip.y * inverseW * projectionRowW);

    vec3 tx = jacobianX * sigmaAxes;
    vec3 ty = jacobianY * sigmaAxes;
    float a = dot(tx, tx) + LowPassPixels;
    float b = dot(tx, ty);
    float c = dot(ty, ty) + LowPassPixels;

    float middle = 0.5 * (a + c);
    float radius = length(vec2(0.5 * (a - c), b));
    float majorVariance = middle + radius;
    float minorVariance = max(middle - radius, 1.0e-6);
    vec2 majorAxis = abs(b) > 1.0e-12
        ? normalize(vec2(b, majorVariance - a))
        : (a >= c ? vec2(1.0, 0.0) : vec2(0.0, 1.0));
    vec2 minorAxis = vec2(-majorAxis.y, majorAxis.x);

    vec2 ellipseOffset = Position.xy * QuadExtentSigma;
    vec2 pixelOffset =
        ellipseOffset.x * sqrt(majorVariance) * majorAxis +
        ellipseOffset.y * sqrt(minorVariance) * minorAxis;

    // Map screen pixel offset back onto the splat's unit-meter tangent axes so UV0 and world
    // position keep continuous screen-space derivatives without underflowing on dense micro-splats.
    mat3 unitTangentAxes = mat3(modelView) * localRot;
    vec3 ux = jacobianX * unitTangentAxes;
    vec3 uy = jacobianY * unitTangentAxes;
    mat2 meterToPixel = mat2(vec2(ux.x, uy.x), vec2(ux.y, uy.y));
    float det = meterToPixel[0][0] * meterToPixel[1][1] - meterToPixel[1][0] * meterToPixel[0][1];
    vec2 rawWorldTangentOffset = vec2(0.0);
    if (abs(det) > 1.0e-2)
    {
        mat2 pixelToMeter = mat2(
            meterToPixel[1][1], -meterToPixel[0][1],
            -meterToPixel[1][0], meterToPixel[0][0]) * (1.0 / det);
        rawWorldTangentOffset = pixelToMeter * pixelOffset;
    }

    vec2 maxPosOffset = sigma.xy * QuadExtentSigma;
    vec2 posTangentOffset = clamp(rawWorldTangentOffset, -maxPosOffset, maxPosOffset);
    vec2 maxUvOffset = max(sigma.xy * 8.0, vec2(0.02));
    vec2 uvTangentOffset = clamp(rawWorldTangentOffset, -maxUvOffset, maxUvOffset);

    vec3 worldOffset = normalize(worldRot[0]) * posTangentOffset.x + normalize(worldRot[1]) * posTangentOffset.y;
    vec3 worldSamplePos = worldCenter + worldOffset;
    vec2 uv0 = surface.uv0Uv1.xy
        + surface.dUv0LocalXY.xy * uvTangentOffset.x
        + surface.dUv0LocalXY.zw * uvTangentOffset.y;

    vec4 viewTangent = ViewMatrix_VTX * vec4(worldSamplePos, 1.0);
    viewTangent.z = min(viewTangent.z - uDepthBiasMeters, -1.0e-4);
    vec4 clipTangent = ProjMatrix_VTX * viewTangent;
    float tangentNdcZ = clipTangent.w > 1.0e-6 ? (clipTangent.z / clipTangent.w) : (clipCenter.z / clipCenter.w);
    vec2 ndcXY = (clipCenter.xy / clipCenter.w) + (pixelOffset / halfViewport);

    gl_Position = vec4(ndcXY * clipCenter.w, tangentNdcZ * clipCenter.w, clipCenter.w);
    FragEllipseOffset = ellipseOffset;
    FragWorldPosition = worldSamplePos;
    FragLocalPosition = surface.localPosPad.xyz + worldOffset;
    FragWorldNormal = surface.worldNormalAndSign.xyz;
    FragWorldTangent = surface.worldTangentPad.xyz;
    FragUv0Uv1 = vec4(uv0, surface.uv0Uv1.zw);
    FragUv2Uv3 = surface.uv2Uv3;
    FragTangentHandedness = surface.worldNormalAndSign.w;
    FragBaseOpacity = splat.centerOpacity.w;
    FragMaterialAndColor = uvec2(
        packedMaterialFlags & 0xFFFFu,
        surface.materialAndColor.y);

#if defined(XR_SPLAT_SHADE_PER_SPLAT)
    // The texture derivatives span one sigma of the splat on its tangent axes, so each texture sample is filtered
    // over the surface area that the splat covers. Only UV0 has tangent-space gradients; the other UV sets use the
    // UV0 gradients as an estimate.
    vec2 uv0Dx = surface.dUv0LocalXY.xy * sigma.x;
    vec2 uv0Dy = surface.dUv0LocalXY.zw * sigma.y;
    XRSplatShadeInput shadeInput;
    shadeInput.worldPosition = worldCenter;
    shadeInput.localPosition = surface.localPosPad.xyz;
    shadeInput.worldPositionDx = normalize(worldRot[0]) * sigma.x;
    shadeInput.worldPositionDy = normalize(worldRot[1]) * sigma.y;
    shadeInput.normal = surface.worldNormalAndSign.xyz;
    shadeInput.normalDx = vec3(0.0);
    shadeInput.normalDy = vec3(0.0);
    shadeInput.tangent = surface.worldTangentPad.xyz;
    shadeInput.tangentDx = vec3(0.0);
    shadeInput.tangentDy = vec3(0.0);
    shadeInput.tangentHandedness = surface.worldNormalAndSign.w;
    shadeInput.uv0Uv1 = surface.uv0Uv1;
    shadeInput.uv2Uv3 = surface.uv2Uv3;
    shadeInput.uv0Dx = uv0Dx;
    shadeInput.uv0Dy = uv0Dy;
    shadeInput.uv1Dx = uv0Dx;
    shadeInput.uv1Dy = uv0Dy;
    shadeInput.uv2Dx = uv0Dx;
    shadeInput.uv2Dy = uv0Dy;
    shadeInput.uv3Dx = uv0Dx;
    shadeInput.uv3Dy = uv0Dy;
    shadeInput.materialIndex = packedMaterialFlags & 0xFFFFu;
    shadeInput.packedColor = surface.materialAndColor.y;
    shadeInput.cameraPosition = InverseViewMatrix[3].xyz;
    // The splat index stands in for the pixel, so ordered alpha dither keeps or drops whole splats.
    shadeInput.pixel = uvec2(splatIndex & 0xFFFFu, splatIndex >> 16u);

    XRAdvancedOpaqueSampleResult shaded;
    FragSplatShade = XR_SPLAT_Shade(shadeInput, shaded)
        ? vec4(shaded.radiance, clamp(shaded.opacity, 0.0, 1.0))
        : vec4(0.0, 0.0, 0.0, -1.0);
#else
    FragSplatShade = vec4(0.0);
#endif
}
