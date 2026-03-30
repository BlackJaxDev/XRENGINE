#version 450
layout (triangles) in;
layout (triangle_strip, max_vertices = 3) out;

layout (location = 1) in vec3 FragNormIn[];
layout (location = 1) out vec3 FragNormOut;

uniform float Magnitude = 0.075f;

vec4 Explode(vec4 position, vec3 normal)
{
    vec3 direction = normal * Magnitude;
    return position + vec4(direction, 0.0);
} 
void main()
{
    FragNormOut = FragNormIn[0];
    gl_Position = Explode(gl_in[0].gl_Position, FragNormOut);
    EmitVertex();

    FragNormOut = FragNormIn[1];
    gl_Position = Explode(gl_in[1].gl_Position, FragNormOut);
    EmitVertex();

    FragNormOut = FragNormIn[2];
    gl_Position = Explode(gl_in[2].gl_Position, FragNormOut);
    EmitVertex();

    EndPrimitive();
}  