#version 450 core

layout(location = 0) in vec3 Position;
layout(location = 4) in vec2 TexCoord0;

layout(location = 4) out vec2 FragUV0;

// Provided by engine (see OctahedralImposterBillboard component requirements)
uniform mat4 ModelMatrix;
uniform mat4 ViewProjectionMatrix;
uniform mat4 InverseViewMatrix;

void main()
{
    // Build billboard basis from camera orientation (inverse view) so the quad faces the camera.
    vec3 right = normalize(InverseViewMatrix[0].xyz);
    vec3 up = normalize(InverseViewMatrix[1].xyz);

    // Respect scale baked into the model matrix columns.
    float sx = length(ModelMatrix[0].xyz);
    float sy = length(ModelMatrix[1].xyz);

    vec3 center = ModelMatrix[3].xyz;
    vec3 worldPos = center + right * Position.x * sx + up * Position.y * sy;

    gl_Position = ViewProjectionMatrix * vec4(worldPos, 1.0);
    FragUV0 = TexCoord0;
}
