// Shared gl_PerVertex redeclaration for instanced debug geometry shaders.
out gl_PerVertex
{
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};
