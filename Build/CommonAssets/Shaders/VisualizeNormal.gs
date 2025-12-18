#version 450
layout(triangles) in;
layout(line_strip, max_vertices=6) out;

layout (location = 0) in vec3 FragPosIn[];
layout (location = 1) in vec3 FragNormIn[];
layout (location = 4) in vec2 FragUV0In[];
layout (location = 0) out vec3 FragPosOut;
layout (location = 1) out vec3 FragNormOut;
layout (location = 4) out vec2 FragUV0Out;

uniform float Magnitude = 0.5f;
uniform mat4 WorldToCameraSpaceMatrix;
uniform mat4 ProjMatrix;

in gl_PerVertex
{
  vec4  gl_Position;
  float gl_PointSize;
  float gl_ClipDistance[];
} gl_in[];

out gl_PerVertex
{
  vec4  gl_Position;
  float gl_PointSize;
  float gl_ClipDistance[];
};

void main()
{
  for (int i = 0; i < gl_in.length(); i++)
  {
    vec4 camPos = gl_in[i].gl_Position;
    vec3 fragPos = FragPosIn[i];
    vec3 fragNorm = FragNormIn[i];
    vec2 fragUV0 = FragUV0In[i];

    gl_Position = camPos;
    FragPosOut = fragPos;
    FragNormOut = fragNorm;
    FragUV0Out = fragUV0;
    EmitVertex();

    camPos.xyz += (ProjMatrix * WorldToCameraSpaceMatrix * vec4(fragNorm, 0.0f)).xyz * Magnitude;

    gl_Position = camPos;
    FragPosOut = fragPos + fragNorm * Magnitude;
    FragNormOut = fragNorm;
    FragUV0Out = fragUV0;
    EmitVertex();

    EndPrimitive();
  }
}
