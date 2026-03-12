#version 450

uniform vec4 MatColor;

#pragma snippet "ExactTransparencyPpll"

void main()
{
    XRE_StorePerPixelLinkedListFragment(MatColor);
}