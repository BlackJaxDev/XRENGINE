
//Vertex shader:
//The vertex shader will process the terrain's heightmap and calculate the position, normal, and texture coordinates for each vertex.

#version 450
#extension GL_ARB_separate_shader_objects : enable

layout (location = 0) in Vector2 inPosition;
layout (location = 1) in Vector2 inTexCoord;

layout (binding = 0) uniform sampler2D heightmap;

out Vector3 worldPos;
out Vector2 texCoord;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

float getHeight(Vector2 uv)
{
    return texture(heightmap, uv).r;
}

void main()
{
    Vector3 position = Vector3(inPosition, getHeight(inTexCoord));
    worldPos = Vector3(model * Vector4(position, 1.0));
    gl_Position = projection * view * Vector4(worldPos, 1.0);
    texCoord = inTexCoord;
}