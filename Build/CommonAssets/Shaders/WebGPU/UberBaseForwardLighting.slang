// Shared source lighting. Call only after canonical material coverage, and
// before a native visibility-identity gate can diverge the fragment quad.
uint uberBasePipelineFlags()
{
    uint flags=15u;
#ifdef XRE_UBER_BASE_DISABLE_LIGHTING
    flags&=~1u;
#endif
#ifdef XRE_UBER_BASE_DISABLE_SHADOWS
    flags&=~2u;
#endif
#ifdef XRE_UBER_BASE_DISABLE_AMBIENT_OCCLUSION
    flags&=~4u;
#endif
#ifdef XRE_UBER_BASE_DISABLE_PBR_RESOURCES
    flags&=~8u;
#endif
    return flags;
}

// Source Mochie BRDF from Uber/source_shading.glsl. The admitted base variant
// disables material AO, so callers pass a material AO of one.
float uberBaseSourceLuminance(float3 color)
{
    return dot(color, float3(0.299, 0.587, 0.114));
}

// Scales an additional source light. A limited light keeps its luminance at or
// below the limit, then the monochrome control blends toward that luminance.
float3 uberBaseSourceAdditiveColor(float3 raw, float attenuation, float limited, float limit, float monochromatic)
{
    float3 color = raw * attenuation;
    if (limited > 0.5)
        color *= min(1.0, limit / max(uberBaseSourceLuminance(color), 0.00001));
    float luma = uberBaseSourceLuminance(color);
    return lerp(color, float3(luma), saturate(monochromatic));
}

// Converts smoothness to perceptual roughness. With geometric specular
// antialiasing on, the normal derivatives widen the roughness.
float uberBaseSourcePerceptualRoughness(float smoothness, float3 normalDx, float3 normalDy, float4 settings)
{
    float perceptual = 1.0 - smoothness;
    if (settings.x > 0.5)
    {
        float variance = settings.y * (dot(normalDx, normalDx) + dot(normalDy, normalDy));
        perceptual = pow(saturate(pow(perceptual, 4.0) + min(2.0 * variance, settings.z)), 0.25);
    }
    return perceptual;
}

float uberBaseSourceBrdfRoughness(float smoothness, float3 normalDx, float3 normalDy, float4 settings)
{
    float perceptual = uberBaseSourcePerceptualRoughness(smoothness, normalDx, normalDy, settings);
    return max(perceptual * perceptual, 0.002);
}

// Returns one GGX lobe for one light. A directional light also limits the
// shadow visibility by N dot L.
float3 uberBaseSourceGgxLobe(float3 normal, float3 view, float3 light, float3 f0, float smoothness,
    float mask, float3 tint, float3 radiance, float materialAo, float shadowVisibility,
    float3 normalDx, float3 normalDy, float4 settings, float ignoreShadows, bool directional)
{
    float nl = dot(normal, light);
    if (nl <= 0.0 || mask <= 0.0)
        return float3(0.0);
    float3 halfVector = normalize(light + view);
    float nv = abs(dot(normal, view));
    float nh = dot(normal, halfVector);
    float lh = max(dot(light, halfVector), 0.00001);
    float alpha = uberBaseSourceBrdfRoughness(smoothness, normalDx, normalDy, settings);
    float visibility = 0.5 / (nl * (nv * (1.0 - alpha) + alpha) + nv * (nl * (1.0 - alpha) + alpha) + 0.00001);
    float nhAlpha = nh * alpha;
    float k = alpha / (1.0 - nh * nh + nhAlpha * nhAlpha + 0.00001);
    float distribution = k * k * 0.3183098861837907;
    float term = max(0.0, visibility * distribution * max(0.00001, nl));
    float3 fresnel = f0 + (float3(1.0) - f0) * pow(1.0 - lh, 5.0);
    float castVisibility = lerp(shadowVisibility, 1.0, saturate(ignoreShadows));
    if (directional)
        castVisibility = min(saturate(nl), castVisibility);
    return radiance * term * fresnel * mask * tint * materialAo * castVisibility;
}

