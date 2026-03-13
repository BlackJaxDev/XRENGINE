uniform int NormalMapMode = 0;
uniform float HeightMapScale = 2.0f;

vec3 XRENGINE_GetSurfaceDetailNormal(vec2 uv, vec3 tangentWS, vec3 bitangentWS, vec3 normalWS)
{
    vec3 T = normalize(tangentWS);
    vec3 B = normalize(bitangentWS);
    vec3 N = normalize(normalWS);
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
        tangentNormal = normalize(texture(Texture1, uv).rgb * 2.0 - 1.0);
    }

    return normalize(tbn * tangentNormal);
}