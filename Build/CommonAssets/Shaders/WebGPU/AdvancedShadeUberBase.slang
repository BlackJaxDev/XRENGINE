// Independently versioned browser Uber material and full-float source attributes.
#if !defined(XR_ADV_UBER_BASE_SCHEMA_VERSION) || XR_ADV_UBER_BASE_SCHEMA_VERSION != 1
#error Native Uber requires its schema1 program contract.
#endif
#if !defined(XR_ADV_SCENE_DIRECTORY_TABLE_COUNT) || XR_ADV_SCENE_DIRECTORY_TABLE_COUNT != 36
#error Native Uber requires the exact 36-table scene directory.
#endif
#include "UberBaseSurface.slang"
#define XR_ADV_UBER_SOURCE 10u

bool XR_ADV_IsUberBaseMaterial(XRAdvancedMaterialRecord material) { return material.reserved == XR_ADV_UBER_SOURCE; }
float4 XR_ADV_UberVector(uint word) { return asfloat(uint4(SceneArena[word], SceneArena[word+1u], SceneArena[word+2u], SceneArena[word+3u])); }
bool XR_ADV_TryLoadUberBase(XRAdvancedMaterialRecord material, out uint word, out uint features, out uint pipeline)
{
    word=0u; features=0u; pipeline=0u;
    uint count, stride; SceneArena.GetDimensions(count,stride);
    const uint header=34u*8u;
    if (!XR_ADV_IsUberBaseMaterial(material) || count < 36u*8u || SceneArena[header+2u]!=156u ||
        SceneArena[header+5u]!=1u || SceneArena[header+6u]!=36u || material.stableRowId==0u ||
        material.stableRowId>=SceneArena[header+1u]) return false;
    uint offset=SceneArena[header];
    if (offset < 36u*8u || offset>count || material.stableRowId>=(count-offset)/156u) return false;
    word=offset+material.stableRowId*156u;
    if (SceneArena[word]!=material.generation || SceneArena[word+1u]!=1u || SceneArena[word+155u]!=0u) return false;
    features=SceneArena[word+2u]; pipeline=SceneArena[word+3u];
    return (features & ~31u)==0u && (pipeline & ~15u)==0u;
}
UberBaseMaterialParameters XR_ADV_UberParameters(uint word)
{
    UberBaseMaterialParameters material;
    uint p=word+4u;
    material._Color=XR_ADV_UberVector(p+0u);
    material._MainTex_ST=XR_ADV_UberVector(p+4u);
    material._MainTexPan=asfloat(uint2(SceneArena[p+8u],SceneArena[p+9u]));
    material._MainTexUV=asint(SceneArena[p+10u]);
    material._MainVertexColoringEnabled=asfloat(SceneArena[p+11u]);
    material._MainVertexColoringLinearSpace=asfloat(SceneArena[p+12u]);
    material._MainVertexColoring=asfloat(SceneArena[p+13u]);
    material._MainUseVertexColorAlpha=asfloat(SceneArena[p+14u]);
    material._AlphaMod=asfloat(SceneArena[p+15u]);
    material._AlphaMask_ST=XR_ADV_UberVector(p+16u);
    material._AlphaMaskPan=asfloat(uint2(SceneArena[p+20u],SceneArena[p+21u]));
    material._AlphaMaskUV=asint(SceneArena[p+22u]);
    material._MainAlphaMaskMode=asint(SceneArena[p+23u]);
    material._AlphaMaskBlendStrength=asfloat(SceneArena[p+24u]);
    material._AlphaMaskValue=asfloat(SceneArena[p+25u]);
    material._AlphaMaskInvert=asfloat(SceneArena[p+26u]);
    material._Cutoff=asfloat(SceneArena[p+27u]);
    material._Mode=asint(SceneArena[p+28u]);
    material._AlphaForceOpaque=asfloat(SceneArena[p+29u]);
    material._SpecularSmoothness=asfloat(SceneArena[p+30u]);
    material._SpecularStrength=asfloat(SceneArena[p+31u]);
    material._BumpMap_ST=XR_ADV_UberVector(p+32u);
    material._BumpMapPan=asfloat(uint2(SceneArena[p+36u],SceneArena[p+37u]));
    material._BumpMapUV=asint(SceneArena[p+38u]);
    material._BumpScale=asfloat(SceneArena[p+39u]);
    material.NormalMapMode=asint(SceneArena[p+40u]);
    material.HeightMapScale=asfloat(SceneArena[p+41u]);
    material._PBRMetallicMapInvert=asfloat(SceneArena[p+42u]);
    material._PBRMetallicMultiplier=asfloat(SceneArena[p+43u]);
    material._PBRMetallicMaps_ST=XR_ADV_UberVector(p+44u);
    material._PBRMetallicMapsPan=asfloat(uint2(SceneArena[p+48u],SceneArena[p+49u]));
    material._PBRMetallicMapsUV=asint(SceneArena[p+50u]);
    material._PBRMetallicMapsMetallicChannel=asint(SceneArena[p+51u]);
    material._PBRSmoothnessMaps_ST=XR_ADV_UberVector(p+52u);
    material._PBRSmoothnessMapsPan=asfloat(uint2(SceneArena[p+56u],SceneArena[p+57u]));
    material._PBRSmoothnessMapsUV=asint(SceneArena[p+58u]);
    material._PBRSmoothnessMapsChannel=asint(SceneArena[p+59u]);
    material._PBRSmoothnessMapInvert=asfloat(SceneArena[p+60u]);
    material._PBRRoughnessMultiplier=asfloat(SceneArena[p+61u]);
    material._SpecularType=asint(SceneArena[p+62u]);
    material._StylizedSpecular=asfloat(SceneArena[p+63u]);
    material._SpecularMap_ST=XR_ADV_UberVector(p+64u);
    material._SpecularTint=XR_ADV_UberVector(p+68u);
    material._EmissionMap_ST=XR_ADV_UberVector(p+72u);
    material._EmissionMapPan=asfloat(uint2(SceneArena[p+76u],SceneArena[p+77u]));
    material._EmissionMapUV=asint(SceneArena[p+78u]);
    material._EmissionStrength=asfloat(SceneArena[p+79u]);
    material._EmissionColor=XR_ADV_UberVector(p+80u);
    material._EmissionScrollingSpeed=asfloat(uint2(SceneArena[p+84u],SceneArena[p+85u]));
    material._EmissionScrollingEnabled=asfloat(SceneArena[p+86u]);
    material._EmissionScrollingVertexColor=asfloat(SceneArena[p+87u]);
    return material;
}
struct XRAdvancedUberAttributes { float2 uv0; float2 uv1; float2 uv2; float2 uv3; float4 color; };
XRAdvancedUberAttributes XR_ADV_ReadUberAttributes(uint word)
{
    XRAdvancedUberAttributes a;
    float4 first=XR_ADV_UberVector(word), second=XR_ADV_UberVector(word+4u);
    a.uv0=first.xy; a.uv1=first.zw; a.uv2=second.xy; a.uv3=second.zw; a.color=XR_ADV_UberVector(word+8u);
    return a;
}
bool XR_ADV_TryLoadUberAttributes(uint drawDense,uint drawIdentity,XRAdvancedDrawRecord draw,uint geometryDense,
    uint3 sourceVertices,out XRAdvancedUberAttributes a,out XRAdvancedUberAttributes b,out XRAdvancedUberAttributes c)
{
    a=(XRAdvancedUberAttributes)0; b=a; c=a;
    uint words,stride; SceneArena.GetDimensions(words,stride); const uint header=35u*8u;
    if(words<36u*8u || SceneArena[header+2u]!=12u || SceneArena[header+5u]!=1u || SceneArena[header+6u]!=36u) return false;
    uint start=SceneArena[header],count=SceneArena[header+1u];
    if(drawDense>=count || start<36u*8u || start>words || drawDense>=(words-start)/12u) return false;
    uint row=start+drawDense*12u;
    XRAdvancedGpuHandle sourceDraw=XRAdvancedGpuHandle(SceneArena[row],SceneArena[row+1u]);
    XRAdvancedGpuHandle sourceGeometry=XRAdvancedGpuHandle(SceneArena[row+2u],SceneArena[row+3u]);
    if(sourceDraw.index!=drawIdentity || sourceGeometry.index!=draw.geometry.index || sourceGeometry.generation!=draw.geometry.generation ||
        XR_ADV_ResolveVisibilityHandle(sourceDraw,VisibilityDrawLookupSegment,XR_ADV_DIAGNOSTIC_DRAW)!=drawDense ||
        XR_ADV_ResolveVisibilityHandle(sourceGeometry,VisibilityGeometryLookupSegment,XR_ADV_DIAGNOSTIC_MESH)!=geometryDense) return false;
    uint offset=SceneArena[row+6u], vertices=SceneArena[row+7u];
    if(offset>words || vertices>(words-offset)/12u || any(sourceVertices>=uint3(vertices))) return false;
    a=XR_ADV_ReadUberAttributes(offset+sourceVertices.x*12u);
    b=XR_ADV_ReadUberAttributes(offset+sourceVertices.y*12u);
    c=XR_ADV_ReadUberAttributes(offset+sourceVertices.z*12u);
    return true;
}
UberBaseMesh XR_ADV_UberMesh(XRAdvancedSurface surface)
{
    UberBaseMesh mesh;
    mesh.worldPosition=surface.interpolatedWorldPosition; mesh.localPosition=surface.uberLocalPosition;
    mesh.vertexNormal=normalize(surface.shadingNormal)*(((surface.visibilityMetadata>>5u)&1u)!=0u?1.0:-1.0);
    mesh.worldNormal=mesh.vertexNormal; mesh.viewDirection=normalize(FrozenView.cameraPositionAndNear.xyz-mesh.worldPosition);
    mesh.tangent=surface.tangent; mesh.bitangent=surface.bitangent; mesh.vertexColor=surface.color0;
    mesh.uv0=surface.texCoord0; mesh.uv1=surface.texCoord1; mesh.uv2=surface.uberTexCoord2; mesh.uv3=surface.uberTexCoord3;
    return mesh;
}
float2 XR_ADV_UberGradient(XRAdvancedSurface surface,int mode,bool horizontal)
{
    switch(mode)
    {
        case 1: return horizontal?surface.texCoord1Dx:surface.texCoord1Dy;
        case 2: return horizontal?surface.uberTexCoord2Dx:surface.uberTexCoord2Dy;
        case 3: return horizontal?surface.uberTexCoord3Dx:surface.uberTexCoord3Dy;
        case 5: return horizontal?surface.worldPositionDx.xz:surface.worldPositionDy.xz;
        case 8: return horizontal?surface.uberLocalPositionDx.xy:surface.uberLocalPositionDy.xy;
        default:return horizontal?surface.texCoord0Dx:surface.texCoord0Dy;
    }
}
float4 XR_ADV_SampleUberRole(uint word,uint role,float2 uv,float2 dx,float2 dy)
{
    XRAdvancedMaterialTextureBinding binding=ReadXRAdvancedMaterialTextureBinding(word+92u+role*8u);
    XRAdvancedTextureRecord record; XRAdvancedEncodedTextureReference reference;
    ResolveTexturePairWithSampling(binding.textureReference.handle,binding.samplerReference.handle,
        binding.textureReference.fallback,SceneArena[word+148u+role],record,reference);
    return XR_ADV_SampleTexture2DGrad(reference,uv,dx,dy);
}
float4 XR_ADV_SampleUberUv(uint word,uint role,int selector,float4 st,float2 pan,float time,UberBaseMesh mesh,XRAdvancedSurface surface)
{
    return XR_ADV_SampleUberRole(word,role,uberBaseTransformUv(uberBaseUv(selector,mesh),st,pan,time),
        XR_ADV_UberGradient(surface,selector,true)*st.xy,XR_ADV_UberGradient(surface,selector,false)*st.xy);
}
float3 XR_ADV_UberNormal(uint word,UberBaseMaterialParameters material,UberBaseMesh mesh,XRAdvancedSurface surface,float time)
{
    if(abs(material._BumpScale)<=1e-6) return mesh.vertexNormal;
    UberBaseFrame frame=uberBaseImportedOrFallbackFrame(mesh,surface.worldPositionDx,surface.worldPositionDy,surface.texCoord0Dx,surface.texCoord0Dy);
    float2 uv=uberBaseTransformUv(uberBaseUv(material._BumpMapUV,mesh),material._BumpMap_ST,material._BumpMapPan,time);
    float2 dx=XR_ADV_UberGradient(surface,material._BumpMapUV,true)*material._BumpMap_ST.xy;
    float2 dy=XR_ADV_UberGradient(surface,material._BumpMapUV,false)*material._BumpMap_ST.xy;
    float3 tangentNormal;
    if(material.NormalMapMode==1)
    {
        XRAdvancedMaterialTextureBinding binding=ReadXRAdvancedMaterialTextureBinding(word+100u);
        XRAdvancedTextureRecord record; XRAdvancedEncodedTextureReference reference;
        ResolveTexturePairWithSampling(binding.textureReference.handle,binding.samplerReference.handle,binding.textureReference.fallback,SceneArena[word+149u],record,reference);
        float2 texel=1.0/float2(XR_ADV_Texture2DDimensions(reference));
        float scale=(abs(material.HeightMapScale)>1e-6?material.HeightMapScale:1.0)*material._BumpScale;
        tangentNormal=uberBaseSobelNormal(
            XR_ADV_SampleUberRole(word,1u,uv+float2(-texel.x,-texel.y),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(0.0,-texel.y),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(texel.x,-texel.y),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(-texel.x,0.0),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(texel.x,0.0),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(-texel.x,texel.y),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(0.0,texel.y),dx,dy).r,
            XR_ADV_SampleUberRole(word,1u,uv+float2(texel.x,texel.y),dx,dy).r,scale);
    }
    else tangentNormal=uberBaseUnpackNormal(XR_ADV_SampleUberRole(word,1u,uv,dx,dy),material._BumpScale);
    return uberBaseMappedNormal(tangentNormal,frame);
}
#if defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
static uint3 XR_ADV_UberRasterAddress;
static float3 XR_ADV_UberRasterLighting;
#endif
bool XR_ADV_TryEvaluateUberBase(XRAdvancedMaterialRecord source,XRAdvancedSurface surface,
    out float4 base,out float4 rmse,out float3 normal,out float3 emission,out uint pipeline)
{
    base=float4(0.0);rmse=float4(0.0);normal=surface.shadingNormal;emission=float3(0.0);pipeline=0u;
    uint word,features;
    if(!XR_ADV_TryLoadUberBase(source,word,features,pipeline))return false;
#if defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
    float4 colorRoughness=UberRasterSurface.Load(int4(XR_ADV_UberRasterAddress,0));
    float4 normalMetallic=UberRasterSurface.Load(int4(XR_ADV_UberRasterAddress+uint3(0,0,1),0));
    float4 emissionSpecular=UberRasterSurface.Load(int4(XR_ADV_UberRasterAddress+uint3(0,0,2),0));
    XR_ADV_UberRasterLighting=UberRasterSurface.Load(int4(XR_ADV_UberRasterAddress+uint3(0,0,3),0)).rgb;
    base=float4(colorRoughness.rgb,1.0);normal=normalMetallic.xyz;emission=emissionSpecular.rgb;
    rmse=float4(colorRoughness.a,normalMetallic.a,emissionSpecular.a,0.0);
    return XR_ADV_IsFinite(base)&&XR_ADV_IsFinite(rmse)&&XR_ADV_IsFinite(normal)&&XR_ADV_IsFinite(emission)&&XR_ADV_IsFinite(XR_ADV_UberRasterLighting);
#else
    UberBaseMaterialParameters material=XR_ADV_UberParameters(word);
    UberBaseMesh mesh=XR_ADV_UberMesh(surface);
    float time=(features&UberBaseRenderTime)!=0u?asfloat(Parameters.renderTimeBits):0.0;
    if((features&UberBaseNormalMap)!=0u) mesh.worldNormal=XR_ADV_UberNormal(word,material,mesh,surface,time);
    normal=mesh.worldNormal;
    base=uberBaseColor(material,XR_ADV_SampleUberUv(word,0u,material._MainTexUV,material._MainTex_ST,material._MainTexPan,time,mesh,surface),mesh.vertexColor);
    float4 alphaSample=float4(1.0);
    if((features&UberBaseAlphaMasks)!=0u) alphaSample=XR_ADV_SampleUberUv(word,2u,material._AlphaMaskUV,material._AlphaMask_ST,material._AlphaMaskPan,time,mesh,surface);
    base.a=uberBaseAlpha(material,features,base.a,alphaSample);
    if(!uberBaseCovered(material,base.a))return false;
    base.a=uberBaseOutputAlpha(material,base.a);
    float4 metallic=float4(1.0),smoothness=float4(1.0),specular=float4(1.0);
    if((features&UberBaseAdvancedSpecular)!=0u)
    {
        metallic=XR_ADV_SampleUberUv(word,3u,material._PBRMetallicMapsUV,material._PBRMetallicMaps_ST,material._PBRMetallicMapsPan,time,mesh,surface);
        smoothness=XR_ADV_SampleUberUv(word,4u,material._PBRSmoothnessMapsUV,material._PBRSmoothnessMaps_ST,material._PBRSmoothnessMapsPan,time,mesh,surface);
        specular=XR_ADV_SampleUberUv(word,5u,0,material._SpecularMap_ST,float2(0.0),time,mesh,surface);
    }
    rmse=float4(uberBaseRms(material,features,metallic,smoothness,specular),0.0);
    if((features&UberBaseEmission)!=0u)
    {
        float2 dx=XR_ADV_UberGradient(surface,material._EmissionMapUV,true)*material._EmissionMap_ST.xy;
        float2 dy=XR_ADV_UberGradient(surface,material._EmissionMapUV,false)*material._EmissionMap_ST.xy;
        if(material._EmissionScrollingEnabled>0.5){dx+=surface.uberColorDx.rg*material._EmissionScrollingVertexColor;dy+=surface.uberColorDy.rg*material._EmissionScrollingVertexColor;}
        emission=uberBaseEmissionColor(material,features,XR_ADV_SampleUberRole(word,6u,uberBaseEmissionUv(material,mesh,time),dx,dy));
    }
    return XR_ADV_IsFinite(base)&&XR_ADV_IsFinite(rmse)&&XR_ADV_IsFinite(normal)&&XR_ADV_IsFinite(emission);
#endif
}
