#version 450 core
#extension GL_OVR_multiview2 : require
uniform sampler2DArray MotionBlur;
uniform sampler2DArray Velocity;
uniform sampler2DArray DepthView;
#define XR_SCENE_FILTER_UV(uv) vec3(uv, gl_ViewID_OVR)
#include "MotionBlur.glslinc"
