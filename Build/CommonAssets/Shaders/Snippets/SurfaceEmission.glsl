// Material-surface emission is published by XRMaterial from semantic bindings.
// Mode zero preserves the legacy RMSE scalar contract for shaders that do not
// author an explicit surface-emission value.
uniform int SurfaceEmissionMode = 0;
uniform vec3 SurfaceEmissionColor = vec3(1.0);
uniform float SurfaceEmissionStrength = 0.0;
uniform int SurfaceEmissionHasTexture = 0;
uniform int SurfaceEmissionTextureIsSrgb = 0;
uniform int SurfaceEmissionTexCoordSet = 0;
uniform vec4 SurfaceEmissionUvScaleOffset = vec4(1.0, 1.0, 0.0, 0.0);
uniform float SurfaceEmissionUvRotation = 0.0;
uniform sampler2D SurfaceEmissionTexture;

vec3 XRENGINE_SrgbToLinear(vec3 color)
{
    vec3 safeColor = max(color, vec3(0.0));
    vec3 lower = safeColor / 12.92;
    vec3 upper = pow((safeColor + 0.055) / 1.055, vec3(2.4));
    return mix(lower, upper, step(vec3(0.04045), safeColor));
}

vec2 XRENGINE_ResolveSurfaceEmissionUv(vec2 uv0, vec2 uv1)
{
    vec2 uv = SurfaceEmissionTexCoordSet == 1 ? uv1 : uv0;
    float sine = sin(SurfaceEmissionUvRotation);
    float cosine = cos(SurfaceEmissionUvRotation);
    mat2 rotation = mat2(cosine, sine, -sine, cosine);
    return rotation * (uv * SurfaceEmissionUvScaleOffset.xy) + SurfaceEmissionUvScaleOffset.zw;
}

vec4 XRENGINE_ResolveSurfaceEmission(vec2 uv0, vec2 uv1, vec3 legacyBaseColor, float legacyEmission)
{
    if (SurfaceEmissionMode == 0)
        return vec4(legacyBaseColor * legacyEmission, 0.0);

    vec3 emission = SurfaceEmissionColor * SurfaceEmissionStrength;
    if (SurfaceEmissionHasTexture != 0)
    {
        vec3 sampled = texture(SurfaceEmissionTexture, XRENGINE_ResolveSurfaceEmissionUv(uv0, uv1)).rgb;
        if (SurfaceEmissionTextureIsSrgb != 0)
            sampled = XRENGINE_SrgbToLinear(sampled);
        emission *= sampled;
    }
    return vec4(emission, 1.0);
}
