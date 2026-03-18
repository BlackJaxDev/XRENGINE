// HashColor snippet
// Usage: #pragma snippet "HashColor"

vec3 XRENGINE_HashColor(uint id)
{
    uint x = id;
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;

    return vec3(
        float((x >> 0) & 255u),
        float((x >> 8) & 255u),
        float((x >> 16) & 255u)) / 255.0;
}
