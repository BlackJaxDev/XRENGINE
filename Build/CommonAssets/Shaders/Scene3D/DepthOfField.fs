#version 450 core
uniform sampler2D ColorSource;
uniform sampler2D DepthView;
#define XR_SCENE_FILTER_UV(uv) uv
#include "DepthOfField.glslinc"
