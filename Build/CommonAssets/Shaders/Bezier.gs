#version 330 core
layout (points) in;
layout (triangle_strip, max_vertices=256) out;

uniform vec2 StartPos;
uniform vec2 StartCPos;
uniform vec2 EndCPos;
uniform vec2 EndPos;

layout(location = 0) out vec3 FragPos;

vec2 evalBezier(float delta, int i, vec2 P0, vec2 P1, vec2 P2, vec2 P3)
{
    float t = delta * float(i);
    float t2 = t * t;
    float one_minus_t = 1.0 - t;
    float one_minus_t2 = one_minus_t * one_minus_t;
    return (P0 * one_minus_t2 * one_minus_t + P1 * 3.0 * t * one_minus_t2 + P2 * 3.0 * t2 * one_minus_t + P3 * t2 * t);
}

void main()
{
	vec4 pos;
	for (int face = 0; face < 6; ++face)
	{
		gl_Layer = face;
		for (int i = 0; i < 3; ++i)
		{
			pos = gl_in[i].gl_Position;
			FragPos = pos.xyz;
			gl_Position = ShadowMatrices[face] * pos;
			EmitVertex();
		}    
		EndPrimitive();
	}
} 