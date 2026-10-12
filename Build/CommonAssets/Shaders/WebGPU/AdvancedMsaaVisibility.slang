// Reversible raw visibility storage for the Core WebGPU four-sample family.
uint4 XR_WEB_PackVisibilityPair(uint2 value)
{
    return uint4(value.x & 0xffffu, value.x >> 16u,
        value.y & 0xffffu, value.y >> 16u);
}

uint2 XR_WEB_UnpackVisibilityPair(uint4 value)
{
    return uint2(value.x | (value.y << 16u), value.z | (value.w << 16u));
}

bool XR_WEB_IsCoveredSample(uint2 identity)
{
    return identity.x != 0u && identity.x != 0xffffffffu;
}

bool XR_WEB_IsValidSampleDepth(float depth)
{
    return !isnan(depth) && !isinf(depth) && depth >= 0.0 && depth <= 1.0;
}

bool XR_WEB_IsCloserSample(float candidate, float current, bool reversedDepth)
{
    return reversedDepth ? candidate > current : candidate < current;
}

float2 XR_WEB_SamplePosition(uint sampleIndex)
{
    // WebGPU mandates this order and these framebuffer-space positions for x4.
    // Every component is exactly representable in the canonical RG16F sidecar.
    static const float2 positions[4] = {
        float2(0.375, 0.125), float2(0.875, 0.375),
        float2(0.125, 0.625), float2(0.625, 0.875)
    };
    return positions[sampleIndex];
}
