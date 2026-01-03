#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray HDRSceneTex; //HDR scene color
uniform sampler2DArray BloomBlurTexture; //Bloom
uniform sampler2DArray DepthView; //Depth
uniform usampler2DArray StencilView; //Stencil

// 1x1 R32F texture containing the current exposure value (GPU-driven auto exposure)
uniform sampler2D AutoExposureTex;
uniform bool UseGpuAutoExposure;

uniform float ChromaticAberrationIntensity;

// Lens distortion mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini, 4=BrownConrady
uniform int LensDistortionMode;
uniform float LensDistortionIntensity;
uniform vec2 LensDistortionCenter;
uniform float PaniniDistance;
uniform float PaniniCrop;
uniform vec2 PaniniViewExtents; // tan(fov/2) * aspect, tan(fov/2)

// Brown-Conrady coefficients
uniform vec3 BrownConradyRadial;     // k1,k2,k3
uniform vec2 BrownConradyTangential; // p1,p2

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

vec3 RGBtoHSV(vec3 c)
{
    vec4 K = vec4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10f;
    return vec3(abs(q.z + (q.w - q.y) / (6.0f * d + e)), d / (q.x + e), q.x);
}
vec3 HSVtoRGB(vec3 c)
{
    vec4 K = vec4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0f - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0f, 1.0f), c.y);
}

float GetExposure()
{
    if (UseGpuAutoExposure)
    {
        float e = texelFetch(AutoExposureTex, ivec2(0, 0), 0).r;
        if (!(isnan(e) || isinf(e)) && e > 0.0)
            return e;
    }
    return ColorGrade.Exposure;
}

vec2 ApplyLensDistortion(vec2 uv, float intensity, vec2 center)
{
    uv -= center;
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
    return center + vec2(sin(uva), cos(uva)) * uvd;
}

vec2 ApplyBrownConrady(vec2 uvCentered)
{
    vec2 x = uvCentered * 2.0 - 1.0;
    float r2 = dot(x, x);
    float r4 = r2 * r2;
    float r6 = r4 * r2;

    float k1 = BrownConradyRadial.x;
    float k2 = BrownConradyRadial.y;
    float k3 = BrownConradyRadial.z;
    float p1 = BrownConradyTangential.x;
    float p2 = BrownConradyTangential.y;

    float radial = 1.0 + k1 * r2 + k2 * r4 + k3 * r6;
    vec2 tangential = vec2(
        2.0 * p1 * x.x * x.y + p2 * (r2 + 2.0 * x.x * x.x),
        p1 * (r2 + 2.0 * x.y * x.y) + 2.0 * p2 * x.x * x.y);

    vec2 xd = x * radial + tangential;
    return xd * 0.5 + 0.5;
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
    // Recenter so principal point maps to UV 0.5,0.5 for distortion models.
    vec2 uvCentered = uv - LensDistortionCenter + vec2(0.5);

    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
            return ApplyLensDistortion(uvCentered, LensDistortionIntensity, vec2(0.5));
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            vec2 view_pos = (2.0 * uvCentered - 1.0) * PaniniViewExtents * PaniniCrop;
            vec2 proj_pos = ApplyPaniniProjection(view_pos, PaniniDistance);
            vec2 proj_ndc = proj_pos / PaniniViewExtents;
            vec2 outCentered = proj_ndc * 0.5 + 0.5;
            return outCentered - vec2(0.5) + LensDistortionCenter;
        }
    }
    else if (LensDistortionMode == 4)
    {
        vec2 outCentered = ApplyBrownConrady(uvCentered);
        return outCentered - vec2(0.5) + LensDistortionCenter;
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
        vec2 dir = duv - LensDistortionCenter;
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
    vec3 ldrSceneColor = vec3(1.0) - exp(-hdrSceneColor * GetExposure());

    // Color grading (LDR)
    ldrSceneColor *= ColorGrade.Tint;

    if (ColorGrade.Hue != 1.0f || ColorGrade.Saturation != 1.0f || ColorGrade.Brightness != 1.0f)
    {
        vec3 hsv = RGBtoHSV(clamp(ldrSceneColor, vec3(0.0f), vec3(1.0f)));
        hsv.x = fract(hsv.x * ColorGrade.Hue);
        hsv.y = clamp(hsv.y * ColorGrade.Saturation, 0.0f, 1.0f);
        hsv.z = max(hsv.z * ColorGrade.Brightness, 0.0f);
        ldrSceneColor = HSVtoRGB(hsv);
    }

    ldrSceneColor = (ldrSceneColor - 0.5f) * ColorGrade.Contrast + 0.5f;

    // Gamma-correct
    ldrSceneColor = pow(ldrSceneColor, vec3(1.0 / ColorGrade.Gamma));
    // Fix subtle banding by applying fine noise
    ldrSceneColor += mix(-0.5 / 255.0, 0.5 / 255.0, rand(uv));

    OutColor = vec4(ldrSceneColor, 1.0);
}
