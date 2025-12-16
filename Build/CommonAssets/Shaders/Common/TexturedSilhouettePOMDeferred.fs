#version 450

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec3 Normal;
layout (location = 2) out vec4 RMSI;

layout (location = 0) in vec3 FragPos;   // view-space position
layout (location = 1) in vec3 FragNorm;  // view-space normal
layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Height (R)

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;

// Parallax (silhouette POM)
uniform float ParallaxScale = 0.04f;     // UV units
uniform int ParallaxMinSteps = 12;
uniform int ParallaxMaxSteps = 48;
uniform int ParallaxRefineSteps = 5;     // binary refinement steps
uniform float ParallaxHeightBias = 0.0f; // applied to height before inversion
uniform float ParallaxSilhouette = 1.0f; // >0.5 enables discard when UV exits [0,1]

mat3 ComputeTBN(vec3 n, vec3 p, vec2 uv)
{
    vec3 dp1 = dFdx(p);
    vec3 dp2 = dFdy(p);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 dp2perp = cross(dp2, n);
    vec3 dp1perp = cross(n, dp1);
    vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;

    float invMax = inversesqrt(max(dot(t, t), dot(b, b)));
    return mat3(t * invMax, b * invMax, n);
}

// Returns parallaxed UV and whether the ray stayed inside the heightfield.
vec2 SilhouetteParallaxOcclusionMapping(
    sampler2D heightMap,
    vec2 baseUV,
    vec3 viewDirTS,
    float scale,
    int minSteps,
    int maxSteps,
    int refineSteps,
    float heightBias,
    out bool valid)
{
    valid = true;

    // If we're viewing from behind the tangent plane, disable.
    if (viewDirTS.z <= 1e-4)
        return baseUV;

    float ndotv = clamp(abs(viewDirTS.z), 0.0, 1.0);
    int stepCount = int(mix(float(maxSteps), float(minSteps), ndotv));
    stepCount = max(stepCount, 1);

    vec2 parallaxDir = (viewDirTS.xy / viewDirTS.z) * scale;
    vec2 deltaUV = parallaxDir / float(stepCount);
    float layerDepth = 1.0 / float(stepCount);

    vec2 uv = baseUV;
    float currentLayerDepth = 0.0;

    float h = clamp(texture(heightMap, uv).r + heightBias, 0.0, 1.0);
    float depthFromMap = 1.0 - h;

    // Linear search
    for (int i = 0; i < stepCount; ++i)
    {
        if (currentLayerDepth >= depthFromMap)
            break;

        uv -= deltaUV;
        currentLayerDepth += layerDepth;

        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        {
            valid = false;
            break;
        }

        h = clamp(texture(heightMap, uv).r + heightBias, 0.0, 1.0);
        depthFromMap = 1.0 - h;
    }

    if (!valid)
        return uv;

    // Binary refinement between previous and current UV
    vec2 uvPrev = uv + deltaUV;
    float depthPrev = currentLayerDepth - layerDepth;

    float hPrev = clamp(texture(heightMap, uvPrev).r + heightBias, 0.0, 1.0);
    float mapPrev = 1.0 - hPrev;

    vec2 aUV = uvPrev;
    vec2 bUV = uv;
    float aDepth = depthPrev;
    float bDepth = currentLayerDepth;
    float aMap = mapPrev;
    float bMap = depthFromMap;

    for (int r = 0; r < refineSteps; ++r)
    {
        vec2 midUV = 0.5 * (aUV + bUV);
        float midDepth = 0.5 * (aDepth + bDepth);

        float hMid = clamp(texture(heightMap, midUV).r + heightBias, 0.0, 1.0);
        float midMap = 1.0 - hMid;

        // If ray depth is still above the surface depth, we haven't hit yet.
        if (midDepth < midMap)
        {
            aUV = midUV;
            aDepth = midDepth;
            aMap = midMap;
        }
        else
        {
            bUV = midUV;
            bDepth = midDepth;
            bMap = midMap;
        }
    }

    // Choose the closer side (b is the first side that is at/behind the surface)
    return bUV;
}

void main()
{
    vec3 n = normalize(FragNorm);
    vec3 v = normalize(-FragPos);

    mat3 tbn = ComputeTBN(n, FragPos, FragUV0);
    vec3 viewDirTS = transpose(tbn) * v;

    bool pomValid;
    vec2 uv = SilhouetteParallaxOcclusionMapping(
        Texture1,
        FragUV0,
        viewDirTS,
        ParallaxScale,
        ParallaxMinSteps,
        ParallaxMaxSteps,
        ParallaxRefineSteps,
        ParallaxHeightBias,
        pomValid);

    if (ParallaxSilhouette > 0.5 && !pomValid)
        discard;

    if (ParallaxSilhouette <= 0.5 && !pomValid)
        uv = FragUV0;

    vec3 albedo = texture(Texture0, uv).rgb * BaseColor;

    Normal = n;
    AlbedoOpacity = vec4(albedo, Opacity);
    RMSI = vec4(Roughness, Metallic, Specular, Emission);
}
