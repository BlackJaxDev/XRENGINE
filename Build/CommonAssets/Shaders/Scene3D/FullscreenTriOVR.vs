#version 450
#extension GL_OVR_multiview2 : require

// Must match the two-layer fullscreen output framebuffer's OVR view count.
layout(num_views = 2) in;
layout(location = 0) out vec3 FragPos;

void main()
{
    int vertexId = gl_VertexID % 3;
    vec2 clipXY = vec2((vertexId << 1) & 2, vertexId & 2) * 2.0 - 1.0;
    FragPos = vec3(clipXY, 0.0);
    gl_Position = vec4(clipXY, 0.0, 1.0);
}
