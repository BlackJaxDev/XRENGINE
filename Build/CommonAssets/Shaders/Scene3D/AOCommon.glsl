uniform int DepthMode;

bool AOIsFarDepth(float depth)
{
    const float eps = 1e-6f;
    return DepthMode == 1 ? depth <= eps : depth >= 1.0f - eps;
}

vec3 AOViewPosFromDepth(float depth, vec2 uv, mat4 projMatrix)
{
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = inverse(projMatrix) * clipSpacePosition;
    return viewSpacePosition.xyz / max(viewSpacePosition.w, 1e-5f);
}

float AOGaussianWeight(float distanceSquared, float sigma)
{
    float safeSigma = max(sigma, 1e-5f);
    return exp(-0.5f * distanceSquared / (safeSigma * safeSigma));
}