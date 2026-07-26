#ifndef XRENGINE_POIYOMI_VERTEX_EFFECTS_GLSL
#define XRENGINE_POIYOMI_VERTEX_EFFECTS_GLSL

mat3 poiEulerMatrix(vec3 radiansValue)
{
    vec3 c = cos(radiansValue);
    vec3 s = sin(radiansValue);
    return mat3(
        c.y * c.z, c.z * s.x * s.y - c.x * s.z, s.x * s.z + c.x * c.z * s.y,
        c.y * s.z, c.x * c.z + s.x * s.y * s.z, c.x * s.y * s.z - c.z * s.x,
        -s.y, c.y * s.x, c.x * c.y);
}

void poiApplyVertexEffects(
    inout vec3 position,
    inout vec3 normal,
    vec2 uv,
    vec4 vertexColor,
    vec3 cameraPosition,
    mat4 modelMatrix,
    float time)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_VERTEX_EFFECTS
    if (_PoiVertexEffectsEnabled <= 0.0001)
        return;

    vec3 scaleValue = mix(vec3(1.0), max(abs(_VertexManipulationLocalScale), vec3(0.0001)), _PoiVertexEffectsEnabled);
    position *= scaleValue;

    vec3 rotation = radians(_VertexManipulationLocalRotation + _VertexManipulationLocalRotationSpeed * time);
    mat3 rotationMatrix = poiEulerMatrix(rotation * _PoiVertexEffectsEnabled);
    position = rotationMatrix * position;
    normal = normalize(rotationMatrix * normal);

    position += _VertexManipulationLocalTranslation * _PoiVertexEffectsEnabled;
    position += normal * _VertexManipulationHeight * _PoiVertexEffectsEnabled;
    position += vertexColor.rgb * _PoiVertexColorPosition.xyz * (_PoiVertexColorPosition.w * _PoiVertexEffectsEnabled);
    normal = normalize(normal + (vertexColor.rgb * 2.0 - 1.0) * _PoiVertexColorNormal.xyz * _PoiVertexColorNormal.w);

    float glitchCell = floor((uv.y + time * _PoiVertexGlitch.y) * max(_PoiVertexGlitch.z, 1.0));
    float glitch = fract(sin(glitchCell * 91.3458) * 47453.5453) * 2.0 - 1.0;
    position.x += glitch * _PoiVertexGlitch.x * step(1.0 - clamp(_PoiVertexGlitch.w, 0.0, 1.0), abs(glitch));

    float uzumorePhase = dot(position, _PoiUzumore.xyz) + time * _PoiUzumore.w;
    position += normal * sin(uzumorePhase) * _PoiUzumore.w;
    position += normal * (
        sin(position.x * _PoiNaturalEquation.x + time * _PoiNaturalEquation.w) *
        cos(position.z * _PoiNaturalEquation.y - time * _PoiNaturalEquation.w) *
        _PoiNaturalEquation.z);

    if (_VertexRoundingEnabled > 0.5)
    {
        float interval = max(abs(_VertexRoundingDivision), 0.0001);
        position = floor(position / interval + 0.5) * interval;
    }

    if (_VertexBarrelMode > 0.5)
    {
        float barrel = smoothstep(0.0, max(_VertexBarrelHeight, 0.0001), abs(position.y));
        position.xz *= 1.0 + _VertexBarrelWidth * mix(_VertexBarrelAlpha, 1.0, barrel);
    }

    float bulge = exp(-dot(uv - _PoiDepthBulge.xy, uv - _PoiDepthBulge.xy) *
        max(_PoiDepthBulge.z, 0.0001));
    position += normal * bulge * _PoiDepthBulge.w;

    if (_PoiLookAtWeight > 0.0001)
    {
        vec3 worldOrigin = (modelMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
        vec3 targetDirection = normalize(cameraPosition - worldOrigin);
        vec3 authoredAxis = normalize(abs(_PoiLookAtAxis.x) + abs(_PoiLookAtAxis.y) + abs(_PoiLookAtAxis.z) > 0.001
            ? _PoiLookAtAxis
            : vec3(0.0, 0.0, 1.0));
        float angle = atan(targetDirection.x, targetDirection.z) - atan(authoredAxis.x, authoredAxis.z);
        mat3 lookRotation = poiEulerMatrix(vec3(0.0, angle * _PoiLookAtWeight, 0.0));
        position = lookRotation * position;
        normal = normalize(lookRotation * normal);
    }

    vec3 worldTranslation = _VertexManipulationWorldTranslation * _PoiVertexEffectsEnabled;
    position += (inverse(modelMatrix) * vec4(worldTranslation, 0.0)).xyz;
#endif
}

void poiApplyOutlineLocal(
    inout vec3 position,
    vec3 normal,
    vec4 vertexColor,
    mat4 modelMatrix)
{
#if defined(XRENGINE_OUTLINE_PASS) && !defined(XRENGINE_UBER_DISABLE_OUTLINE)
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

void poiApplyOutlineClip(inout vec4 clipPosition, vec3 normal, mat4 transform)
{
#if defined(XRENGINE_OUTLINE_PASS) && !defined(XRENGINE_UBER_DISABLE_OUTLINE)
    if (_OutlineSpace == 2 || _OutlineFixedSize > 0.5)
    {
        vec2 screen = max(u_ScreenParams.xy, vec2(1.0));
        vec2 clipNormal = normalize((transform * vec4(normal, 0.0)).xy);
        clipPosition.xy += clipNormal * (_OutlineWidth * 2.0 / screen) * clipPosition.w;
    }
    clipPosition.z += _OutlineZOffset * 0.0001 * clipPosition.w;
#endif
}

#endif
