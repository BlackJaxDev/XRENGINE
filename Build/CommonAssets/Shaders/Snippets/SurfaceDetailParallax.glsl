// Surface-detail parallax: silhouette parallax occlusion mapping (POM) for height maps.
//
// The model importer injects XRENGINE_HEIGHTMAP_POM for materials whose surface-detail
// texture is a height map. Without the define, every function in this snippet is a
// pass-through and the shader keeps early depth testing.
//
// Contract:
//   - Texture1 is the height map. The red channel is height: 1 = surface top, 0 = deepest.
//   - Call XRENGINE_BeginSurfaceParallax at the start of main, before any texture sample.
//     Sample every surface texture with XRENGINE_SampleSurface and the returned UV.
//   - Call XRENGINE_EndSurfaceParallax at the end of main. It writes gl_FragDepth and
//     discards silhouette fragments. The discard is deferred to the end so that all
//     derivative operations run in uniform control flow.
//
// The ray march uses a derivative tangent frame built from the world position and the
// unmodified UV0. That frame is exactly dP/du and dP/dv, so the UV offset and the depth
// offset stay correct for mirrored, non-uniformly scaled or tiled UV layouts.
#ifdef XRENGINE_HEIGHTMAP_POM
layout(location = 22) in float FragViewIndex;

// Per-eye camera state. XRCamera.SetUniforms writes the left-eye names in mono passes too,
// so view index 0 is always valid.
uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeViewProjectionMatrix;
uniform mat4 RightEyeViewProjectionMatrix;
uniform int ClipDepthRange;

// Height scale in UV units at full depth.
uniform float ParallaxScale = 0.04f;
uniform int ParallaxMinSteps = 8;
uniform int ParallaxMaxSteps = 32;
uniform int ParallaxRefineSteps = 5;
// 1 = discard fragments whose ray leaves the [0, 1] UV square (silhouette clip).
// The importer enables this only when every mesh that uses the material keeps UV0 in [0, 1].
uniform int ParallaxSilhouette = 0;

vec2 XRENGINE_SurfaceUvDx = vec2(0.0);
vec2 XRENGINE_SurfaceUvDy = vec2(0.0);
float XRENGINE_SurfaceParallaxDepth = 0.0;
bool XRENGINE_SurfaceParallaxRejected = false;

float XRENGINE_SampleParallaxHeight(vec2 uv)
{
    return textureGrad(Texture1, uv, XRENGINE_SurfaceUvDx, XRENGINE_SurfaceUvDy).r;
}

bool XRENGINE_IsParallaxUvInside(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
}

float XRENGINE_ParallaxWindowDepth(mat4 viewProjection, vec3 positionWS)
{
    vec4 clip = viewProjection * vec4(positionWS, 1.0);
    float ndcZ = clip.z / max(abs(clip.w), 1e-20) * sign(clip.w);
    return ClipDepthRange == 1 ? ndcZ * 0.5 + 0.5 : ndcZ;
}

