#version 450

#ifdef XRENGINE_SHADOW_CASTER_PASS
layout (location = 0) out float Depth;
#else
layout (location = 0) out vec4 OutColor;
#endif
layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;
uniform float AlphaCutoff = 0.1f;

void main()
{
    vec4 color = texture(Texture0, FragUV0);
    if (color.a < AlphaCutoff)
      discard;

#ifdef XRENGINE_SHADOW_CASTER_PASS
    // Future tinted transmission: add a separate transmittance target and accumulate color-filtered light attenuation here instead of a depth-only write.
    Depth = gl_FragCoord.z;
#else
    OutColor = color;
#endif
}
