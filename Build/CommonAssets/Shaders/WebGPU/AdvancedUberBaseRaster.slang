// Source-material raster evaluation over the exact retained native triangle stream.
#include "UberBaseSurface.slang"
#define XRE_UBER_BASE_ALL_TEXTURES 1
#define XRE_UBER_BASE_FEATURES 0
struct UberNativeRasterParameters
{
    float4x4 viewProjection;
    float4x4 viewProjectionUnjittered;
    float4x4 previousViewProjectionUnjittered;
    uint triangleBase; uint viewIndex; uint viewFlags; uint origin;
    uint baseColorWord; uint alphaCutoffWord; uint pipelineFlags; uint masked;
    uint directPayloadIndex; uint direct; uint exportSample; uint renderTimeBits;
    float3 cameraPosition; uint uberFeatures;
};
[[vk::binding(0,0)]] StructuredBuffer<uint> SceneArena;
[[vk::binding(1,0)]] StructuredBuffer<uint> GeometryArena;
[[vk::binding(2,0)]] StructuredBuffer<uint> Payloads;
[[vk::binding(3,0)]] StructuredBuffer<uint> Triangles;
[[vk::binding(4,0)]] ConstantBuffer<UberNativeRasterParameters> Parameters;
[[vk::binding(0,1)]] Texture2D<float4> _MainTex;
[[vk::binding(1,1)]] SamplerState _MainTexSampler;
[[vk::binding(2,1)]] Texture2D<float4> _BumpMap;
[[vk::binding(3,1)]] SamplerState _BumpMapSampler;
[[vk::binding(4,1)]] Texture2D<float4> _AlphaMask;
[[vk::binding(5,1)]] SamplerState _AlphaMaskSampler;
[[vk::binding(6,1)]] Texture2D<float4> _PBRMetallicMaps;
[[vk::binding(7,1)]] SamplerState _PBRMetallicMapsSampler;
[[vk::binding(8,1)]] Texture2D<float4> _PBRSmoothnessMaps;
[[vk::binding(9,1)]] SamplerState _PBRSmoothnessMapsSampler;
[[vk::binding(10,1)]] Texture2D<float4> _SpecularMap;
[[vk::binding(11,1)]] SamplerState _SpecularMapSampler;
[[vk::binding(12,1)]] Texture2D<float4> _EmissionMap;
[[vk::binding(13,1)]] SamplerState _EmissionMapSampler;
[[vk::binding(14,1)]] ConstantBuffer<UberBaseMaterialParameters> UberMaterial;
#include "UberBaseFragmentSampling.slang"
#if defined(XR_ADV_UBER_RASTER_EXPORT)
#define XRE_UBER_BASE_NATIVE_RASTER 1
#define XRE_LOCAL_SHADOWS 1
#include "UberBaseLighting.slang"
#include "UberBasePbrEnvironment.slang"
#include "UberBaseForwardLighting.slang"
#if defined(XR_ADV_UBER_MULTISAMPLE)
[[vk::binding(0,2)]] Texture2DMS<uint4,4> WinnerIdentity;
[[vk::binding(1,2)]] Texture2DMS<uint4,4> WinnerMetadata;
#else
[[vk::binding(0,2)]] Texture2D<uint2> WinnerIdentity;
[[vk::binding(1,2)]] Texture2D<uint> WinnerMetadata;
#endif
[[vk::binding(2,2)]] [[vk::image_format("rgba32f")]] WTexture2DArray<float4> UberRasterSurface;
#endif
static const uint Invalid=0xffffffffu;
uint WordCount(StructuredBuffer<uint> source) { uint count,stride;source.GetDimensions(count,stride);return count; }
uint2 Handle(uint word) { return uint2(SceneArena[word],SceneArena[word+1u]); }
float4 SceneVector(uint word) { return asfloat(uint4(SceneArena[word],SceneArena[word+1u],SceneArena[word+2u],SceneArena[word+3u])); }
float4 GeometryVector(uint word) { return asfloat(uint4(GeometryArena[word],GeometryArena[word+1u],GeometryArena[word+2u],GeometryArena[word+3u])); }
float4x4 Matrix(uint word) { return transpose(float4x4(SceneVector(word),SceneVector(word+4u),SceneVector(word+8u),SceneVector(word+12u))); }
uint Resolve(uint table,uint2 handle)
{
    uint header=table*8u;
    if(handle.x==0u||handle.y==0u||handle.x>=SceneArena[header+4u])return Invalid;
    uint lookup=SceneArena[header+3u]+handle.x*2u;
    if(SceneArena[lookup]!=handle.y)return Invalid;
    uint dense=SceneArena[lookup+1u];
    return dense<SceneArena[header+1u]?SceneArena[header]+dense*SceneArena[header+2u]:Invalid;
}
uint VertexWord(uint stream,uint vertex)
{
    return stream<7u&&vertex<GeometryArena[stream*4u+1u]/64u?GeometryArena[stream*4u]+vertex*16u:Invalid;
}
bool Finite(float4 value) { return all((asuint(value)&0x7f800000u)!=0x7f800000u); }
uint SourceWord(uint table,uint2 draw,uint2 geometry,uint vertex,uint stride)
{
    uint count=WordCount(SceneArena),header=table*8u;
    if(count<288u||SceneArena[header+2u]!=12u||SceneArena[header+5u]!=1u)return Invalid;
    if(table==35u&&SceneArena[header+6u]!=36u)return Invalid;
    uint dense=SceneArena[SceneArena[3u]+draw.x*2u+1u],start=SceneArena[header];
    if(dense>=SceneArena[header+1u]||start<288u||start>count||dense>=(count-start)/12u)return Invalid;
    uint row=start+dense*12u;
    if(any(Handle(row)!=draw)||any(Handle(row+2u)!=geometry)||vertex>=SceneArena[row+7u])return Invalid;
    uint offset=SceneArena[row+6u];
    return offset<=count&&vertex<(count-offset)/stride?offset+vertex*stride:Invalid;
}
struct UberNativeVertex
{
    float4 position:SV_Position;
    [[vk::location(0)]] nointerpolation uint2 identity:TEXCOORD0;
    [[vk::location(1)]] nointerpolation uint metadata:TEXCOORD1;
    [[vk::location(2)]] nointerpolation uint selection:TEXCOORD2;
    [[vk::location(3)]] float3 world:TEXCOORD3;
    [[vk::location(4)]] float3 local:TEXCOORD4;
    [[vk::location(5)]] float3 normal:TEXCOORD5;
    [[vk::location(6)]] float3 tangent:TEXCOORD6;
    [[vk::location(7)]] float3 bitangent:TEXCOORD7;
    [[vk::location(8)]] float4 uv01:TEXCOORD8;
    [[vk::location(9)]] float4 uv23:TEXCOORD9;
    [[vk::location(10)]] float4 color:TEXCOORD10;
};
UberNativeVertex Rejected() { UberNativeVertex result=(UberNativeVertex)0;result.position=float4(2,2,2,1);result.identity=uint2(Invalid);return result; }
float3 CofactorDirection(float4x4 model,float3 direction)
{
    float3 a=float3(model[0][0],model[1][0],model[2][0]);
    float3 b=float3(model[0][1],model[1][1],model[2][1]);
    float3 c=float3(model[0][2],model[1][2],model[2][2]);
    return cross(b,c)*direction.x+cross(c,a)*direction.y+cross(a,b)*direction.z;
}
[shader("vertex")]
UberNativeVertex advancedUberVertex(uint vertexIndex:SV_VertexID,uint instanceIndex:SV_InstanceID)
{
    if(instanceIndex!=0u||Parameters.viewIndex>255u||Parameters.origin>1u)return Rejected();
    uint payloadIndex=Parameters.directPayloadIndex,producer=3u,primitive=Invalid,stream=0u,vertex=0u;
    if(Parameters.direct==0u)
    {
        uint index=Parameters.triangleBase+vertexIndex/3u;
        if(index>=WordCount(Triangles)/8u)return Rejected();
        uint triangle=index*8u;payloadIndex=Triangles[triangle];primitive=Triangles[triangle+1u];
        producer=Triangles[triangle+2u];stream=Triangles[triangle+3u];vertex=Triangles[triangle+4u+vertexIndex%3u];
    }
    if(payloadIndex>=WordCount(Payloads)/24u)return Rejected();
    uint payload=payloadIndex*24u;
    if(Parameters.direct!=0u)
    {
        bool deformed=(Payloads[payload+22u]&1u)!=0u;producer=deformed?4u:3u;stream=deformed?2u:0u;
        uint first=Payloads[payload+15u],index=first+vertexIndex;
        if(vertexIndex>=Payloads[payload+16u]||index<first||index>=GeometryArena[5u]/4u||first%3u!=0u)return Rejected();
        uint local=GeometryArena[GeometryArena[4u]+index];vertex=Payloads[payload+6u]+local;
        if(local>=Payloads[payload+17u]||vertex<local)return Rejected();primitive=first/3u+vertexIndex/3u;
    }
    uint2 drawHandle=uint2(Payloads[payload],Payloads[payload+1u]),meshHandle=uint2(Payloads[payload+2u],Payloads[payload+3u]);
    uint draw=Resolve(0u,drawHandle),mesh=Resolve(2u,meshHandle),material=Resolve(3u,uint2(Payloads[payload+4u],Payloads[payload+5u]));
    if(draw==Invalid||mesh==Invalid||material==Invalid||SceneArena[material+15u]!=10u)return Rejected();
    uint instance=Resolve(1u,Handle(draw)),transform=Resolve(17u,Handle(draw+12u)),previousTransform=Resolve(17u,Handle(draw+14u));
    if(instance==Invalid||transform==Invalid||previousTransform==Invalid||producer>4u||primitive==Invalid||(stream!=0u&&stream!=2u))return Rejected();
    uint address=VertexWord(stream,vertex);
    if(address==Invalid||vertex<Payloads[payload+6u])return Rejected();
    uint relative=vertex-Payloads[payload+6u],attributes=SourceWord(35u,drawHandle,meshHandle,relative,12u),basis=SourceWord(33u,drawHandle,meshHandle,relative,8u);
    if(attributes==Invalid||basis==Invalid)return Rejected();
    float3 normal=SceneVector(basis).xyz;uint flags=SceneArena[basis+3u];float4 tangent=SceneVector(basis+4u);
    if(stream==2u)
    {
        uint start=GeometryArena[10u],bytes=GeometryArena[11u],words=WordCount(GeometryArena);
        if(start<28u||start>words||bytes==0u||(bytes&31u)!=0u||bytes/4u>words-start||vertex>=bytes/32u)return Rejected();
        uint current=start+vertex*8u;uint currentFlags=GeometryArena[current+3u];
        if((flags&1u)!=(currentFlags&1u))return Rejected();
        normal=GeometryVector(current).xyz;tangent=GeometryVector(current+4u);flags=currentFlags;
    }
    if((flags&~7u)!=0u||(flags&2u)==0u)return Rejected();
    float4x4 model=Matrix(transform);float3 local=GeometryVector(address).xyz;float4 world=mul(model,float4(local,1));
    float4 clip=mul(Parameters.viewProjection,world);if(!Finite(clip))return Rejected();
    float3 previousPosition=local;bool previousValid=stream==0u;
    if(stream==2u){uint previousVertex=Payloads[payload+7u]+relative,previousAddress=VertexWord(3u,previousVertex);previousValid=previousVertex>=Payloads[payload+7u]&&previousAddress!=Invalid;if(previousValid)previousPosition=GeometryVector(previousAddress).xyz;}
    float4 previousWorld=mul(Matrix(previousTransform),float4(previousPosition,1));
    bool velocityValid=previousValid&&(Parameters.viewFlags&128u)!=0u&&((Payloads[payload+22u]>>16u)&15u)==0u&&SceneArena[mesh+5u]!=0u&&SceneArena[mesh+13u]!=0u&&Finite(mul(Parameters.viewProjectionUnjittered,world))&&Finite(mul(Parameters.previousViewProjectionUnjittered,previousWorld));
    uint editor=Resolve(23u,Handle(draw+10u)),selection=Invalid,editorFlags=0u;
    if(editor!=Invalid){selection=SceneArena[editor+6u];editorFlags=SceneArena[editor+7u]&3u;}
    UberNativeVertex result;result.position=clip;result.identity=uint2(drawHandle.x,primitive);result.selection=selection;
    result.metadata=producer|(Parameters.origin<<3u)|(Parameters.masked<<4u)|(uint(velocityValid)<<6u)|(Parameters.viewIndex<<8u)|(1u<<16u)|(uint(selection!=Invalid)<<24u)|(editorFlags<<25u);
    result.world=world.xyz;result.local=local;result.normal=normalize(CofactorDirection(model,normal));
    result.tangent=float3(0);result.bitangent=float3(0);
    if((flags&5u)==5u&&dot(tangent.xyz,tangent.xyz)>0.0){result.tangent=normalize(CofactorDirection(model,tangent.xyz));result.bitangent=normalize(CofactorDirection(model,cross(normal,tangent.xyz)*tangent.w));}
    result.uv01=SceneVector(attributes);result.uv23=SceneVector(attributes+4u);result.color=SceneVector(attributes+8u);return result;
}
UberBaseMesh FragmentMesh(UberNativeVertex input,bool front)
{
    UberBaseMesh mesh;mesh.worldPosition=input.world;mesh.localPosition=input.local;mesh.vertexNormal=normalize(input.normal)*(front?1.0:-1.0);
    mesh.worldNormal=mesh.vertexNormal;mesh.viewDirection=normalize(Parameters.cameraPosition-input.world);mesh.tangent=input.tangent;mesh.bitangent=input.bitangent;
    mesh.vertexColor=input.color;mesh.uv0=input.uv01.xy;mesh.uv1=input.uv01.zw;mesh.uv2=input.uv23.xy;mesh.uv3=input.uv23.zw;return mesh;
}
float4 EvaluateCoverage(UberNativeVertex input,bool front,out UberBaseMesh mesh,out float time)
{
    mesh=FragmentMesh(input,front);time=(Parameters.uberFeatures&16u)!=0u?asfloat(Parameters.renderTimeBits):0.0;
    mesh.worldNormal=uberBaseSampleNormal(UberMaterial,mesh,Parameters.uberFeatures,time);
    float4 base=uberBaseSampleColor(UberMaterial,mesh,time);
    base.a=uberBaseSampleAlpha(UberMaterial,mesh,base.a,Parameters.uberFeatures,time);
    if(!uberBaseCovered(UberMaterial,base.a))discard;
    return base;
}
uint4 PackPair(uint2 value){return uint4(value.x&65535u,value.x>>16u,value.y&65535u,value.y>>16u);}
#if !defined(XR_ADV_UBER_RASTER_EXPORT)
struct UberVisibilityOutput
{
#if defined(XR_ADV_UBER_MULTISAMPLE)
    uint4 identity:SV_Target0;uint4 metadataSelection:SV_Target1;float2 samplePosition:SV_Target2;
#else
    uint2 identity:SV_Target0;uint metadata:SV_Target1;uint selection:SV_Target2;
#endif
};
[shader("fragment")]
UberVisibilityOutput advancedUberVisibility(UberNativeVertex input,bool front:SV_IsFrontFace
#if defined(XR_ADV_UBER_MULTISAMPLE)
    ,uint sampleIndex:SV_SampleIndex
#endif
)
{
    UberBaseMesh mesh;float time;EvaluateCoverage(input,front,mesh,time);
    if(input.identity.x==0u||input.identity.x==Invalid||input.identity.y==Invalid)discard;
    uint metadata=input.metadata|(uint(front)<<5u);UberVisibilityOutput result;
#if defined(XR_ADV_UBER_MULTISAMPLE)
    static const float2 positions[4]={float2(.375,.125),float2(.875,.375),float2(.125,.625),float2(.625,.875)};
    result.identity=PackPair(input.identity);result.metadataSelection=PackPair(uint2(metadata,input.selection));result.samplePosition=positions[sampleIndex];
#else
    result.identity=input.identity;result.metadata=metadata;result.selection=input.selection;
#endif
    return result;
}
#else
[shader("fragment")]
void advancedUberSurface(UberNativeVertex input,bool front:SV_IsFrontFace
#if defined(XR_ADV_UBER_MULTISAMPLE)
    ,uint sampleIndex:SV_SampleIndex
#endif
)
{
    UberBaseMesh mesh;float time;float4 base=EvaluateCoverage(input,front,mesh,time);
    float3 rms=uberBaseSampleRms(UberMaterial,mesh,Parameters.uberFeatures,time);
    float3 emission=uberBaseSampleEmission(UberMaterial,mesh,Parameters.uberFeatures,time);
    float3 lit=uberBaseForwardLighting(mesh,base.rgb,rms,input.position.xy,Parameters.pipelineFlags);
    uint2 pixel=uint2(input.position.xy);uint layer=0u;uint2 identity;uint metadata;
#if defined(XR_ADV_UBER_MULTISAMPLE)
    uint4 packed=WinnerIdentity.Load(pixel,sampleIndex),packedMetadata=WinnerMetadata.Load(pixel,sampleIndex);
    identity=uint2(packed.x|(packed.y<<16u),packed.z|(packed.w<<16u));metadata=packedMetadata.x|(packedMetadata.y<<16u);
#else
    identity=WinnerIdentity.Load(int3(pixel,0));metadata=WinnerMetadata.Load(int3(pixel,0));
#endif
    // Sample selection follows all implicit samples and retains default-center varyings.
#if defined(XR_ADV_UBER_MULTISAMPLE)
    if(sampleIndex!=Parameters.exportSample)discard;
#endif
    // All source implicit samples precede this divergent winner gate. A depth
    // comparison alone cannot identify the selected coplanar draw or instance.
    if(input.identity.x==0u||input.identity.x==Invalid||any(identity!=input.identity)||metadata!=(input.metadata|(uint(front)<<5u)))discard;
    UberRasterSurface.Store(int3(pixel,layer),float4(base.rgb,rms.x));
    UberRasterSurface.Store(int3(pixel,layer+1u),float4(mesh.worldNormal,rms.y));
    UberRasterSurface.Store(int3(pixel,layer+2u),float4(emission,rms.z));
    UberRasterSurface.Store(int3(pixel,layer+3u),float4(lit,1.0));
}
#endif
