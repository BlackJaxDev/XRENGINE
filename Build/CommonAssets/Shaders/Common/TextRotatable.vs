#version 460

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

layout(std430, binding = 0) buffer GlyphTransformsBuffer
{
    vec4 GlyphTransforms[];
};
layout(std430, binding = 1) buffer GlyphTexCoordsBuffer
{
    vec4 GlyphTexCoords[];
};
layout(std430, binding = 2) buffer GlyphRotationsBuffer
{
    float GlyphRotations[];
};

uniform mat4 ModelMatrix;
uniform mat4 ViewProjectionMatrix_VTX;
uniform vec4 OutlineColor;
uniform float OutlineThickness;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 GlyphUVBounds;
layout (location = 20) out vec3 FragPosLocal;

const float PI = 3.14f;

mat2 rotationMatrix(float angle)
{
	angle *= PI / 180.0f;
    float sine = sin(angle), cosine = cos(angle);
    return mat2(cosine, -sine,
                sine,    cosine);
}

void main()
{
    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];
	float rot = GlyphRotations[gl_InstanceID];

	mat4 mvpMatrix = ViewProjectionMatrix_VTX * ModelMatrix;

    vec2 corner = Position.xy;
    vec2 glyphMin = tfm.xy;
    vec2 glyphSize = tfm.zw;
    vec2 uvMin = uv.xy;
    vec2 uvMax = uv.zw;
    if (OutlineThickness > 0.0 && OutlineColor.a > 0.0)
    {
        vec2 expand = vec2(OutlineThickness);
        vec2 uvExpand = (uvMax - uvMin) * (expand / max(abs(glyphSize), vec2(1e-6)));
        vec2 glyphDirection = vec2(
            glyphSize.x < 0.0 ? -1.0 : 1.0,
            glyphSize.y < 0.0 ? -1.0 : 1.0);
        glyphMin -= expand * glyphDirection;
        glyphSize += expand * 2.0 * glyphDirection;
        uvMin -= uvExpand;
        uvMax += uvExpand;
    }

	vec4 position = vec4((glyphMin + (corner * glyphSize)) * rotationMatrix(rot), 0.0f, 1.0f);
	
	FragPosLocal = position.xyz;
	FragPos = (mvpMatrix * position).xyz;
	gl_Position = mvpMatrix * position;
	FragNorm = Normal;
	FragUV0 = mix(uvMin, uvMax, corner);
	GlyphUVBounds = uv;
}
