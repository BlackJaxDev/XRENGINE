#version 450

layout(location = 0) out vec4 OutMask;

uniform uint XRENGINE_EditorHighlightFlags; // XRENGINE_FREQUENCY(Object)

void main()
{
    OutMask = vec4(
        (XRENGINE_EditorHighlightFlags & 1u) != 0u ? 1.0 : 0.0,
        (XRENGINE_EditorHighlightFlags & 2u) != 0u ? 1.0 : 0.0,
        0.0,
        1.0);
}
