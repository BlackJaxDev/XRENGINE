#version 460

layout(location = 0) out vec4 OutColor;
layout(location = 1) out float OutFragmentCount;
layout(location = 0) in vec3 FragPos;

uniform sampler2D TransparentSceneCopyTex;
uniform usampler2D PpllHeadPointerTex;
uniform int PpllResolveFragmentLimit;

struct XRE_PpllNode
{
    vec4 Color;
    float Depth;
    uint Next;
    uint _Pad0;
    uint _Pad1;
};

layout(std430, binding = 24) readonly buffer PpllNodeBuffer
{
    XRE_PpllNode PpllNodes[];
};

layout(std430, binding = 25) readonly buffer PpllCounterBuffer
{
    uint PpllAllocatedNodeCount;
    uint PpllOverflowCount;
};

void main()
{
    vec2 uv = FragPos.xy * 0.5 + 0.5;
    vec4 sceneColor = texture(TransparentSceneCopyTex, uv);

    vec4 sortedColors[16];
    float sortedDepths[16];
    int storedCount = 0;
    int actualCount = 0;

    ivec2 pixel = ivec2(gl_FragCoord.xy);
    uint nodeIndex = texelFetch(PpllHeadPointerTex, pixel, 0).r;
    while (nodeIndex != 0xFFFFFFFFu && actualCount < 256)
    {
        XRE_PpllNode node = PpllNodes[nodeIndex];
        if (storedCount < PpllResolveFragmentLimit)
        {
            int insertIndex = storedCount;
            while (insertIndex > 0 && sortedDepths[insertIndex - 1] > node.Depth)
            {
                sortedDepths[insertIndex] = sortedDepths[insertIndex - 1];
                sortedColors[insertIndex] = sortedColors[insertIndex - 1];
                insertIndex--;
            }
            sortedDepths[insertIndex] = node.Depth;
            sortedColors[insertIndex] = node.Color;
            storedCount++;
        }

        actualCount++;
        nodeIndex = node.Next;
    }

    vec4 composite = sceneColor;
    for (int i = storedCount - 1; i >= 0; --i)
    {
        vec4 src = sortedColors[i];
        composite.rgb = src.rgb * src.a + composite.rgb * (1.0 - src.a);
        composite.a = src.a + composite.a * (1.0 - src.a);
    }

    OutColor = composite;
    OutFragmentCount = float(actualCount);
}
