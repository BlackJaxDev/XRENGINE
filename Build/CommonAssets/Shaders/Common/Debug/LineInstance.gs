#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 1) in vec3 vPos[];
layout(location = 0) out vec4 MatColor;

layout(std430, binding = 0) buffer LinesBuffer
{
    vec4 Lines[];
};

out gl_PerVertex
{
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;
uniform float LineWidth;
uniform int TotalLines;

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    // Touch vPos to ensure Position attribute is kept alive across stages
    vec3 anchor = vPos[0];
    anchor *= 0.0f;

    int index = instanceID[0] * 3;
    vec4 start = viewProj * vec4(Lines[index].xyz + anchor, 1.0);
    vec4 end = viewProj * vec4(Lines[index + 1].xyz + anchor, 1.0);
    MatColor = Lines[index + 2];

    vec2 ndcStart = start.xy / start.w;
    vec2 ndcEnd = end.xy / end.w;
    vec2 dir = normalize(ndcEnd - ndcStart);
    vec2 perp = vec2(-dir.y, dir.x);

    vec4 startOffset = vec4(perp * LineWidth * start.w, 0.0, 0.0);
    vec4 endOffset = vec4(perp * LineWidth * end.w, 0.0, 0.0);

    gl_Position = start + startOffset;
    EmitVertex();

    gl_Position = start - startOffset;
    EmitVertex();

    gl_Position = end + endOffset;
    EmitVertex();

    gl_Position = end - endOffset;
    EmitVertex();

    EndPrimitive();
}