// Returns the main Mochie lobe, or the brighter of the main and the second
// lobe when the second lobe is on.
float3 uberBaseSourceMochieDirect(float3 normal, float3 view, float3 light, float3 baseColor,
    float metallic, float smoothness, float packedSmoothness, float mask, float3 tint, float3 radiance,
    float shadowVisibility, float3 normalDx, float3 normalDy, float4 settings, float4 second, bool directional)
{
    float3 f0 = lerp(float3(0.04), baseColor, metallic);
    float3 mainLobe = uberBaseSourceGgxLobe(normal, view, light, f0, smoothness, mask, tint, radiance, 1.0,
        shadowVisibility, normalDx, normalDy, settings, second.z, directional);
    if (settings.w <= 0.5 || second.y <= 0.0)
        return mainLobe;
    float3 extraLobe = uberBaseSourceGgxLobe(normal, view, light, f0, packedSmoothness * second.x, mask * second.y,
        tint, radiance, 1.0, shadowVisibility, normalDx, normalDy, settings, second.z, directional);
    return max(mainLobe, extraLobe);
}

// Returns the Mochie probe reflection. It replaces the standard probe specular
// term and uses the primary light color for its horizon visibility.
float3 uberBaseSourceMochieReflection(float3 rawRadiance, float3 directColor, float3 f0,
    float smoothness, float reflectionMask, float nv, float3 normalDx, float3 normalDy,
    float4 settings, float4 reflectionSettings, float3 tint)
{
    float alpha = uberBaseSourceBrdfRoughness(smoothness, normalDx, normalDy, settings);
    float fresnelStrength = reflectionSettings.x;
    float3 grazing = f0 + lerp(f0, float3(1.0), fresnelStrength) * fresnelStrength;
    float3 fresnel = lerp(f0, grazing, pow(1.0 - nv, 5.0));
    float visibility = saturate(length(directColor + float3(0.000001)) / max(reflectionSettings.y, 0.000001));
    float sf = saturate(pow(nv + visibility, exp2(-16.0 * alpha - 1.0)) - 1.0 + visibility);
    return rawRadiance * (1.0 / (alpha * alpha + 1.0)) * fresnel * sf * reflectionMask * tint;
}

