#version 450 core
#extension GL_OVR_multiview2 : require
uniform sampler2DArray ColorSource;
uniform sampler2DArray DepthView;
#define XR_SCENE_FILTER_UV(uv) vec3(uv, gl_ViewID_OVR)
#include "DepthOfField.glslinc"
