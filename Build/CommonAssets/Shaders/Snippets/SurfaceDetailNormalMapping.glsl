// Surface-detail normal mapping: height map (Sobel 3x3) or RGB normal map.
//
// When XRENGINE_HEIGHTMAP_MODE is defined (compile-time, injected by ModelImporter for
// imported height/bump maps), the height-map path is unconditional and no NormalMapMode
// uniform is emitted — avoiding the runtime uniform pipeline entirely.
//
// Without the define, a runtime uniform selects the mode. The shader never
// infers the mode from texture contents; import/material metadata owns that.
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

bool XRENGINE_IsFiniteVec3(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

bool XRENGINE_TryBuildDerivativeTbn(vec3 positionWS, vec2 uv, vec3 normalWS, out mat3 tbn)
{
    vec3 positionDx = dFdx(positionWS);
    vec3 positionDy = dFdy(positionWS);
    vec2 uvDx = dFdx(uv);
    vec2 uvDy = dFdy(uv);
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    if (abs(determinant) < 1e-10)
        return false;

    vec3 N = normalize(normalWS);
    vec3 tangent = (positionDx * uvDy.y - positionDy * uvDx.y) / determinant;
    vec3 bitangent = (positionDy * uvDx.x - positionDx * uvDy.x) / determinant;
    tangent -= N * dot(N, tangent);
    if (!XRENGINE_IsFiniteVec3(tangent) || !XRENGINE_IsFiniteVec3(bitangent) || dot(tangent, tangent) < 1e-10)
        return false;

    tangent = normalize(tangent);
    float handedness = dot(cross(N, tangent), bitangent) < 0.0 ? -1.0 : 1.0;
    tbn = mat3(tangent, normalize(cross(N, tangent)) * handedness, N);
    return true;
}

vec3 XRENGINE_GetSurfaceDetailNormal(vec2 uv, vec3 positionWS, vec3 tangentWS, vec3 bitangentWS, vec3 normalWS)
{
    vec3 N = normalize(normalWS);
    if (!XRENGINE_IsFiniteVec3(N) || dot(N, N) <= 0.0)
        return vec3(0.0, 0.0, 1.0);

    float tangentLengthSq = dot(tangentWS, tangentWS);
    float bitangentLengthSq = dot(bitangentWS, bitangentWS);
    mat3 tbn;
    if (tangentLengthSq < 1e-10 || bitangentLengthSq < 1e-10)
    {
        // Generated vertex shaders use a zero TBN sentinel for meshes without
        // authored tangents. Reconstruct from world-position and UV derivatives
        // so tangent-space maps retain their source orientation.
        if (!XRENGINE_TryBuildDerivativeTbn(positionWS, uv, N, tbn))
            return N;
    }
    else
    {
        vec3 T = tangentWS - N * dot(N, tangentWS);
        vec3 B = bitangentWS - N * dot(N, bitangentWS);
        if (dot(T, T) < 1e-10 || dot(B, B) < 1e-10)
            return N;

        T = normalize(T);
        B = normalize(B);
        if (!XRENGINE_IsFiniteVec3(T) || !XRENGINE_IsFiniteVec3(B))
            return N;
        tbn = mat3(T, B, N);
    }

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
        vec3 sampledColor = texture(Texture1, uv).rgb;
        vec3 sampledNormal = sampledColor * 2.0 - 1.0;
        sampledNormal.y = -sampledNormal.y;
        if (!XRENGINE_IsFiniteVec3(sampledNormal) || dot(sampledNormal, sampledNormal) <= 1e-6)
            return N;

        tangentNormal = normalize(sampledNormal);
    }
#endif

    // Tangent-space normals must face outward (Z > 0). A negative Z means the
    // normal points into the surface, which zeroes out all lighting. This happens
    // when the normal map contains invalid data (e.g. black fallback texture:
    // (0,0,0)*2-1 = (-1,-1,-1) → Z = -0.577). Clamp to prevent total blackout.
    tangentNormal.z = max(tangentNormal.z, 0.001);
    tangentNormal = normalize(tangentNormal);

    vec3 mappedNormal = tbn * tangentNormal;
    if (!XRENGINE_IsFiniteVec3(mappedNormal) || dot(mappedNormal, mappedNormal) <= 1e-6)
        return N;

    return normalize(mappedNormal);
}
