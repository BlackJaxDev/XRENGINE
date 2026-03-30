#version 450
layout(triangles) in;
layout(line_strip, max_vertices=18) out;
//3 vertex points in a triangle * 3 normal lines (NBT) per triangle vertex * 2 vertices per line primitive = 18 vertices

layout (location = 1) in vec3 FragNormIn[];
layout (location = 2) in vec3 FragBinormIn[];
layout (location = 3) in vec3 FragTanIn[];
layout (location = 0) out vec3 FragPosOut;
layout (location = 4) out vec4 FragColorOut;

uniform float Magnitude = 0.5f;
uniform mat4 WorldToCameraSpaceMatrix;
uniform mat4 ProjMatrix;

void main()
{
  for (int i = 0; i < gl_in.length(); i++)
  {
    vec4 camPos = gl_in[i].gl_Position;

    gl_Position = camPos;
    FragColorOut = vec4(0.0f, 0.0f, 1.0f, 1.0f);
    EmitVertex();

    gl_Position = camPos + vec4((ProjMatrix * WorldToCameraSpaceMatrix * vec4(FragNormIn[i], 0.0f)).xyz * Magnitude, 0.0f);
    FragColorOut = vec4(0.0f, 0.0f, 1.0f, 1.0f);
    EmitVertex();

    EndPrimitive();

    gl_Position = camPos;
    FragColorOut = vec4(1.0f, 0.0f, 0.0f, 1.0f);
    EmitVertex();

    gl_Position = camPos + vec4((ProjMatrix * WorldToCameraSpaceMatrix * vec4(FragBinormIn[i], 0.0f)).xyz * Magnitude, 0.0f);
    FragColorOut = vec4(1.0f, 0.0f, 0.0f, 1.0f);
    EmitVertex();

    EndPrimitive();

    gl_Position = camPos;
    FragColorOut = vec4(0.0f, 1.0f, 0.0f, 1.0f);
    EmitVertex();

    gl_Position = camPos + vec4((ProjMatrix * WorldToCameraSpaceMatrix * vec4(FragTanIn[i], 0.0f)).xyz * Magnitude, 0.0f);
    FragColorOut = vec4(0.0f, 1.0f, 0.0f, 1.0f);
    EmitVertex();

    EndPrimitive();
  }
}
