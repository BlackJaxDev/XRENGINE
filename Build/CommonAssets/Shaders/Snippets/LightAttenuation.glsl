// LightAttenuation snippet
// Usage: #pragma snippet "LightAttenuation"

float XRENGINE_Attenuate(float dist, float radius)
{
    return pow(clamp(1.0 - pow(dist / radius, 4.0), 0.0, 1.0), 2.0) / (dist * dist + 1.0);
}
