#version 420

// Vertex Attributes
layout(location = 0)in vec4 ex_Color;
layout(location = 1)in vec2 ex_ObjectCoord;

// Out Params
layout(location = 0)out vec4 out_Color;

void main(void) {
  out_Color = ex_Color;
}
