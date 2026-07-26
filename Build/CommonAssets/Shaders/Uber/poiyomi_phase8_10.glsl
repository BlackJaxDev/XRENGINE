#ifndef XRENGINE_POIYOMI_PHASE8_10_GLSL
#define XRENGINE_POIYOMI_PHASE8_10_GLSL

float poiHash12(vec2 value)
{
    vec3 p3 = fract(vec3(value.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

bool poiApplyPhase8Coverage(in ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_VIEW_CONTEXT
    if (_PoiViewVisibilityMask != 0 && (_PoiViewFlags & _PoiViewVisibilityMask) == 0)
        return true;
#endif
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SPECIAL_EFFECTS
    if (_PoiFaceDiscard == 1 && mesh.isFrontFace > 0.0)
        return true;
    if (_PoiFaceDiscard == 2 && mesh.isFrontFace < 0.0)
        return true;

    if (_PoiUvDiscard > 0.5)
    {
        vec2 grid = max(_PoiUvDiscardGrid, vec2(1.0));
        vec2 cell = floor(fract(mesh.uv[0]) * grid);
        float tile = (cell.y * grid.x + cell.x) / max(grid.x * grid.y - 1.0, 1.0);
        if (tile < min(_PoiUvDiscardRange.x, _PoiUvDiscardRange.y) ||
            tile > max(_PoiUvDiscardRange.x, _PoiUvDiscardRange.y))
            return true;
    }
#endif
    return false;
}

void poiApplyInternalParallax(inout ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SPECIAL_EFFECTS
    if (_PoiInternalParallax <= 0.0001)
        return;

    vec3 tangentView = transpose(mesh.TBN) * mesh.viewDir;
    float layers = max(_PoiInternalParallaxParams.y, 1.0);
    float depth = _PoiInternalParallaxParams.x * _PoiInternalParallax / layers;
    // This is intentionally distinct from height-map POM: it offsets an
    // interior layer stack and never changes the outer silhouette.
    mesh.uv[0] -= tangentView.xy / max(abs(tangentView.z), 0.08) *
        depth * (0.5 + 0.5 * fract(_PoiInternalParallaxParams.z));
#endif
}

float poiAudioBandValue(int band, float history)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_AUDIOLINK
    vec2 size = max(_AudioLinkTextureSize.xy, vec2(1.0));
    float x = (clamp(float(band), 0.0, 3.0) + 0.5) / size.x;
    float y = (clamp(history, 0.0, size.y - 1.0) + 0.5) / size.y;
    return texture(_AudioLinkTexture, vec2(x, y)).r;
#else
    return 0.0;
#endif
}

void poiApplyPhase8Surface(
    in ToonMesh mesh,
    inout vec3 color,
    inout vec3 emission,
    inout float alpha)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SPECIAL_EFFECTS
    if (_PoiPathing > 0.0001)
    {
        vec3 axis = normalize(max(abs(_PoiPathingParams.xyz), vec3(0.0001)) * sign(_PoiPathingParams.xyz));
        float coordinate = dot(mesh.localPos, axis) + _PoiPathingParams.w * u_Time;
        float band = smoothstep(0.5, 0.0, abs(fract(coordinate) - 0.5));
        float weight = band * _PoiPathing * _PoiPathingColor.a;
        color = mix(color, _PoiPathingColor.rgb, weight);
        emission += _PoiPathingColor.rgb * weight;
    }

    if (_PoiProximity > 0.0001)
    {
        float distanceToCamera = length(mesh.worldPos - u_CameraPosition);
        float proximity = 1.0 - smoothstep(
            min(_PoiProximityParams.x, _PoiProximityParams.y),
            max(_PoiProximityParams.x, _PoiProximityParams.y),
            distanceToCamera);
        float weight = proximity * _PoiProximity * _PoiProximityColor.a;
        color = mix(color, _PoiProximityColor.rgb, weight);
        emission += _PoiProximityColor.rgb * weight * _PoiProximityParams.z;
        alpha *= mix(1.0, proximity, saturate(_PoiProximityParams.w));
    }

    if (_PoiTouchGlow > 0.0001)
    {
        float radial = length(mesh.worldPos - _PoiTouchGlowParams.xyz);
        float touch = 1.0 - smoothstep(0.0, max(_PoiTouchGlowParams.w, 0.0001), radial);
        emission += _PoiTouchGlowColor.rgb * touch * _PoiTouchGlow * _PoiTouchGlowColor.a;
    }

    if (_PoiVideoBlend > 0.0001)
    {
        vec2 videoUv = mesh.uv[0] * _PoiVideoTexture_ST.xy + _PoiVideoTexture_ST.zw;
        vec4 video = texture(_PoiVideoTexture, videoUv);
        color = mix(color, video.rgb, saturate(_PoiVideoBlend * video.a));
    }
#endif

#ifndef XRENGINE_UBER_DISABLE_POIYOMI_AUDIOLINK
    float audio = poiAudioBandValue(_AudioLinkBand, _AudioLinkHistory.x) * _AudioLinkStrength;
    color = mix(color, color * _AudioLinkColor.rgb, saturate(audio * _AudioLinkColor.a));
    emission += _AudioLinkColor.rgb * audio;
#endif

#ifndef XRENGINE_UBER_DISABLE_POIYOMI_ENVIRONMENT_ADAPTERS
    color *= mix(vec3(1.0), _PoiEnvironmentLight.rgb, saturate(_PoiEnvironmentLight.a));
    emission += _PoiEnvironmentSpecular.rgb * _PoiEnvironmentSpecular.a;
    float blacklight = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    emission += _PoiBlacklight.rgb * blacklight * _PoiBlacklight.a;
#endif

#ifndef XRENGINE_UBER_DISABLE_POIYOMI_VIEW_CONTEXT
    color *= mix(vec3(1.0), _PoiViewTint.rgb, saturate(_PoiViewTint.a));
#endif
}

vec3 poiApplyPhase8Post(in ToonMesh mesh, vec3 color)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SPECIAL_EFFECTS
    int mode = _PoiProceduralMode;
    if (mode == 1)
    {
        float scanline = 0.82 + 0.18 * sin(gl_FragCoord.y * max(_PoiProceduralParams.x, 1.0));
        float grille = 0.9 + 0.1 * sin(gl_FragCoord.x * 2.094);
        color *= scanline * grille;
    }
    else if (mode == 2)
    {
        float levels = max(_PoiProceduralParams.x, 2.0);
        float luminance = dot(color, vec3(0.299, 0.587, 0.114));
        luminance = floor(luminance * levels + 0.5) / levels;
        color = mix(vec3(luminance), _PoiProceduralColor.rgb * luminance, _PoiProceduralColor.a);
    }
    else if (mode == 3)
    {
        vec2 p = mesh.uv[0] * max(_PoiProceduralParams.xy, vec2(1.0));
        vec2 cell = floor(p);
        vec2 f = fract(p);
        float distanceToFeature = 1.0;
        for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
        {
            vec2 neighbor = vec2(x, y);
            vec2 feature = vec2(
                poiHash12(cell + neighbor),
                poiHash12(cell + neighbor + 17.0));
            distanceToFeature = min(distanceToFeature, length(neighbor + feature - f));
        }
        color = mix(color, _PoiProceduralColor.rgb, saturate((1.0 - distanceToFeature) * _PoiProceduralColor.a));
    }
    else if (mode == 4)
    {
        vec2 tile = fract(mesh.uv[0] * max(_PoiProceduralParams.xy, vec2(1.0)));
        float flip = step(0.5, poiHash12(floor(mesh.uv[0] * max(_PoiProceduralParams.xy, vec2(1.0)))));
        float arc = flip < 0.5 ? abs(length(tile) - 0.5) : abs(length(tile - 1.0) - 0.5);
        color = mix(color, _PoiProceduralColor.rgb, smoothstep(0.08, 0.0, arc) * _PoiProceduralColor.a);
    }
#endif
    return color;
}

#endif
