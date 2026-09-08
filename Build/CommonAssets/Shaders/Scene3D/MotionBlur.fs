#version 450 core
uniform sampler2D MotionBlur;
uniform sampler2D Velocity;
uniform sampler2D DepthView;
#define XR_SCENE_FILTER_UV(uv) uv
#include "MotionBlur.glslinc"
