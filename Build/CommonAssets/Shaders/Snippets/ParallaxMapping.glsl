// Parallax Occlusion Mapping (POM) Snippet
// Provides silhouette-aware parallax mapping functions

mat3 XRENGINE_ComputeTBN(vec3 n, vec3 p, vec2 uv)
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

vec2 XRENGINE_SilhouetteParallaxOcclusionMapping(
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

    return bUV;
}

// Simple parallax mapping (single offset)
vec2 XRENGINE_SimpleParallaxMapping(vec2 baseUV, vec3 viewDirTS, float heightScale, sampler2D heightMap)
{
    float height = texture(heightMap, baseUV).r;
    vec2 offset = viewDirTS.xy / viewDirTS.z * (height * heightScale);
    return baseUV - offset;
}

// Steep parallax mapping (no refinement)
vec2 XRENGINE_SteepParallaxMapping(
    sampler2D heightMap,
    vec2 baseUV,
    vec3 viewDirTS,
    float scale,
    int numLayers)
{
    float layerDepth = 1.0 / float(numLayers);
    float currentLayerDepth = 0.0;
    vec2 deltaUV = viewDirTS.xy / viewDirTS.z * scale / float(numLayers);
    
    vec2 uv = baseUV;
    float depthFromMap = 1.0 - texture(heightMap, uv).r;
    
    for (int i = 0; i < numLayers; ++i)
    {
        if (currentLayerDepth >= depthFromMap)
            break;
        
        uv -= deltaUV;
        currentLayerDepth += layerDepth;
        depthFromMap = 1.0 - texture(heightMap, uv).r;
    }
    
    return uv;
}
