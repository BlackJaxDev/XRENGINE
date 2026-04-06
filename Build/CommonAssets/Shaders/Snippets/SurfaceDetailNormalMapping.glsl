uniform int NormalMapMode = 0;
uniform float HeightMapScale = 2.0f;

bool XRENGINE_IsFiniteVec3(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

vec3 XRENGINE_GetSurfaceDetailNormal(vec2 uv, vec3 tangentWS, vec3 bitangentWS, vec3 normalWS)
{
    vec3 N = normalize(normalWS);
    if (!XRENGINE_IsFiniteVec3(N) || dot(N, N) <= 0.0)
        return vec3(0.0, 0.0, 1.0);

    float tangentLengthSq = dot(tangentWS, tangentWS);
    float bitangentLengthSq = dot(bitangentWS, bitangentWS);
    if (tangentLengthSq <= 1e-6 || bitangentLengthSq <= 1e-6)
        return N;

    vec3 T = tangentWS - N * dot(N, tangentWS);
    vec3 B = bitangentWS - N * dot(N, bitangentWS);
    if (dot(T, T) <= 1e-6 || dot(B, B) <= 1e-6)
        return N;

    T = normalize(T);
    B = normalize(B);
    if (!XRENGINE_IsFiniteVec3(T) || !XRENGINE_IsFiniteVec3(B))
        return N;

    mat3 tbn = mat3(T, B, N);

    vec3 tangentNormal;
    if (NormalMapMode == 1)
    {
        vec2 texelSize = 1.0 / vec2(textureSize(Texture1, 0));
        float hL = texture(Texture1, uv - vec2(texelSize.x, 0.0)).r;
        float hR = texture(Texture1, uv + vec2(texelSize.x, 0.0)).r;
        float hD = texture(Texture1, uv - vec2(0.0, texelSize.y)).r;
        float hU = texture(Texture1, uv + vec2(0.0, texelSize.y)).r;
        vec2 slope = vec2(hL - hR, hD - hU) * HeightMapScale;
        tangentNormal = normalize(vec3(slope, 1.0));
    }
    else
    {
        vec3 sampledNormal = texture(Texture1, uv).rgb * 2.0 - 1.0;
        if (!XRENGINE_IsFiniteVec3(sampledNormal) || dot(sampledNormal, sampledNormal) <= 1e-6)
            return N;

        tangentNormal = normalize(sampledNormal);
    }

    vec3 mappedNormal = tbn * tangentNormal;
    if (!XRENGINE_IsFiniteVec3(mappedNormal) || dot(mappedNormal, mappedNormal) <= 1e-6)
        return N;

    return normalize(mappedNormal);
}