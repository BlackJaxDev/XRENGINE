// Engine-authored compatibility include for translated Unity programs.
// It gives the clipping and color helpers of Unity's UnityUI.cginc.
#ifndef UNITY_UI_INCLUDED
#define UNITY_UI_INCLUDED

// Returns 1 when the position is in the rectangle (xy minimum, zw maximum), and 0 when it is not.
float UnityGet2DClipping(float2 position, float4 clipRect)
{
    float2 inside = step(clipRect.xy, position) * step(position, clipRect.zw);
    return inside.x * inside.y;
}

// Samples a UI texture and an optional separate alpha texture.
float4 UnityGetUIDiffuseColor(float2 position, sampler2D mainTexture, sampler2D alphaTexture, float4 textureSampleAdd)
{
    float4 color = tex2D(mainTexture, position) + textureSampleAdd;
#if defined(UNITY_UI_SPLIT_ALPHA)
    color.a = tex2D(alphaTexture, position).r;
#endif
    return color;
}

#endif // UNITY_UI_INCLUDED
