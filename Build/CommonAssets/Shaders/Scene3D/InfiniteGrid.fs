#version 450

layout(location = 0) in vec3 NearWorldPos;
layout(location = 1) in vec3 FarWorldPos;
layout(location = 2) in vec2 FragClipXY;

layout(location = 0) out vec4 OutColor;

uniform mat4 ViewProjectionMatrix;
uniform mat4 InverseViewMatrix;
uniform vec3 CameraPosition;
uniform int DepthMode;
uniform int ClipDepthRange;

// Grid parameters
uniform int GridPlane = 0;
uniform float GridHeight = 0.0;
uniform float GridCellSize = 1.0;
uniform float GridSubdivisions = 10.0;
uniform float GridLineWidth = 1.0;
uniform float GridLodTargetPixelSpacing = 8.0;
uniform float GridMaxDistance = 500.0;
uniform float GridFadeRange = 150.0;
uniform float GridAltitudeDistanceScale = 8.0;
uniform vec4 GridMinorColor = vec4(0.45, 0.45, 0.48, 0.35);
uniform vec4 GridMajorColor = vec4(0.70, 0.70, 0.75, 0.60);
uniform vec4 GridXAxisColor = vec4(0.90, 0.25, 0.25, 0.85);
uniform vec4 GridYAxisColor = vec4(0.30, 0.80, 0.35, 0.85);
uniform vec4 GridZAxisColor = vec4(0.25, 0.45, 0.95, 0.85);
uniform int GridShowAxes = 1;
uniform int GridPlaneMask = 1;
uniform float GridBehindPlaneOpacity = 0.0;

float PristineGrid(vec2 coord, vec2 dxy, float cellSize, float lineWidth)
{
    vec2 grid = abs(fract(coord / cellSize - 0.5) - 0.5) * cellSize;
    vec2 antiAlias = max(dxy, vec2(1e-7));
    vec2 line = clamp((lineWidth * antiAlias * 0.5 - grid) / antiAlias + 0.5, 0.0, 1.0);
    return max(line.x, line.y);
}

float SmootherStep(float value)
{
    value = clamp(value, 0.0, 1.0);
    return value * value * value * (value * (value * 6.0 - 15.0) + 10.0);
}

float IntersectGridPlane(int plane, vec3 rayOrigin, vec3 rayDir)
{
    float denominator;
    float origin;
    float offset;

    if (plane == 1) // XY
    {
        denominator = rayDir.z;
        origin = rayOrigin.z;
        offset = 0.0;
    }
    else if (plane == 2) // YZ
    {
        denominator = rayDir.x;
        origin = rayOrigin.x;
        offset = 0.0;
    }
    else // XZ
    {
        denominator = rayDir.y;
        origin = rayOrigin.y;
        offset = GridHeight;
    }

    if (abs(denominator) < 1e-6)
        return 1e30;

    float intersection = (offset - origin) / denominator;
    return intersection > 0.0 ? intersection : 1e30;
}

float FindNearestOtherGridPlane(int currentPlane, vec3 rayOrigin, vec3 rayDir)
{
    float nearest = 1e30;
    for (int plane = 0; plane < 3; ++plane)
    {
        if (plane == currentPlane || (GridPlaneMask & (1 << plane)) == 0)
            continue;

        nearest = min(nearest, IntersectGridPlane(plane, rayOrigin, rayDir));
    }
    return nearest;
}

