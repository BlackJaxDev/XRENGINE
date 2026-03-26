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

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 GlyphUVBounds;
layout (location = 20) out vec3 FragPosLocal;

out gl_PerVertex
{
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

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
	
	vec4 position = vec4((tfm.xy + (TexCoord0.xy * tfm.zw)) * rotationMatrix(rot), 0.0f, 1.0f);
	
	FragPosLocal = position.xyz;
	FragPos = (mvpMatrix * position).xyz;
	gl_Position = mvpMatrix * position;
	FragNorm = Normal;
	FragUV0 = mix(uv.xy, uv.zw, Position.xy);
	GlyphUVBounds = uv;
}