float3 uberBaseForwardLighting(UberBaseMaterialParameters material,uint features,UberBaseMesh mesh,float3 albedo,
    UberBaseSpecular specular,float2 pixel,uint pipelineFlags)
{
    if((pipelineFlags&1u)==0u)return albedo;
    bool shadows=(pipelineFlags&2u)!=0u;
    bool advancedSpecular=(features&UberBaseAdvancedSpecular)!=0u;
    // The source Mochie lobes replace the standard direct and probe specular terms.
    bool mochie=advancedSpecular&&material._SourceMochieSecond.w>0.5;
    float3 normal=mesh.worldNormal,viewDirection=mesh.viewDirection,color=float3(0.0);
    // Take the normal derivatives here, in uniform control flow, for the Mochie lobes.
    float3 normalDx=ddx(normal),normalDy=ddy(normal);
    float3 rms=specular.rms;
    if(mochie)rms.z=0.0;
    float primaryVisibility=1.0;
    [unroll]
    for(int index=0;index<4;index++)
    {
        if(lighting.lightCounts.x<=index)continue;
        float4 direction=index==0?lighting.directional0Direction:index==1?lighting.directional1Direction:index==2?lighting.directional2Direction:lighting.directional3Direction;
        float4 intensity=index==0?lighting.directional0ColorIntensity:index==1?lighting.directional1ColorIntensity:index==2?lighting.directional2ColorIntensity:lighting.directional3ColorIntensity;
        float visibility=shadows&&index==0?selectedDirectionalVisibility(0,mesh.worldPosition,mesh.vertexNormal,pixel):1.0;
        if(index==0)primaryVisibility=visibility;
        color+=directionalLight(direction,intensity,normal,viewDirection,albedo,rms,visibility);
    }
    // Keep each local shadow visibility, so the Mochie lobes reuse one sample per light.
    float pointVisibility[8]={1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0};
    float spotVisibility[8]={1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0};
#define UBER_POINT(N) if(lighting.lightCounts.y>N){ pointVisibility[N]=shadows?selectedPointVisibility(N,mesh.worldPosition,mesh.vertexNormal,pixel):1.0; color+=pointVisibility[N]*pointLight(lighting.point##N##PositionRadius,lighting.point##N##ColorIntensity,lighting.point##N##Brightness,mesh.worldPosition,normal,viewDirection,albedo,rms); }
#define UBER_SPOT(N) if(lighting.lightCounts.z>N){ spotVisibility[N]=shadows?selectedSpotVisibility(N,mesh.worldPosition,mesh.vertexNormal,pixel):1.0; color+=spotVisibility[N]*spotLight(lighting.spot##N##PositionRadius,lighting.spot##N##ColorIntensity,lighting.spot##N##DirectionExponent,lighting.spot##N##CutoffsBrightness,mesh.worldPosition,normal,viewDirection,albedo,rms); }
    UBER_POINT(0) UBER_POINT(1) UBER_POINT(2) UBER_POINT(3)
    UBER_POINT(4) UBER_POINT(5) UBER_POINT(6) UBER_POINT(7)
    UBER_SPOT(0) UBER_SPOT(1) UBER_SPOT(2) UBER_SPOT(3)
    UBER_SPOT(4) UBER_SPOT(5) UBER_SPOT(6) UBER_SPOT(7)
#undef UBER_POINT
#undef UBER_SPOT
    float ao=1.0;
    if((pipelineFlags&4u)!=0u&&lighting.ambientOcclusionControls.x>0.5)
    {
        uint width,height;ambientOcclusionTexture.GetDimensions(width,height);
        float2 uv=clamp((pixel-lighting.ambientOcclusionViewport.xy)/max(lighting.ambientOcclusionViewport.zw,float2(1.0)),float2(0.0),float2(0.999999));
        int2 texel=int2(floor(uv*float2(width,height)));
        ao=pow(saturate(ambientOcclusionTexture.Load(int3(texel,0)).r),max(lighting.ambientOcclusionControls.y,0.001));
        if(lighting.ambientOcclusionControls.w>0.0) ao=pow(saturate(ao),lighting.ambientOcclusionControls.w);
        // The material strength blends the screen-space AO toward no occlusion.
        ao=lerp(1.0,ao,saturate(material._WorldAmbientOcclusionStrength));
    }
    // Packed masks put the specular strength in the direct term only. The probe
    // term uses the reflection mask instead.
    float3 ambientRms=specular.rms;
    if(advancedSpecular&&material._PBRMochiePackedMasksEnabled!=0)ambientRms.z=1.0;
    float3 probeDiffuse,probeSpecular;
    float3 ambient=XRENGINE_CalculateAmbientPbrWithProbe(normal,mesh.worldPosition,albedo,viewDirection,
        ambientRms,ao,specular.reflectionMask,probeDiffuse,probeSpecular);
    if(!mochie)return color+ambient;

    float metallic=specular.rms.y;
    float sourceSmoothness=specular.packedSmoothness*material._PBRRoughnessMultiplier;
    float4 settings=material._SourceMochieSettings,second=material._SourceMochieSecond;
    float3 primaryColor=lighting.lightCounts.x>0?lighting.directional0ColorIntensity.xyz*lighting.directional0ColorIntensity.w:float3(0.0);
    float nv=abs(dot(normal,viewDirection));
    float perceptual=uberBaseSourcePerceptualRoughness(sourceSmoothness,normalDx,normalDy,settings);
    float3 rawReflection=XRENGINE_SampleForwardRawPrefilter(normal,mesh.worldPosition,viewDirection,perceptual);
    float3 sourceReflection=uberBaseSourceMochieReflection(rawReflection,primaryColor,lerp(float3(0.04),albedo,metallic),
        sourceSmoothness,specular.reflectionMask,nv,normalDx,normalDy,settings,material._SourceMochieReflection,
        material._SourceMochieReflectionTint.rgb)*XRENGINE_ForwardSpecularOcclusion(nv,ao,perceptual);
    ambient+=sourceReflection-probeSpecular;

    // Source light mode lights the primary directional with the source color
    // rules and can skip the additional lights.
    bool sourceLights=material._SourceLightMode!=0;
    float4 additive=material._SourceAdditiveSettings;
    float additiveMonochrome=material._SourceAdditiveColor.x;
    float3 tint=material._SpecularTint.rgb;
    float sourcePrimaryVisibility=sourceLights&&uberBaseSourceLuminance(primaryColor)<=0.000001?1.0:primaryVisibility;
    float3 direct=float3(0.0);
    if(sourceLights&&lighting.lightCounts.x>0)
        direct=uberBaseSourceMochieDirect(normal,viewDirection,safeNormalize(-lighting.directional0Direction.xyz),albedo,metallic,
            sourceSmoothness,specular.packedSmoothness,specular.specularMask,tint,primaryColor,sourcePrimaryVisibility,
            normalDx,normalDy,settings,second,true);
    if(!sourceLights||additive.x>0.5)
    {
        [unroll]
        for(int index=0;index<4;index++)
        {
            if(lighting.lightCounts.x<=index||(sourceLights&&(index==0||additive.y>0.5)))continue;
            float4 direction=index==0?lighting.directional0Direction:index==1?lighting.directional1Direction:index==2?lighting.directional2Direction:lighting.directional3Direction;
            float4 intensity=index==0?lighting.directional0ColorIntensity:index==1?lighting.directional1ColorIntensity:index==2?lighting.directional2ColorIntensity:lighting.directional3ColorIntensity;
            float3 raw=intensity.xyz*intensity.w;
            float3 radiance=index==0?raw:sourceLights?uberBaseSourceAdditiveColor(raw,1.0,additive.z,additive.w,additiveMonochrome):max(raw,float3(0.0));
            direct+=uberBaseSourceMochieDirect(normal,viewDirection,safeNormalize(-direction.xyz),albedo,metallic,
                sourceSmoothness,specular.packedSmoothness,specular.specularMask,tint,radiance,index==0?sourcePrimaryVisibility:1.0,
                normalDx,normalDy,settings,second,true);
        }
#define MOCHIE_POINT(N) if(lighting.lightCounts.y>N){ float3 delta=lighting.point##N##PositionRadius.xyz-mesh.worldPosition; float lightDistance=length(delta); if(lightDistance>0.000001){ float attenuation=lightAttenuation(lightDistance,lighting.point##N##PositionRadius.w)*lighting.point##N##Brightness.x; float3 raw=lighting.point##N##ColorIntensity.xyz*lighting.point##N##ColorIntensity.w; float3 radiance=sourceLights?uberBaseSourceAdditiveColor(raw,attenuation,additive.z,additive.w,additiveMonochrome):max(raw,float3(0.0))*attenuation; direct+=uberBaseSourceMochieDirect(normal,viewDirection,delta/lightDistance,albedo,metallic,sourceSmoothness,specular.packedSmoothness,specular.specularMask,tint,radiance,pointVisibility[N],normalDx,normalDy,settings,second,false); } }
#define MOCHIE_SPOT(N) if(lighting.lightCounts.z>N){ float3 delta=lighting.spot##N##PositionRadius.xyz-mesh.worldPosition; float lightDistance=length(delta); if(lightDistance>0.000001){ float3 lightDirection=delta/lightDistance; float4 cutoffs=lighting.spot##N##CutoffsBrightness; float cosine=max(0.0,dot(-lightDirection,safeNormalize(lighting.spot##N##DirectionExponent.xyz))); float cone=cutoffs.x==cutoffs.y?(cosine>=cutoffs.x?1.0:0.0):smoothstep(cutoffs.y,cutoffs.x,cosine); float attenuation=lightAttenuation(lightDistance,lighting.spot##N##PositionRadius.w)*cutoffs.z*cone*pow(cosine,lighting.spot##N##DirectionExponent.w); float3 raw=lighting.spot##N##ColorIntensity.xyz*lighting.spot##N##ColorIntensity.w; float3 radiance=sourceLights?uberBaseSourceAdditiveColor(raw,attenuation,additive.z,additive.w,additiveMonochrome):max(raw,float3(0.0))*attenuation; direct+=uberBaseSourceMochieDirect(normal,viewDirection,lightDirection,albedo,metallic,sourceSmoothness,specular.packedSmoothness,specular.specularMask,tint,radiance,spotVisibility[N],normalDx,normalDy,settings,second,false); } }
        MOCHIE_POINT(0) MOCHIE_POINT(1) MOCHIE_POINT(2) MOCHIE_POINT(3)
        MOCHIE_POINT(4) MOCHIE_POINT(5) MOCHIE_POINT(6) MOCHIE_POINT(7)
        MOCHIE_SPOT(0) MOCHIE_SPOT(1) MOCHIE_SPOT(2) MOCHIE_SPOT(3)
        MOCHIE_SPOT(4) MOCHIE_SPOT(5) MOCHIE_SPOT(6) MOCHIE_SPOT(7)
#undef MOCHIE_POINT
#undef MOCHIE_SPOT
    }
    return (color+ambient)+direct;
}
