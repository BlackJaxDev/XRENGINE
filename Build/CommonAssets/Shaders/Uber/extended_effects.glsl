#ifndef XRENGINE_EXTENDED_EFFECTS_GLSL
#define XRENGINE_EXTENDED_EFFECTS_GLSL

#if defined(XRENGINE_VULKAN) && !defined(XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS)
layout(std430, set = 0, binding = 40) readonly buffer SourceUdimTriangleCoverage
{
    uvec4 sourceUdimTriangleMasks[];
};
uniform int u_SourceUdimTriangleCount; // XRENGINE_FREQUENCY(Object)

uint uberEnabledUdimTileMask()
{
    uint enabled = 0u;
    vec4 rows[4] = vec4[4](
        vec4(_UDIMDiscardRow0_0, _UDIMDiscardRow0_1, _UDIMDiscardRow0_2, _UDIMDiscardRow0_3),
        vec4(_UDIMDiscardRow1_0, _UDIMDiscardRow1_1, _UDIMDiscardRow1_2, _UDIMDiscardRow1_3),
        vec4(_UDIMDiscardRow2_0, _UDIMDiscardRow2_1, _UDIMDiscardRow2_2, _UDIMDiscardRow2_3),
        vec4(_UDIMDiscardRow3_0, _UDIMDiscardRow3_1, _UDIMDiscardRow3_2, _UDIMDiscardRow3_3));
    for (int row = 0; row < 4; row++)
    for (int column = 0; column < 4; column++)
        if (rows[row][column] > 0.001)
            enabled |= 1u << uint(row * 4 + column);
    return enabled;
}
#endif

// Native implementations for extended material effects and runtime
// integrations. Importers own all source-format translation.

