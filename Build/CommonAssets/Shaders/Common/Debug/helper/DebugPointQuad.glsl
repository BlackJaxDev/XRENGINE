// Emits a camera-facing quad for a debug point.
// Requires the following to be defined before including:
//   uniform mat4 InverseViewMatrix;
//   uniform mat4 ProjMatrix;
//   uniform float PointSize;
//   layout(location = 0) flat out vec4 MatColor;
//   layout(location = 1) out vec2 FragUV;
//   vec3 center  — world-space point position (set before including)
//   vec4 color   — point color (set before including)

void EmitPointQuad(mat4 viewProj, vec3 center, vec4 color)
{
    vec3 right = InverseViewMatrix[0].xyz;
    vec3 up    = InverseViewMatrix[1].xyz;

    float dist = length(center - InverseViewMatrix[3].xyz);
    right *= dist;
    up    *= dist;

    // Quad corners in world space
    vec3 offset1 = (-right - up) * PointSize;
    vec3 offset2 = ( right - up) * PointSize;
    vec3 offset3 = (-right + up) * PointSize;
    vec3 offset4 = ( right + up) * PointSize;

    MatColor = color;

    FragUV = vec2(-1.0, -1.0);
    gl_Position = viewProj * vec4(center + offset1, 1.0);
    EmitVertex();

    FragUV = vec2( 1.0, -1.0);
    gl_Position = viewProj * vec4(center + offset2, 1.0);
    EmitVertex();

    FragUV = vec2(-1.0,  1.0);
    gl_Position = viewProj * vec4(center + offset3, 1.0);
    EmitVertex();

    FragUV = vec2( 1.0,  1.0);
    gl_Position = viewProj * vec4(center + offset4, 1.0);
    EmitVertex();

    EndPrimitive();
}
