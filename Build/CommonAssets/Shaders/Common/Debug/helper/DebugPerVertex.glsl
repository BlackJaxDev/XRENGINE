// Debug primitive geometry shaders only need the default gl_in[].gl_Position
// input and gl_Position output. Explicitly redeclaring gl_PerVertex here caused
// Vulkan/glslang to expose a different built-in block between VS and GS
// (notably CullDistance), which invalidates graphics pipeline creation.

uniform int ClipSpaceYDirection;

vec4 XRENGINE_DebugOutputPosition(vec4 position)
{
#ifdef XRENGINE_VULKAN
    // Late debug primitives are expanded in geometry shaders and composited
    // onto the post-process texture source. Mirror clip Y only for the
    // OpenGL-style Vulkan path; explicit Y-down clip space is corrected by the
    // final fallback blit so debug, skybox, and post-AA outputs agree.
    if (ClipSpaceYDirection == 0)
        position.y = -position.y;
#endif

    return position;
}
