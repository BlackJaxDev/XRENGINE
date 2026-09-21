#ifndef XR_DDGI_ENVIRONMENT_INCLUDED
#define XR_DDGI_ENVIRONMENT_INCLUDED

#pragma snippet "DDGIBindings"
// DDGISampling supplies the octahedral mapping shared by the DDGI hit shader.

#ifndef DDGI_ENVIRONMENT_BINDING
#define DDGI_ENVIRONMENT_BINDING 5
#endif

// A per-pipeline octahedral radiance map. DDGIEnvironmentResources renders the
// active authored skybox into this target before the hit shading dispatch.
layout(XR_DDGI_SAMPLER_BINDING(DDGI_ENVIRONMENT_BINDING)) uniform sampler2D uDDGIEnvironment;
uniform int uDDGIEnvironmentAvailable;
uniform vec3 uDDGIEnvironmentFallback;

vec3 DDGISampleEnvironment(vec3 direction)
{
    if (uDDGIEnvironmentAvailable == 0)
        return uDDGIEnvironmentFallback;
    return textureLod(uDDGIEnvironment, XRENGINE_EncodeOcta(normalize(direction)), 0.0).rgb;
}

#endif
