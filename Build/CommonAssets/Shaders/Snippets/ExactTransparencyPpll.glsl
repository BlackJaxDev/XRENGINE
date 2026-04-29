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
    uint PpllAllocatedNodeCount;
    uint PpllOverflowCount;
};

#ifndef XRENGINE_SCREEN_SIZE_UNIFORMS
#define XRENGINE_SCREEN_SIZE_UNIFORMS
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif
uniform int PpllMaxNodes;

void XRE_StorePerPixelLinkedListFragment(vec4 shadedColor)
{
    float alpha = clamp(shadedColor.a, 0.0, 1.0);
    if (alpha <= 0.0001)
        discard;

    uint nodeIndex = atomicAdd(PpllAllocatedNodeCount, 1u);
    if (nodeIndex >= uint(PpllMaxNodes))
    {
        atomicAdd(PpllOverflowCount, 1u);
        discard;
    }

    ivec2 pixel = ivec2(clamp(gl_FragCoord.xy, vec2(0.0), vec2(ScreenWidth - 1.0, ScreenHeight - 1.0)));
    uint previousHead = imageAtomicExchange(PpllHeadPointers, pixel, nodeIndex);

    PpllNodes[nodeIndex].Color = shadedColor;
    PpllNodes[nodeIndex].Depth = gl_FragCoord.z;
    PpllNodes[nodeIndex].Next = previousHead;
    PpllNodes[nodeIndex]._Pad0 = 0u;
    PpllNodes[nodeIndex]._Pad1 = 0u;
}
