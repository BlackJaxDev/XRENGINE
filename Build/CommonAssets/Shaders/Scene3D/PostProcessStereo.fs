#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray HDRSceneTex; //HDR scene color
uniform sampler2DArray BloomBlurTexture; //Bloom
uniform sampler2DArray DepthView; //Depth
uniform usampler2DArray StencilView; //Stencil

uniform float ChromaticAberrationIntensity;

// Lens distortion mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini
uniform int LensDistortionMode;
uniform float LensDistortionIntensity;
uniform float PaniniDistance;
uniform float PaniniCrop;
uniform vec2 PaniniViewExtents; // tan(fov/2) * aspect, tan(fov/2)

struct ColorGradeStruct
{
    vec3 Tint;

    float Exposure;
    float Contrast;
    float Gamma;

    float Hue;
    float Saturation;
    float Brightness;
};
uniform ColorGradeStruct ColorGrade;

vec2 ApplyLensDistortion(vec2 uv, float intensity)
{
    uv -= vec2(0.5);
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
    return vec2(0.5) + vec2(sin(uva), cos(uva)) * uvd;
}

// Panini projection - preserves vertical lines while compressing horizontal periphery
// Based on Unity's implementation
vec2 ApplyPaniniProjection(vec2 view_pos, float d)
{
    float view_dist = 1.0 + d;
    float view_hyp_sq = view_pos.x * view_pos.x + view_dist * view_dist;
    
    float isect_D = view_pos.x * d;
    float isect_discrim = view_hyp_sq - isect_D * isect_D;
    
    float cyl_dist_minus_d = (-isect_D * view_pos.x + view_dist * sqrt(max(isect_discrim, 0.0))) / view_hyp_sq;
    float cyl_dist = cyl_dist_minus_d + d;
    
    vec2 cyl_pos = view_pos * (cyl_dist / view_dist);
    return cyl_pos / (cyl_dist - d);
}

vec2 ApplyLensDistortionByMode(vec2 uv)
{
    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
            return ApplyLensDistortion(uv, LensDistortionIntensity);
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            vec2 view_pos = (2.0 * uv - 1.0) * PaniniViewExtents * PaniniCrop;
            vec2 proj_pos = ApplyPaniniProjection(view_pos, PaniniDistance);
            vec2 proj_ndc = proj_pos / PaniniViewExtents;
            return proj_ndc * 0.5 + 0.5;
        }
    }
    return uv;
}

float rand(vec2 coord)
{
    return fract(sin(dot(coord, vec2(12.9898f, 78.233f))) * 43758.5453f);
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0 || uv.y > 1.0)
        discard;
    // uv is now normalized to [0, 1]
    vec2 duv = ApplyLensDistortionByMode(uv);

    vec3 uvi = vec3(duv, gl_ViewID_OVR);

    vec3 hdrSceneColor;

    if (ChromaticAberrationIntensity > 0.0)
    {
        vec2 dir = duv - 0.5;
        vec2 off = dir * ChromaticAberrationIntensity * 0.1;

        vec2 uvR = clamp(duv + off, vec2(0.0), vec2(1.0));
        vec2 uvG = duv;
        vec2 uvB = clamp(duv - off, vec2(0.0), vec2(1.0));

        hdrSceneColor = vec3(
            texture(HDRSceneTex, vec3(uvR, gl_ViewID_OVR)).r,
            texture(HDRSceneTex, vec3(uvG, gl_ViewID_OVR)).g,
            texture(HDRSceneTex, vec3(uvB, gl_ViewID_OVR)).b);
    }
    else
    {
        hdrSceneColor = texture(HDRSceneTex, uvi).rgb;
    }

    // Add each blurred bloom mipmap
    // Starts at 1/2 size lod because original image is not blurred (and doesn't need to be)
        for (float lod = 1.0; lod < 5.0; lod += 1.0)
        {
            vec3 bloomSample = textureLod(BloomBlurTexture, vec3(duv, gl_ViewID_OVR), lod).rgb;
            hdrSceneColor += bloomSample;
        }

    // Tone mapping
    vec3 ldrSceneColor = vec3(1.0) - exp(-hdrSceneColor * ColorGrade.Exposure);

    // Gamma-correct
    ldrSceneColor = pow(ldrSceneColor, vec3(1.0 / ColorGrade.Gamma));
    // Fix subtle banding by applying fine noise
    ldrSceneColor += mix(-0.5 / 255.0, 0.5 / 255.0, rand(uv));

    OutColor = vec4(ldrSceneColor, 1.0);
}
