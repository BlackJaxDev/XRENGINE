#version 460

layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;

#pragma snippet "ExactTransparencyPpll"

void main()
{
    XRE_StorePerPixelLinkedListFragment(texture(Texture0, FragUV0));
}