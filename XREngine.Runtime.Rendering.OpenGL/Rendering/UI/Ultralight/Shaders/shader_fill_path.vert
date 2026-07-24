#version 420

// Program Uniforms
layout(std140, binding=0) uniform Uniforms
{
  vec4 State;
  mat4 Transform;
  vec4 Scalar4[2];
  vec4 Vector[8];
  mat4 Clip[8];
  uint ClipSize;
};

// Vertex Attributes
layout(location = 0) in vec2 in_Position;
layout(location = 1) in vec4 in_Color;
layout(location = 2) in vec2 in_TexCoord;

// Out Params
layout(location = 0) out vec4 ex_Color;
layout(location = 1) out vec2 ex_ObjectCoord;

void main()
{
  ex_ObjectCoord = in_Position;
  gl_Position = Transform * vec4(in_Position, 0.0, 1.0);
  ex_Color = in_Color;
}
