#ifndef XRENGINE_VERTEX_EFFECTS_GLSL
#define XRENGINE_VERTEX_EFFECTS_GLSL

#include "vertex_manipulation.glsl"

void uberApplyOutlineLocal(
    inout vec3 position,
    vec3 normal,
    vec4 vertexColor,
    mat4 modelMatrix,
    vec2 uv,
    vec3 cameraPosition)
{
#if defined(XRENGINE_OUTLINE_PASS) && !defined(XRENGINE_UBER_DISABLE_OUTLINE)
    if (_SourceOutlineBasic != 0)
    {
        float mask = textureLod(_OutlineMask, uv, 0.0).r;
        float distanceFactor = 1.0;
        if (_OutlineFixedSize > 0.5)
        {
            vec3 worldPosition = (modelMatrix * vec4(position, 1.0)).xyz;
            float cameraDistance = length(cameraPosition - worldPosition);
            distanceFactor = mix(1.0, clamp(cameraDistance, 0.0, _OutlinesMaxDistance), _OutlineFixWidth);
        }
        position += normal * (_OutlineWidth * 0.01 * mask * distanceFactor);
        return;
    }

    float vertexWidth = mix(1.0, max(vertexColor.a, 0.0), clamp(_OutlineUseVertexColors, 0.0, 1.0));
    float width = _OutlineWidth * 0.01 * vertexWidth;
    vec3 direction = normal;
    if (_OutlineExpansionMode == 1)
        direction = normalize(position + normal * 0.001);
    else if (_OutlineExpansionMode == 2)
        direction = normalize(_OutlinePersonaDirection);
    else if (_OutlineExpansionMode == 3)
        direction = normalize(_OutlineDropShadowOffset);

    if (_OutlineExpansionMode == 3)
        position += _OutlineDropShadowOffset * width;
    else if (_OutlineSpace == 1)
    {
        vec3 worldDirection = normalize((u_NormalMatrix * direction));
        position += (inverse(modelMatrix) * vec4(worldDirection * width, 0.0)).xyz;
    }
    else if (_OutlineSpace != 2)
        position += normalize(direction) * width;
#endif
}

void uberApplyOutlineClip(inout vec4 clipPosition, vec3 normal, mat4 transform)
{
#if defined(XRENGINE_OUTLINE_PASS) && !defined(XRENGINE_UBER_DISABLE_OUTLINE)
    if (_SourceOutlineBasic == 0 && (_OutlineSpace == 2 || _OutlineFixedSize > 0.5))
    {
        vec2 screen = max(u_ScreenParams.xy, vec2(1.0));
        vec2 clipNormal = normalize((transform * vec4(normal, 0.0)).xy);
        clipPosition.xy += clipNormal * (_OutlineWidth * 2.0 / screen) * clipPosition.w;
    }
    clipPosition.z += _OutlineZOffset * 0.0001 * clipPosition.w;
#endif
}

#endif
