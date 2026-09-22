#version 450 core

#if defined(XR_ADV_MULTIVIEW_RESOLVE)
#extension GL_EXT_multiview : require
#endif

layout(push_constant, std430) uniform XRAdvancedMsaaResolvePushConstants
{
    uint viewIndex;
    uint reversedDepth;
} XR_ADV_MsaaResolvePush;

void main()
{
#if defined(XR_ADV_MULTIVIEW_RESOLVE)
    // The layered target broadcasts each draw to every hardware view. Keep one
    // draw per frozen view so the raw array layer and canonical output layer
    // remain the same immutable ordinal.
    if (uint(gl_ViewIndex) != XR_ADV_MsaaResolvePush.viewIndex)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }
#endif

    int vertexIndex = gl_VertexIndex % 3;
    vec2 clipXY = vec2((vertexIndex << 1) & 2, vertexIndex & 2) * 2.0 - 1.0;
    gl_Position = vec4(clipXY, 0.0, 1.0);
}
