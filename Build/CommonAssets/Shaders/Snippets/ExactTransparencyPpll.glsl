layout(binding = 0, r32ui) uniform uimage2D PpllHeadPointers;

struct XRE_PpllNode
{
    vec4 Color;
    float Depth;
    uint Next;
    uint _Pad0;
    uint _Pad1;
};

layout(std430, binding = 24) buffer PpllNodeBuffer
{
    XRE_PpllNode PpllNodes[];
};

layout(std430, binding = 25) buffer PpllCounterBuffer
{
    uint PpllEmittedNodeCount;
    uint PpllOverflowFragmentCount;
    uint PpllRejectedPixelCount;
    uint PpllStatusFlags;
};

#ifndef XRENGINE_SCREEN_SIZE_UNIFORMS
#define XRENGINE_SCREEN_SIZE_UNIFORMS
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif
uniform uint PpllMaxNodes;

const uint XRE_PPLL_STATUS_OVERFLOW = 1u << 0;
const uint XRE_PPLL_STATUS_COUNTER_SATURATED = 1u << 1;

void XRE_RecordPpllOverflow()
{
    atomicOr(PpllStatusFlags, XRE_PPLL_STATUS_OVERFLOW);
    uint observed = PpllOverflowFragmentCount;
    while (observed != 0xFFFFFFFFu)
    {
        uint previous = atomicCompSwap(
            PpllOverflowFragmentCount,
            observed,
            observed + 1u);
        if (previous == observed)
            return;
        observed = previous;
    }

    atomicOr(PpllStatusFlags, XRE_PPLL_STATUS_COUNTER_SATURATED);
}

bool XRE_TryReservePpllNode(out uint nodeIndex)
{
    uint safeCapacity = min(PpllMaxNodes, uint(PpllNodes.length()));
    uint observed = PpllEmittedNodeCount;
    while (observed < safeCapacity)
    {
        uint previous = atomicCompSwap(
            PpllEmittedNodeCount,
            observed,
            observed + 1u);
        if (previous == observed)
        {
            nodeIndex = observed;
            return true;
        }
        observed = previous;
    }

    nodeIndex = 0u;
    return false;
}

void XRE_StorePerPixelLinkedListFragment(vec4 shadedColor)
{
    float alpha = clamp(shadedColor.a, 0.0, 1.0);
    if (alpha <= 0.0001)
        discard;

    ivec2 dimensions = imageSize(PpllHeadPointers);
    if (any(lessThanEqual(dimensions, ivec2(0))))
        discard;

    uint nodeIndex;
    if (!XRE_TryReservePpllNode(nodeIndex))
    {
        XRE_RecordPpllOverflow();
        discard;
    }

    PpllNodes[nodeIndex].Color = shadedColor;
    PpllNodes[nodeIndex].Depth = gl_FragCoord.z;
    PpllNodes[nodeIndex]._Pad0 = 0u;
    PpllNodes[nodeIndex]._Pad1 = 0u;

    ivec2 pixel = ivec2(clamp(
        gl_FragCoord.xy,
        vec2(0.0),
        vec2(max(dimensions - ivec2(1), ivec2(0)))));
    uint previousHead = imageAtomicExchange(PpllHeadPointers, pixel, nodeIndex);
    PpllNodes[nodeIndex].Next = previousHead;
}
