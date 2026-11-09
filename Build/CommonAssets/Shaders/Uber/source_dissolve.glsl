#ifndef XR_SOURCE_DISSOLVE_GLSL
#define XR_SOURCE_DISSOLVE_GLSL

// Return the dissolved fraction and edge blend for source Basic dissolve.
// Keep this rule shared by canonical shading, native shading, and coverage.
vec3 xrSourceBasicDissolve(float noise, float detail, float mask,
    float amount, float width, float detailStrength, float invertNoise,
    vec4 parameters, float time)
{
    float progress = clamp(amount, 0.0, 1.0);
    if (parameters.w != 0.0)
        progress = sin(time * 0.05 * parameters.w) * 0.5 + 0.5;
    float threshold = mix(-width, 1.0, progress * mask);
    float strength = clamp(detailStrength, 0.0, 1.0);
    strength *= (1.0 - smoothstep(0.99, 1.0, threshold)) *
        mix(1.0, smoothstep(0.0, mix(0.01, 0.1, strength), threshold), parameters.z);
    if (invertNoise > 0.5) noise = 1.0 - noise;
    if (parameters.y > 0.5) detail = 1.0 - detail;
    noise = clamp(noise - detail * strength, 0.0, 1.0) * 0.998 + 0.001;
    float dissolved = step(noise, threshold);
    float edge = width > 0.0
        ? clamp((threshold + width - noise) / width, 0.0, 1.0) * (1.0 - dissolved)
        : 0.0;
    float hardness = clamp(parameters.x, 0.0, 1.0);
    float blend = hardness >= 1.0 ? (edge > 0.0 ? 1.0 : 0.0)
        : smoothstep(0.0, 1.0 - hardness, edge);
    return vec3(dissolved, edge, blend);
}

#endif
