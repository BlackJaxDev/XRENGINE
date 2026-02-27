// Emits a single debug triangle as a triangle strip.
// Requires the following to be defined before including:
//   layout(location = 0) flat out vec4 MatColor;

void EmitTriangle(mat4 viewProj, vec3 p0, vec3 p1, vec3 p2, vec4 color)
{
    MatColor = color;

    gl_Position = viewProj * vec4(p0, 1.0);
    EmitVertex();

    gl_Position = viewProj * vec4(p1, 1.0);
    EmitVertex();

    gl_Position = viewProj * vec4(p2, 1.0);
    EmitVertex();

    EndPrimitive();
}
