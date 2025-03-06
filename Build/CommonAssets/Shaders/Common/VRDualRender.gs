#version 460

//This shader expects triangles as input and outputs a triangle strip.
//We set max_vertices to 6 since we're emitting 3 vertices for each of our 2 copies.
layout(triangles) in;
layout(triangle_strip, max_vertices = 6) out;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;

uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;

void main()
{
    //Left eye is layer 0
    gl_Layer = 0;
    for (int j = 0; j < 3; j++)
    {
        vec4 worldPos = gl_in[j].gl_Position;
        vec4 viewPos = LeftEyeInverseViewMatrix * worldPos;
        gl_Position = LeftEyeProjMatrix * viewPos;
        EmitVertex();
    }
    EndPrimitive();

    //Right eye is layer 1
    gl_Layer = 1;
    for (int j = 0; j < 3; j++)
    {
        vec4 worldPos = gl_in[j].gl_Position;
        vec4 viewPos = RightEyeInverseViewMatrix * worldPos;
        gl_Position = RightEyeProjMatrix * viewPos;
        EmitVertex();
    }
    EndPrimitive();
}