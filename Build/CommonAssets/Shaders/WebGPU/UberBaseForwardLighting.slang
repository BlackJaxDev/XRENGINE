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

float3 uberBaseForwardLighting(UberBaseMesh mesh,float3 albedo,float3 rms,float2 pixel,uint pipelineFlags)
{
    if((pipelineFlags&1u)==0u)return albedo;
    bool shadows=(pipelineFlags&2u)!=0u;
    float3 normal=mesh.worldNormal,viewDirection=mesh.viewDirection,color=float3(0.0);
    [unroll]
    for(int index=0;index<4;index++)
    {
        if(lighting.lightCounts.x<=index)continue;
        float4 direction=index==0?lighting.directional0Direction:index==1?lighting.directional1Direction:index==2?lighting.directional2Direction:lighting.directional3Direction;
        float4 intensity=index==0?lighting.directional0ColorIntensity:index==1?lighting.directional1ColorIntensity:index==2?lighting.directional2ColorIntensity:lighting.directional3ColorIntensity;
        float visibility=shadows&&index==0?selectedDirectionalVisibility(0,mesh.worldPosition,mesh.vertexNormal,pixel):1.0;
        color+=directionalLight(direction,intensity,normal,viewDirection,albedo,rms,visibility);
    }
#define UBER_POINT(N) if(lighting.lightCounts.y>N) color+=(shadows?selectedPointVisibility(N,mesh.worldPosition,mesh.vertexNormal,pixel):1.0)*pointLight(lighting.point##N##PositionRadius,lighting.point##N##ColorIntensity,lighting.point##N##Brightness,mesh.worldPosition,normal,viewDirection,albedo,rms);
#define UBER_SPOT(N) if(lighting.lightCounts.z>N) color+=(shadows?selectedSpotVisibility(N,mesh.worldPosition,mesh.vertexNormal,pixel):1.0)*spotLight(lighting.spot##N##PositionRadius,lighting.spot##N##ColorIntensity,lighting.spot##N##DirectionExponent,lighting.spot##N##CutoffsBrightness,mesh.worldPosition,normal,viewDirection,albedo,rms);
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
    }
    return color+XRENGINE_CalculateAmbientPbr(normal,mesh.worldPosition,albedo,viewDirection,rms,ao);
}
