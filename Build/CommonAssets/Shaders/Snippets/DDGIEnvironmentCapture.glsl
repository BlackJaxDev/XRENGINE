#ifndef XR_DDGI_ENVIRONMENT_CAPTURE_INCLUDED
#define XR_DDGI_ENVIRONMENT_CAPTURE_INCLUDED

#ifdef XRENGINE_DDGI_ENVIRONMENT_CAPTURE
#pragma snippet "OctahedralMapping"
uniform vec2 uDDGIEnvironmentInvResolution;
uniform float uDDGIEnvironmentRotation;
#endif

// A fullscreen triangle cannot interpolate an octahedral direction faithfully
// across the map fold, so capture variants reconstruct it per fragment.
vec3 XRENGINE_DDGIEnvironmentDirection(vec3 interpolatedDirection)
{
#ifdef XRENGINE_DDGI_ENVIRONMENT_CAPTURE
    vec3 direction = XRENGINE_DecodeOcta(gl_FragCoord.xy * uDDGIEnvironmentInvResolution);
    float cosRotation = cos(uDDGIEnvironmentRotation);
    float sinRotation = sin(uDDGIEnvironmentRotation);
    return vec3(
        direction.x * cosRotation - direction.z * sinRotation,
        direction.y,
        direction.x * sinRotation + direction.z * cosRotation);
#else
    return normalize(interpolatedDirection);
#endif
}

#endif