float uberHash12(vec2 value)
{
    vec3 p3 = fract(vec3(value.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

bool uberApplyCoverageEffects(in ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_VIEW_CONTEXT
    if (_ViewVisibilityMask != 0 && (_ViewFlags & _ViewVisibilityMask) == 0)
        return true;
#endif
#ifndef XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS
    if (_FaceDiscard == 1 && mesh.isFrontFace > 0.0)
        return true;
    if (_FaceDiscard == 2 && mesh.isFrontFace < 0.0)
        return true;

    if (_UvTileDiscard > 0.5 && _UDIMDiscardMode == 0)
    {
#if defined(XRENGINE_VULKAN) && !defined(XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS)
        uint enabled = uberEnabledUdimTileMask();
        if (enabled != 0u)
        {
            if (u_SourceUdimTriangleCount <= 0 ||
                gl_PrimitiveID < 0 || gl_PrimitiveID >= u_SourceUdimTriangleCount)
                return true;
            uint membership = sourceUdimTriangleMasks[gl_PrimitiveID][clamp(_UDIMDiscardUV, 0, 3)];
            if ((membership & enabled) != 0u)
                return true;
        }
#endif
    }

    if (_UvTileDiscard > 0.5 && _UDIMDiscardMode == 1)
    {
        vec2 tile = floor(mesh.uv[clamp(_UDIMDiscardUV, 0, 3)]);
        if (all(greaterThanEqual(tile, vec2(0.0))) &&
            all(lessThan(tile, vec2(4.0))))
        {
            vec4 row = tile.y < 1.0
                ? vec4(_UDIMDiscardRow0_0, _UDIMDiscardRow0_1,
                    _UDIMDiscardRow0_2, _UDIMDiscardRow0_3)
                : tile.y < 2.0
                    ? vec4(_UDIMDiscardRow1_0, _UDIMDiscardRow1_1,
                        _UDIMDiscardRow1_2, _UDIMDiscardRow1_3)
                    : tile.y < 3.0
                        ? vec4(_UDIMDiscardRow2_0, _UDIMDiscardRow2_1,
                            _UDIMDiscardRow2_2, _UDIMDiscardRow2_3)
                        : vec4(_UDIMDiscardRow3_0, _UDIMDiscardRow3_1,
                            _UDIMDiscardRow3_2, _UDIMDiscardRow3_3);
            if (row[int(tile.x)] > 0.001)
                return true;
        }
    }
#endif
    return false;
}

void uberApplyInternalParallax(inout ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS
    if (_InternalParallaxStrength <= 0.0001)
        return;

    vec3 tangentView = transpose(mesh.TBN) * mesh.viewDir;
    float layers = max(_InternalParallaxParams.y, 1.0);
    float depth = _InternalParallaxParams.x * _InternalParallaxStrength / layers;
    // This is intentionally distinct from height-map POM: it offsets an
    // interior layer stack and never changes the outer silhouette.
    mesh.uv[0] -= tangentView.xy / max(abs(tangentView.z), 0.08) *
        depth * (0.5 + 0.5 * fract(_InternalParallaxParams.z));
#endif
}

float uberAudioBandValue(int band, float history)
{
#ifndef XRENGINE_UBER_DISABLE_AUDIOLINK
    vec2 size = max(_AudioLinkTextureSize.xy, vec2(1.0));
    float x = (clamp(float(band), 0.0, 3.0) + 0.5) / size.x;
    float y = (clamp(history, 0.0, size.y - 1.0) + 0.5) / size.y;
    return texture(_AudioLinkTexture, vec2(x, y)).r;
#else
    return 0.0;
#endif
}

void uberApplySurfaceEffects(
    in ToonMesh mesh,
    inout vec3 color,
    inout vec3 emission,
    inout float alpha)
{
#ifndef XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS
    if (_PathingStrength > 0.0001)
    {
        vec3 axis = normalize(max(abs(_PathingParams.xyz), vec3(0.0001)) * sign(_PathingParams.xyz));
        float coordinate = dot(mesh.localPos, axis) + _PathingParams.w * u_Time;
        float band = smoothstep(0.5, 0.0, abs(fract(coordinate) - 0.5));
        float weight = band * _PathingStrength * _PathingColor.a;
        color = mix(color, _PathingColor.rgb, weight);
        emission += _PathingColor.rgb * weight;
    }

    if (_ProximityStrength > 0.0001)
    {
        float distanceToCamera = length(mesh.worldPos - u_CameraPosition);
        float proximity = 1.0 - smoothstep(
            min(_ProximityParams.x, _ProximityParams.y),
            max(_ProximityParams.x, _ProximityParams.y),
            distanceToCamera);
        float weight = proximity * _ProximityStrength * _ProximityColor.a;
        color = mix(color, _ProximityColor.rgb, weight);
        emission += _ProximityColor.rgb * weight * _ProximityParams.z;
        alpha *= mix(1.0, proximity, saturate(_ProximityParams.w));
    }

    if (_TouchGlowStrength > 0.0001)
    {
        float radial = length(mesh.worldPos - _TouchGlowParams.xyz);
        float touch = 1.0 - smoothstep(0.0, max(_TouchGlowParams.w, 0.0001), radial);
        emission += _TouchGlowColor.rgb * touch * _TouchGlowStrength * _TouchGlowColor.a;
    }

    if (_VideoBlend > 0.0001)
    {
        vec2 videoUv = mesh.uv[0] * _VideoTexture_ST.xy + _VideoTexture_ST.zw;
        vec4 video = texture(_VideoTexture, videoUv);
        color = mix(color, video.rgb, saturate(_VideoBlend * video.a));
    }
#endif

#ifndef XRENGINE_UBER_DISABLE_AUDIOLINK
    float audio = uberAudioBandValue(_AudioLinkBand, _AudioLinkHistory.x) * _AudioLinkStrength;
    color = mix(color, color * _AudioLinkColor.rgb, saturate(audio * _AudioLinkColor.a));
    emission += _AudioLinkColor.rgb * audio;
#endif

#ifndef XRENGINE_UBER_DISABLE_ENVIRONMENT_LIGHTING
    color *= mix(vec3(1.0), _EnvironmentDiffuse.rgb, saturate(_EnvironmentDiffuse.a));
    emission += _EnvironmentSpecular.rgb * _EnvironmentSpecular.a;
    float blacklight = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    emission += _EnvironmentBlacklight.rgb * blacklight * _EnvironmentBlacklight.a;
#endif

#ifndef XRENGINE_UBER_DISABLE_VIEW_CONTEXT
    color *= mix(vec3(1.0), _ViewTint.rgb, saturate(_ViewTint.a));
#endif
}

vec3 uberApplyPostEffects(in ToonMesh mesh, vec3 color)
{
#ifndef XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS
    int mode = _ProceduralMode;
    if (mode == 1)
    {
        float scanline = 0.82 + 0.18 * sin(gl_FragCoord.y * max(_ProceduralParams.x, 1.0));
        float grille = 0.9 + 0.1 * sin(gl_FragCoord.x * 2.094);
        color *= scanline * grille;
    }
    else if (mode == 2)
    {
        float levels = max(_ProceduralParams.x, 2.0);
        float luminance = dot(color, vec3(0.299, 0.587, 0.114));
        luminance = floor(luminance * levels + 0.5) / levels;
        color = mix(vec3(luminance), _ProceduralColor.rgb * luminance, _ProceduralColor.a);
    }
    else if (mode == 3)
    {
        vec2 p = mesh.uv[0] * max(_ProceduralParams.xy, vec2(1.0));
        vec2 cell = floor(p);
        vec2 f = fract(p);
        float distanceToFeature = 1.0;
        for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
        {
            vec2 neighbor = vec2(x, y);
            vec2 feature = vec2(
                uberHash12(cell + neighbor),
                uberHash12(cell + neighbor + 17.0));
            distanceToFeature = min(distanceToFeature, length(neighbor + feature - f));
        }
        color = mix(color, _ProceduralColor.rgb, saturate((1.0 - distanceToFeature) * _ProceduralColor.a));
    }
    else if (mode == 4)
    {
        vec2 tile = fract(mesh.uv[0] * max(_ProceduralParams.xy, vec2(1.0)));
        float flip = step(0.5, uberHash12(floor(mesh.uv[0] * max(_ProceduralParams.xy, vec2(1.0)))));
        float arc = flip < 0.5 ? abs(length(tile) - 0.5) : abs(length(tile - 1.0) - 0.5);
        color = mix(color, _ProceduralColor.rgb, smoothstep(0.08, 0.0, arc) * _ProceduralColor.a);
    }
#endif
    return color;
}

#endif
