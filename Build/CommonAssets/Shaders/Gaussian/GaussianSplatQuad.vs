#version 460

// Draws one screen-space quad for each sorted gaussian splat (one instance per order slot).
// The quad covers 3 sigma along the eigenvectors of the projected 2D covariance (EWA splatting).

layout(location = 0) in vec3 Position; // Quad corner. X and Y are -1 or 1.

struct GaussianSplat
{
    vec4 centerOpacity;
    vec4 rotation;
    vec4 scalePad;
    vec4 linearColor;
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

uniform mat4 ModelMatrix;
uniform mat4 ViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform float SplatScale;

// Offset from the splat center in eigenvector space, in units of sigma.
layout(location = 0) out vec2 FragEllipseOffset;
// Linear RGB color and opacity.
layout(location = 1) flat out vec4 FragColorOpacity;

const uint CulledKey = 0xFFFFFFFFu;
const float QuadExtentSigma = 3.0;
// Low-pass filter added to the projected covariance, in square pixels.
const float LowPassPixels = 0.3;
// Limit of x/depth and y/depth for the Jacobian, as a multiple of tan(fov / 2).
const float JacobianClampFactor = 1.3;

// Puts all quad vertices at one point outside the clip volume, so the quad makes no fragments.
void Collapse()
{
    gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
    FragEllipseOffset = vec2(0.0);
    FragColorOpacity = vec4(0.0);
}

// Rotation matrix of a quaternion (x, y, z, w). The columns are the rotated local axes.
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

    GaussianSplat splat = splats[pair.y];
    mat4 modelView = ViewMatrix_VTX * ModelMatrix;
    vec4 viewCenter = modelView * vec4(splat.centerOpacity.xyz, 1.0);
    vec4 clipCenter = ProjMatrix_VTX * viewCenter;
    if (!(clipCenter.w > 0.0))
    {
        Collapse();
        return;
    }

    // The columns are the view-space axes of the splat, scaled by sigma: W * R * S.
    vec3 sigma = max(splat.scalePad.xyz * SplatScale, vec3(1e-7));
    mat3 sigmaAxes = mat3(modelView) * QuaternionToMatrix(splat.rotation) * mat3(
        sigma.x, 0.0, 0.0,
        0.0, sigma.y, 0.0,
        0.0, 0.0, sigma.z);

    // Standard 3DGS clamp: for a perspective projection, evaluate the Jacobian at a point whose x/depth and
    // y/depth are limited to 1.3 * tan(fov / 2). Splats far outside the view then do not make very large
    // quads. The clamp assumes a symmetric perspective projection, so tan(fovX / 2) = 1 / P[0][0] and
    // tan(fovY / 2) = 1 / P[1][1]. The clamp changes only the Jacobian; the quad stays at the true center.
    vec3 jacobianPoint = viewCenter.xyz;
    if (ProjMatrix_VTX[2][3] != 0.0)
    {
        float depth = -jacobianPoint.z;
        vec2 limit = JacobianClampFactor / vec2(ProjMatrix_VTX[0][0], ProjMatrix_VTX[1][1]);
        jacobianPoint.xy = clamp(jacobianPoint.xy / depth, -limit, limit) * depth;
    }
    vec4 jacobianClip = ProjMatrix_VTX * vec4(jacobianPoint, 1.0);

    // Jacobian J of the pixel position with respect to the view position at that point:
    // d(clip.xy / clip.w) = (P_row_xy - (clip.xy / clip.w) * P_row_w) / clip.w, scaled to pixels.
    vec3 projectionRowX = vec3(ProjMatrix_VTX[0][0], ProjMatrix_VTX[1][0], ProjMatrix_VTX[2][0]);
    vec3 projectionRowY = vec3(ProjMatrix_VTX[0][1], ProjMatrix_VTX[1][1], ProjMatrix_VTX[2][1]);
    vec3 projectionRowW = vec3(ProjMatrix_VTX[0][3], ProjMatrix_VTX[1][3], ProjMatrix_VTX[2][3]);
    float inverseW = 1.0 / jacobianClip.w;
    vec2 halfViewport = 0.5 * vec2(ScreenWidth, ScreenHeight);
    vec3 jacobianX = halfViewport.x * inverseW * (projectionRowX - jacobianClip.x * inverseW * projectionRowW);
    vec3 jacobianY = halfViewport.y * inverseW * (projectionRowY - jacobianClip.y * inverseW * projectionRowW);

    // T = J * W * R * S. The 2D covariance is T * transpose(T) plus the low-pass filter.
    vec3 tx = jacobianX * sigmaAxes;
    vec3 ty = jacobianY * sigmaAxes;
    float a = dot(tx, tx) + LowPassPixels;
    float b = dot(tx, ty);
    float c = dot(ty, ty) + LowPassPixels;

    // Eigenvalues and eigenvectors of the symmetric 2x2 covariance [a b; b c].
    float middle = 0.5 * (a + c);
    float radius = length(vec2(0.5 * (a - c), b));
    float majorVariance = middle + radius;
    float minorVariance = max(middle - radius, 1e-6);
    vec2 majorAxis = abs(b) > 1e-12
        ? normalize(vec2(b, majorVariance - a))
        : (a >= c ? vec2(1.0, 0.0) : vec2(0.0, 1.0));
    vec2 minorAxis = vec2(-majorAxis.y, majorAxis.x);

    // In eigenvector space, d^T * inverse(covariance) * d is the squared length of the offset in sigma.
    vec2 ellipseOffset = Position.xy * QuadExtentSigma;
    vec2 pixelOffset =
        ellipseOffset.x * sqrt(majorVariance) * majorAxis +
        ellipseOffset.y * sqrt(minorVariance) * minorAxis;

    gl_Position = clipCenter + vec4(pixelOffset / halfViewport * clipCenter.w, 0.0, 0.0);
    FragEllipseOffset = ellipseOffset;
    FragColorOpacity = vec4(splat.linearColor.rgb, splat.centerOpacity.w);
}
