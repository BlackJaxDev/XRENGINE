#ifndef XRENGINE_VERTEX_MANIPULATION_GLSL
#define XRENGINE_VERTEX_MANIPULATION_GLSL

mat3 uberEulerMatrix(vec3 radiansValue)
{
    vec3 c = cos(radiansValue);
    vec3 s = sin(radiansValue);
    return mat3(
        c.y * c.z, c.z * s.x * s.y - c.x * s.z, s.x * s.z + c.x * c.z * s.y,
        c.y * s.z, c.x * c.z + s.x * s.y * s.z, c.x * s.y * s.z - c.z * s.x,
        -s.y, c.y * s.x, c.x * c.y);
}

void uberApplyVertexEffects(
    inout vec3 position,
    inout vec3 normal,
    vec2 uv,
    vec4 vertexColor,
    vec3 cameraPosition,
    mat4 modelMatrix,
    float time)
{
#ifndef XRENGINE_UBER_DISABLE_VERTEX_EFFECTS
    if (_VertexEffectsEnabled <= 0.0001)
        return;

    vec3 scaleValue = mix(vec3(1.0), max(abs(_VertexManipulationLocalScale), vec3(0.0001)), _VertexEffectsEnabled);
    position *= scaleValue;

    vec3 rotation = radians(_VertexManipulationLocalRotation + _VertexManipulationLocalRotationSpeed * time);
    mat3 rotationMatrix = uberEulerMatrix(rotation * _VertexEffectsEnabled);
    position = rotationMatrix * position;
    normal = normalize(rotationMatrix * normal);

    position += _VertexManipulationLocalTranslation * _VertexEffectsEnabled;
    position += normal * _VertexManipulationHeight * _VertexEffectsEnabled;
    position += vertexColor.rgb * _VertexColorPositionOffset.xyz * (_VertexColorPositionOffset.w * _VertexEffectsEnabled);
    normal = normalize(normal + (vertexColor.rgb * 2.0 - 1.0) * _VertexColorNormalOffset.xyz * _VertexColorNormalOffset.w);

    float glitchCell = floor((uv.y + time * _VertexGlitch.y) * max(_VertexGlitch.z, 1.0));
    float glitch = fract(sin(glitchCell * 91.3458) * 47453.5453) * 2.0 - 1.0;
    position.x += glitch * _VertexGlitch.x * step(1.0 - clamp(_VertexGlitch.w, 0.0, 1.0), abs(glitch));

    float uzumorePhase = dot(position, _VertexWave.xyz) + time * _VertexWave.w;
    position += normal * sin(uzumorePhase) * _VertexWave.w;
    position += normal * (
        sin(position.x * _VertexEquation.x + time * _VertexEquation.w) *
        cos(position.z * _VertexEquation.y - time * _VertexEquation.w) *
        _VertexEquation.z);

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

    float bulge = exp(-dot(uv - _VertexDepthBulge.xy, uv - _VertexDepthBulge.xy) *
        max(_VertexDepthBulge.z, 0.0001));
    position += normal * bulge * _VertexDepthBulge.w;

    if (_VertexLookAtWeight > 0.0001)
    {
        vec3 worldOrigin = (modelMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
        vec3 targetDirection = normalize(cameraPosition - worldOrigin);
        vec3 authoredAxis = normalize(abs(_VertexLookAtAxis.x) + abs(_VertexLookAtAxis.y) + abs(_VertexLookAtAxis.z) > 0.001
            ? _VertexLookAtAxis
            : vec3(0.0, 0.0, 1.0));
        float angle = atan(targetDirection.x, targetDirection.z) - atan(authoredAxis.x, authoredAxis.z);
        mat3 lookRotation = uberEulerMatrix(vec3(0.0, angle * _VertexLookAtWeight, 0.0));
        position = lookRotation * position;
        normal = normalize(lookRotation * normal);
    }

    vec3 worldTranslation = _VertexManipulationWorldTranslation * _VertexEffectsEnabled;
    position += (inverse(modelMatrix) * vec4(worldTranslation, 0.0)).xyz;
#endif
}

#endif
