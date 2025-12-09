#version 450

layout (location = 0) out vec4 OutAlbedoOpacity;
layout (location = 1) out vec3 OutNormal;
layout (location = 2) out vec4 OutRMSI;

uniform sampler2D AlbedoOpacity; //Screen AlbedoOpacity
uniform sampler2D Normal; //Screen Normal
uniform sampler2D RMSE; //Screen RMSI
uniform sampler2D DepthView; //Screen Depth
uniform sampler2D Texture4; //Decal AlbedoOpacity
//uniform sampler2D Texture5; //Decal Normal
//uniform sampler2D Texture6; //Decal Roughness / Metallic

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 ProjMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 BoxWorldMatrix;
uniform vec3 BoxHalfScale;

vec3 WorldPosFromDepth(in float depth, in vec2 uv)
{
	vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
	vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
	viewSpacePosition /= viewSpacePosition.w;
	return (InverseViewMatrix * viewSpacePosition).xyz;
}

void main()
{
	vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);

	//Retrieve shading information from GBuffer textures
	vec4 albedo = texture(AlbedoOpacity, uv);
	vec3 normal = texture(Normal, uv).rgb;
	vec4 rmsi = texture(RMSE, uv);
	float depth = texture(DepthView, uv).r;

	//Resolve world fragment position using depth and screen UV
	vec3 fragPosWS = WorldPosFromDepth(depth, uv);
	vec4 fragPosOS = (inverse(BoxWorldMatrix) * vec4(fragPosWS, 1.0f));
	fragPosOS.xyz /= BoxHalfScale;

  	if (abs(fragPosOS.x) > 1.0f ||
		abs(fragPosOS.y) > 1.0f ||
		abs(fragPosOS.z) > 1.0f)
    	discard;

  	vec2 decalUV = fragPosOS.xz * vec2(0.5f) + vec2(0.5f);
	float intensity = smoothstep(0.0f, 1.0f, 1.0f - abs(fragPosOS.y));

	vec3 viewDDYBinormal = normalize(dFdy(fragPosWS));
	vec3 viewDDXTangent = normalize(dFdx(fragPosWS));

	//orthonormalize
	//tangent = normalize(tangent - normal * dot(normal, tangent));

	vec3 normal2 = normalize(cross(viewDDXTangent, viewDDYBinormal));
	mat3 tbnToWorld = mat3(InverseViewMatrix) * mat3(viewDDXTangent, viewDDYBinormal, normal2);

	vec4 decalAlbedoOpacity = texture(Texture4, decalUV);
	//vec3 decalNormal = normal;//texture(Texture5, decalUV).rgb;
	//decalNormal = normalize(normal + mat3(BoxWorldMatrix) * (tbnToWorld * decalNormal));
	//vec4 decalRMSI = texture(Texture6, decalUV);

	decalAlbedoOpacity.rgb = mix(albedo.rgb, decalAlbedoOpacity.rgb, decalAlbedoOpacity.a);
	//decalNormal = mix(normal, decalNormal, decalAlbedo.a);
	//decalRMSI = mix(rmsi, decalRMSI, decalAlbedo.a);

	OutAlbedoOpacity = vec4(decalAlbedoOpacity.rgb, albedo.a);
  	OutNormal = normal;
	OutRMSI = rmsi;
}