vec2 XRENGINE_BeginSurfaceParallax(vec2 uv, vec3 positionWS, vec3 normalWS)
{
    // All derivatives first, in uniform control flow.
    vec3 positionDx = dFdx(positionWS);
    vec3 positionDy = dFdy(positionWS);
    XRENGINE_SurfaceUvDx = dFdx(uv);
    XRENGINE_SurfaceUvDy = dFdy(uv);
    XRENGINE_SurfaceParallaxDepth = gl_FragCoord.z;
    XRENGINE_SurfaceParallaxRejected = false;

    bool rightEye = FragViewIndex > 0.5;
    vec3 cameraPosition = rightEye ? RightEyeInverseViewMatrix[3].xyz : LeftEyeInverseViewMatrix[3].xyz;
    vec3 N = normalize(normalWS);
    vec3 V = normalize(cameraPosition - positionWS);
    if (!gl_FrontFacing)
        N = -N;
    float cosView = dot(N, V);
    if (cosView <= 1e-3 || ParallaxScale <= 0.0)
        return uv;

    // dP/du and dP/dv from screen derivatives.
    float determinant = XRENGINE_SurfaceUvDx.x * XRENGINE_SurfaceUvDy.y - XRENGINE_SurfaceUvDx.y * XRENGINE_SurfaceUvDy.x;
    if (abs(determinant) < 1e-12)
        return uv;
    vec3 dPdu = (positionDx * XRENGINE_SurfaceUvDy.y - positionDy * XRENGINE_SurfaceUvDx.y) / determinant;
    vec3 dPdv = (positionDy * XRENGINE_SurfaceUvDx.x - positionDx * XRENGINE_SurfaceUvDy.x) / determinant;
    dPdu -= N * dot(N, dPdu);
    dPdv -= N * dot(N, dPdv);

    // Express the tangential view direction in UV units per world unit (least squares on
    // the dP/du, dP/dv basis). worldPerUv is the isotropic UV-to-world scale.
    float guu = dot(dPdu, dPdu);
    float guv = dot(dPdu, dPdv);
    float gvv = dot(dPdv, dPdv);
    float gramDeterminant = guu * gvv - guv * guv;
    if (gramDeterminant < 1e-20)
        return uv;
    vec3 viewTangential = V - N * cosView;
    vec2 rhs = vec2(dot(viewTangential, dPdu), dot(viewTangential, dPdv));
    vec2 viewUvPerWorld = vec2(gvv * rhs.x - guv * rhs.y, guu * rhs.y - guv * rhs.x) / gramDeterminant;
    float worldPerUv = sqrt(sqrt(gramDeterminant));
    if (!(worldPerUv > 0.0) || isinf(worldPerUv))
        return uv;

    // At layer depth d in [0, 1] the ray has traveled d * depthWorld / cosView world units.
    float depthWorld = ParallaxScale * worldPerUv;
    vec2 fullDepthUvOffset = viewUvPerWorld * (depthWorld / cosView);

    int stepCount = int(mix(float(ParallaxMaxSteps), float(ParallaxMinSteps), clamp(cosView, 0.0, 1.0)));
    stepCount = clamp(stepCount, 1, 256);
    float layerStep = 1.0 / float(stepCount);
    vec2 uvStep = fullDepthUvOffset * layerStep;

    bool clipSilhouette = ParallaxSilhouette != 0;
    vec2 currentUv = uv;
    float currentLayer = 0.0;
    float currentSurfaceDepth = 1.0 - XRENGINE_SampleParallaxHeight(currentUv);
    for (int i = 0; i < stepCount && currentLayer < currentSurfaceDepth; ++i)
    {
        currentUv -= uvStep;
        currentLayer += layerStep;
        currentSurfaceDepth = 1.0 - XRENGINE_SampleParallaxHeight(currentUv);
    }

    // Binary refinement between the last layer above the surface and the first below it.
    vec2 aboveUv = currentUv + uvStep;
    float aboveLayer = max(currentLayer - layerStep, 0.0);
    vec2 belowUv = currentUv;
    float belowLayer = currentLayer;
    if (currentLayer > 0.0)
    {
        for (int r = 0; r < ParallaxRefineSteps; ++r)
        {
            vec2 midUv = 0.5 * (aboveUv + belowUv);
            float midLayer = 0.5 * (aboveLayer + belowLayer);
            if (midLayer < 1.0 - XRENGINE_SampleParallaxHeight(midUv))
            {
                aboveUv = midUv;
                aboveLayer = midLayer;
            }
            else
            {
                belowUv = midUv;
                belowLayer = midLayer;
            }
        }
    }

    if (clipSilhouette && !XRENGINE_IsParallaxUvInside(belowUv))
        XRENGINE_SurfaceParallaxRejected = true;

    // Depth offset: move the fragment along the view ray to the intersection point.
    mat4 viewProjection = rightEye ? RightEyeViewProjectionMatrix : LeftEyeViewProjectionMatrix;
    vec3 hitPositionWS = positionWS - V * (belowLayer * depthWorld / cosView);
    float depthDelta = XRENGINE_ParallaxWindowDepth(viewProjection, hitPositionWS)
        - XRENGINE_ParallaxWindowDepth(viewProjection, positionWS);
    if (!isnan(depthDelta) && !isinf(depthDelta))
        XRENGINE_SurfaceParallaxDepth = clamp(gl_FragCoord.z + depthDelta, 0.0, 1.0);

    return belowUv;
}

vec4 XRENGINE_SampleSurface(sampler2D surfaceTexture, vec2 uv)
{
    return textureGrad(surfaceTexture, uv, XRENGINE_SurfaceUvDx, XRENGINE_SurfaceUvDy);
}

void XRENGINE_EndSurfaceParallax()
{
    gl_FragDepth = XRENGINE_SurfaceParallaxDepth;
    if (XRENGINE_SurfaceParallaxRejected)
        discard;
}
#else
vec2 XRENGINE_BeginSurfaceParallax(vec2 uv, vec3 positionWS, vec3 normalWS)
{
    return uv;
}

vec4 XRENGINE_SampleSurface(sampler2D surfaceTexture, vec2 uv)
{
    return texture(surfaceTexture, uv);
}

void XRENGINE_EndSurfaceParallax()
{
}
#endif
