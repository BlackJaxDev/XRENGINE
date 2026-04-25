#version 450

// Volumetric fog bilateral upscale.
//
// Reads:
//   VolumetricFogHalfTemporal (RGBA16F, half-res) - temporally reprojected fog
//     rgb = in-scattered radiance, a = transmittance
//   VolumetricFogHalfDepth   (R32F,  half-res)  - raw depth at each
//     half-res pixel's raymarch sample
//   DepthView                (float, full-res)   - scene depth
// Writes:
//   VolumetricFogColor       (RGBA16F, full-res) - bilateral-upsampled
//     volumetric to be sampled by PostProcess.fs.
//
// Algorithm: 2x2 depth-aware bilateral filter.
//   For each full-res output pixel:
//     1. Sample full-res raw depth D_full.
//     2. Convert UV to half-res texel space; gather the four surrounding
//        half-res taps (floor and floor+1 in x/y).
//     3. Weight each tap by a bilinear spatial factor (sub-pixel distance)
//        multiplied by a Gaussian on |D_half_tap - D_full| in eye-linear
//        space.
//     4. If the total weight is below a floor (all four taps failed the
//        depth threshold), fall back to the closest-depth tap.
//
// Depth comparison runs in eye-linear distance derived from raw depth via the
// inverse projection, so the bilateral threshold is scene-scale invariant.
//
// Far-plane (sky) pixels early-out to (0,0,0,1): the scatter pass also
// produces (0,0,0,1) there since ComputeVolumetricFog's ray exits all
// volumes, and the bilateral would only slightly soften that constant.

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D VolumetricFogHalfTemporal;
uniform sampler2D VolumetricFogHalfDepth;
uniform sampler2D DepthView;

uniform mat4 InverseProjMatrix;
uniform int DepthMode;

float ResolveDepth(float d)
{
    return DepthMode == 1 ? (1.0f - d) : d;
}

// Returns eye-space linear distance for the given raw depth sample at UV.
// We reconstruct the view-space Z magnitude from the inverse projection so
// the bilateral weight is invariant to perspective.
float LinearEyeDistance(float rawDepth, vec2 uv)
{
    vec4 clip = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
    vec4 view = InverseProjMatrix * clip;
    float w = max(abs(view.w), 1e-5f);
    return abs(view.z / w);
}

void main()
{
    vec2 ndc = FragPos.xy;
    if (ndc.x > 1.0f || ndc.y > 1.0f)
        discard;
    vec2 uv = ndc * 0.5f + 0.5f;

    float rawFullDepth = texture(DepthView, uv).r;
    float resolvedFullDepth = ResolveDepth(rawFullDepth);

    // Sky / far-plane pixels have no scatter contribution; match the
    // scatter shader's early-out so PostProcess.fs composites a clean scene.
    if (resolvedFullDepth >= 0.999999f)
    {
        OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
        return;
    }

    float linearFull = LinearEyeDistance(rawFullDepth, uv);

    // Locate the four surrounding half-res taps in texel space.
    vec2 halfSize = vec2(textureSize(VolumetricFogHalfTemporal, 0));
    if (halfSize.x <= 0.0f || halfSize.y <= 0.0f)
    {
        OutColor = texture(VolumetricFogHalfTemporal, uv);
        return;
    }

    vec2 halfTexel = 1.0f / halfSize;
    // Shift by -0.5 texel so uv * halfSize - 0.5 gives fractional offset
    // relative to the four nearest half-res pixel centers.
    vec2 halfPix = uv * halfSize - 0.5f;
    vec2 halfBase = floor(halfPix);
    vec2 halfFrac = halfPix - halfBase;

    // Clamp the base so the four taps stay in range even at the edge.
    halfBase = clamp(halfBase, vec2(0.0f), halfSize - 2.0f);

    // Bilinear spatial weights for the 2x2 neighborhood.
    vec4 spatial = vec4(
        (1.0f - halfFrac.x) * (1.0f - halfFrac.y),
        (       halfFrac.x) * (1.0f - halfFrac.y),
        (1.0f - halfFrac.x) * (       halfFrac.y),
        (       halfFrac.x) * (       halfFrac.y));

    // Depth threshold in eye-linear units. Scales with far-plane distance
    // so we don't over-reject at long range. 2% of current-pixel distance
    // plus a small epsilon keeps near-plane pixels from collapsing to
    // nearest-only on minor depth variance.
    float sigma = max(linearFull * 0.02f, 0.05f);

    vec4 accum = vec4(0.0f);
    float weightSum = 0.0f;

    // Track the nearest-depth tap as a fallback in case all taps are
    // rejected (e.g. full-res pixel sits across a silhouette edge from
    // every half-res sample).
    float bestDelta = 1.0e30f;
    vec4 bestColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);

    for (int j = 0; j < 2; ++j)
    {
        for (int i = 0; i < 2; ++i)
        {
            int index = j * 2 + i;
            vec2 tapUV = (halfBase + vec2(float(i) + 0.5f, float(j) + 0.5f)) * halfTexel;
            vec4 tapColor = textureLod(VolumetricFogHalfTemporal, tapUV, 0.0f);
            float tapRaw = textureLod(VolumetricFogHalfDepth, tapUV, 0.0f).r;
            float tapLinear = LinearEyeDistance(tapRaw, tapUV);

            float delta = abs(tapLinear - linearFull);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestColor = tapColor;
            }

            // Gaussian on linear-depth distance.
            float depthW = exp(-(delta * delta) / (2.0f * sigma * sigma));
            float w = spatial[index] * depthW;

            accum += tapColor * w;
            weightSum += w;
        }
    }

    // If nothing survived the depth rejection, fall back to the nearest tap.
    if (weightSum < 1e-4f)
    {
        OutColor = bestColor;
        return;
    }

    OutColor = accum / weightSum;
}
