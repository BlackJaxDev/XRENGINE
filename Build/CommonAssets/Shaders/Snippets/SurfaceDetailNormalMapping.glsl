// Surface-detail normal mapping: height map (Sobel 3x3) or RGB normal map.
//
// When XRENGINE_HEIGHTMAP_MODE is defined (compile-time, injected by ModelImporter for
// imported height/bump maps), the height-map path is unconditional and no NormalMapMode
// uniform is emitted — avoiding the runtime uniform pipeline entirely.
//
// Without the define, a runtime uniform selects the mode:
//   0 = RGB normal map (default)
//   1 = height map (Sobel 3x3 slopes)
#ifndef XRENGINE_HEIGHTMAP_MODE
uniform int NormalMapMode = 0;
#endif
uniform float HeightMapScale = 1.0f;

// Sobel 3x3 height-to-normal: averages slope over a 3x3 neighborhood, producing
// much smoother normals than simple central differences on high-contrast bump maps
// (e.g. Sponza brick mortar lines, lion relief detail).
vec3 XRENGINE_HeightToNormalSobel(vec2 uv)
{
    vec2 texelSize = 1.0 / vec2(textureSize(Texture1, 0));
    float tl = texture(Texture1, uv + vec2(-texelSize.x, -texelSize.y)).r;
    float t  = texture(Texture1, uv + vec2( 0.0,         -texelSize.y)).r;
    float tr = texture(Texture1, uv + vec2( texelSize.x, -texelSize.y)).r;
    float l  = texture(Texture1, uv + vec2(-texelSize.x,  0.0        )).r;
    float r  = texture(Texture1, uv + vec2( texelSize.x,  0.0        )).r;
    float bl = texture(Texture1, uv + vec2(-texelSize.x,  texelSize.y)).r;
    float b  = texture(Texture1, uv + vec2( 0.0,          texelSize.y)).r;
    float br = texture(Texture1, uv + vec2( texelSize.x,  texelSize.y)).r;
    // Sobel kernels (divided by 4 to keep magnitude in a usable range).
    float slopeX = ((tl + 2.0 * l + bl) - (tr + 2.0 * r + br)) * 0.25;
    float slopeY = ((tl + 2.0 * t + tr) - (bl + 2.0 * b + br)) * 0.25;
    return normalize(vec3(vec2(slopeX, slopeY) * HeightMapScale, 1.0));
}

vec3 XRENGINE_GetSurfaceDetailNormal(vec2 uv, vec3 tangentWS, vec3 bitangentWS, vec3 normalWS)
{
    vec3 N = normalize(normalWS);

    // Guard against degenerate tangent/bitangent (e.g., mesh has no tangent data
    // and the fragment inputs are zero/undefined). normalize(vec3(0)) is NaN on
    // most GPUs, which poisons the entire TBN matrix and produces black lighting.
    float tLen2 = dot(tangentWS, tangentWS);
    float bLen2 = dot(bitangentWS, bitangentWS);
    if (tLen2 < 1e-10 || bLen2 < 1e-10)
        return N;

    vec3 T = normalize(tangentWS);
    vec3 B = normalize(bitangentWS);
    mat3 tbn = mat3(T, B, N);

    vec3 tangentNormal;
#ifdef XRENGINE_HEIGHTMAP_MODE
    tangentNormal = XRENGINE_HeightToNormalSobel(uv);
#else
    if (NormalMapMode == 1)
    {
        tangentNormal = XRENGINE_HeightToNormalSobel(uv);
    }
    else
    {
        tangentNormal = normalize(texture(Texture1, uv).rgb * 2.0 - 1.0);
    }
#endif

    // Tangent-space normals must face outward (Z > 0). A negative Z means the
    // normal points into the surface, which zeroes out all lighting. This happens
    // when the normal map contains invalid data (e.g. black fallback texture:
    // (0,0,0)*2-1 = (-1,-1,-1) → Z = -0.577). Clamp to prevent total blackout.
    tangentNormal.z = max(tangentNormal.z, 0.001);
    tangentNormal = normalize(tangentNormal);

    return normalize(tbn * tangentNormal);
}