void main()
{
    vec3 rayOrigin = NearWorldPos;
    vec3 rayDir = FarWorldPos - NearWorldPos;
    vec3 cameraPosition = InverseViewMatrix[3].xyz;

    float rayPlaneAxis;
    float rayOriginPlaneAxis;
    float planeOffset;
    float cameraHeight;

    if (GridPlane == 1) // XY
    {
        rayPlaneAxis = rayDir.z;
        rayOriginPlaneAxis = rayOrigin.z;
        planeOffset = 0.0;
        cameraHeight = abs(cameraPosition.z);
    }
    else if (GridPlane == 2) // YZ
    {
        rayPlaneAxis = rayDir.x;
        rayOriginPlaneAxis = rayOrigin.x;
        planeOffset = 0.0;
        cameraHeight = abs(cameraPosition.x);
    }
    else // XZ
    {
        rayPlaneAxis = rayDir.y;
        rayOriginPlaneAxis = rayOrigin.y;
        planeOffset = GridHeight;
        cameraHeight = abs(cameraPosition.y - GridHeight);
    }

    if (abs(rayPlaneAxis) < 1e-6)
        discard;

    float t = (planeOffset - rayOriginPlaneAxis) / rayPlaneAxis;
    if (t <= 0.0)
        discard;

    float nearestOtherPlaneT = FindNearestOtherGridPlane(GridPlane, rayOrigin, rayDir);
    vec3 hitPos = rayOrigin + t * rayDir;
    vec2 coord;
    vec2 cameraCoord;
    vec4 firstAxisColor;
    vec4 secondAxisColor;

    if (GridPlane == 1) // XY
    {
        coord = hitPos.xy;
        cameraCoord = cameraPosition.xy;
        firstAxisColor = GridXAxisColor;
        secondAxisColor = GridYAxisColor;
    }
    else if (GridPlane == 2) // YZ
    {
        coord = hitPos.yz;
        cameraCoord = cameraPosition.yz;
        firstAxisColor = GridYAxisColor;
        secondAxisColor = GridZAxisColor;
    }
    else // XZ
    {
        coord = hitPos.xz;
        cameraCoord = cameraPosition.xz;
        firstAxisColor = GridXAxisColor;
        secondAxisColor = GridZAxisColor;
    }

    // Depth computation
    vec4 clipHit = ViewProjectionMatrix * vec4(hitPos, 1.0);
    if (clipHit.w <= 0.0)
        discard;

    float ndcZ = clipHit.z / clipHit.w;
    float depth = ClipDepthRange == 1 ? ndcZ * 0.5 + 0.5 : ndcZ;
    if (depth < 0.0 || depth > 1.0)
        discard;

    gl_FragDepth = DepthMode == 1 ? (1.0 - depth) : depth;

    // Multi-scale grid calculation
    vec2 dxy = fwidth(coord);
    float pixelFootprint = max(dxy.x, dxy.y);

    float baseCell = max(GridCellSize, 1e-4);
    float lodScale = max(GridSubdivisions, 2.0);
    float targetSpacing = max(GridLodTargetPixelSpacing, 1.0);
    float lodLevel = log(max(pixelFootprint * targetSpacing / baseCell, 1.0)) / log(lodScale);
    float lodFloor = floor(lodLevel);
    float lodFraction = SmootherStep(lodLevel - lodFloor);

    float scale0 = baseCell * pow(lodScale, lodFloor);
    float scale1 = scale0 * lodScale;
    float scale2 = scale1 * lodScale;

    float g0 = PristineGrid(coord, dxy, scale0, GridLineWidth);
    float g1 = PristineGrid(coord, dxy, scale1, GridLineWidth);
    float g2 = PristineGrid(coord, dxy, scale2, GridLineWidth);

    float fineAlpha = g0 * (1.0 - lodFraction) * GridMinorColor.a;
    vec4 middleColor = mix(GridMajorColor, GridMinorColor, lodFraction);
    float middleAlpha = g1 * middleColor.a;
    float coarseAlpha = g2 * lodFraction * GridMajorColor.a;

    float alphaSum = fineAlpha + middleAlpha + coarseAlpha;
    vec3 gridRgb = alphaSum > 1e-6
        ? (GridMinorColor.rgb * fineAlpha + middleColor.rgb * middleAlpha + GridMajorColor.rgb * coarseAlpha) / alphaSum
        : GridMajorColor.rgb;
    float gridAlpha = min(alphaSum, 1.0);

    if (GridShowAxes == 1)
    {
        float firstAxisMask = clamp((GridLineWidth * 1.5 * dxy.y * 0.5 - abs(coord.y)) / max(dxy.y, 1e-7) + 0.5, 0.0, 1.0);
        float secondAxisMask = clamp((GridLineWidth * 1.5 * dxy.x * 0.5 - abs(coord.x)) / max(dxy.x, 1e-7) + 0.5, 0.0, 1.0);

        if (firstAxisMask > 0.0)
        {
            gridRgb = mix(gridRgb, firstAxisColor.rgb, firstAxisMask);
            gridAlpha = max(gridAlpha, firstAxisMask * firstAxisColor.a);
        }
        if (secondAxisMask > 0.0)
        {
            gridRgb = mix(gridRgb, secondAxisColor.rgb, secondAxisMask);
            gridAlpha = max(gridAlpha, secondAxisMask * secondAxisColor.a);
        }
    }

    float planeVisibility = 1.0;
    if (nearestOtherPlaneT < t)
    {
        float worldSeparation = (t - nearestOtherPlaneT) * length(rayDir);
        float transitionWidth = max(baseCell, pixelFootprint * 4.0);
        float behindAmount = SmootherStep(worldSeparation / transitionWidth);
        planeVisibility = mix(1.0, clamp(GridBehindPlaneOpacity, 0.0, 1.0), behindAmount);
    }

    // Distance fade within the selected plane.
    float dist = length(coord - cameraCoord);
    float distanceFade = 1.0;
    if (GridMaxDistance > 0.0)
    {
        float effectiveMaxDistance = max(GridMaxDistance, cameraHeight * GridAltitudeDistanceScale);
        float effectiveFadeRange = max(GridFadeRange, effectiveMaxDistance * 0.2);
        float startFade = max(0.0, effectiveMaxDistance - effectiveFadeRange);
        distanceFade = SmootherStep(1.0 - (dist - startFade) / max(effectiveFadeRange, 1e-5));
    }

    float finalAlpha = gridAlpha * distanceFade * planeVisibility;
    if (finalAlpha < 0.001)
        discard;

    OutColor = vec4(gridRgb, finalAlpha);
}
