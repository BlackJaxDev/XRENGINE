// Canonical Advanced record layouts and raw scene/geometry access.
// The source records mirror Advanced/Access and VisibilityInterface exactly.
#if !defined(XR_ADV_AMBIENT_OCCLUSION_SCHEMA_VERSION) || XR_ADV_AMBIENT_OCCLUSION_SCHEMA_VERSION != 1
#error Native shading requires the AO flag and neutral-binding semantic contract.
#endif

struct XRAdvancedGpuHandle
{
    uint index;
    uint generation;

};
struct XRAdvancedBufferReference
{
    XRAdvancedGpuHandle bufferHandle;
    uint2 byteOffset;
    uint elementOffset;
    uint elementCount;
    uint elementStride;
    uint flags;

};
struct XRAdvancedTextureReference
{
    XRAdvancedGpuHandle handle;
    uint fallback;
    uint reserved;

};
struct XRAdvancedSamplerReference
{
    XRAdvancedGpuHandle handle;
    uint fallback;
    uint reserved;

};
struct XRAdvancedEncodedTextureReference
{
    uint payload0;
    uint payload1;
    uint payload2;
    uint flags;

};
struct XRAdvancedMaterialTextureBinding
{
    XRAdvancedTextureReference textureReference;
    XRAdvancedSamplerReference samplerReference;

};
struct XRAdvancedDrawRecord
{
    XRAdvancedGpuHandle instance;
    XRAdvancedGpuHandle geometry;
    XRAdvancedGpuHandle material;
    XRAdvancedGpuHandle deformation;
    XRAdvancedGpuHandle renderState;
    XRAdvancedGpuHandle editorIdentity;
    XRAdvancedGpuHandle currentTransform;
    XRAdvancedGpuHandle previousTransform;
    uint primitiveSection;
    uint flags;
    uint reserved0;
    uint reserved1;

};
struct XRAdvancedTransformRecord
{
    float4x4 world;
    uint frameSlot;
    uint flags;
    uint reserved0;
    uint reserved1;

};
struct XRAdvancedInstanceRecord
{
    float4x4 currentWorld;
    float4x4 previousWorld;
    float4 boundsSphere;
    float4 boundsMin;
    float4 boundsMax;
    XRAdvancedGpuHandle animation;
    XRAdvancedGpuHandle deformation;
    uint visibilityFlags;
    uint lodLevel;
    uint layerMask;
    uint renderPassMask;
    uint currentFrameSlot;
    uint previousFrameSlot;
    uint reserved0;
    uint reserved1;

};
struct XRAdvancedGeometryRecord
{
    XRAdvancedBufferReference currentVertexData;
    XRAdvancedBufferReference previousVertexData;
    XRAdvancedBufferReference indexData;
    XRAdvancedBufferReference meshletDescriptors;
    XRAdvancedBufferReference meshletVertexIndices;
    XRAdvancedBufferReference meshletTriangleWords;
    XRAdvancedGpuHandle fallbackGeometry;
    uint vertexBase;
    uint vertexCount;
    uint indexBase;
    uint indexCount;
    uint meshletFirst;
    uint meshletCount;
    uint2 vertexLayoutId;
    uint reservedLayout0;
    uint reservedLayout1;
    float4 boundsSphere;
    float4 boundsMin;
    float4 boundsMax;
    uint materialSectionFirst;
    uint materialSectionCount;
    uint primitiveTopology;
    uint source;
    uint residency;
    uint missingBehavior;
    uint cookedLayoutVersion;
    uint flags;

};
struct XRAdvancedMaterialRecord
{
    uint stableRowId;
    uint generation;
    uint shadingKernelId;
    uint shadingKernelGeneration;
    uint2 materialLayoutHash;
    uint renderStateClass;
    uint coverageMode;
    uint requiredAttributeMask;
    uint textureReferenceOffset;
    uint textureReferenceCount;
    uint constantWordOffset;
    uint constantWordCount;
    uint featureFlags;
    uint eligibilityFlags;
    uint reserved;

};
struct XRAdvancedShadingKernelRecord
{
    uint stableKernelId;
    uint generation;
    uint2 materialLayoutHash;
    uint requiredAttributeMask;
    uint supportedCoverageMask;
    uint supportedEligibility;
    uint supportedFeatures;
    uint2 shaderIdentityHash;
    uint renderStateClassMask;
    uint flags;
    uint reserved0;
    uint reserved1;
    uint reserved2;
    uint reserved3;

};
struct XRAdvancedLightRecord
{
    uint stableLightId;
    uint generation;
    uint type;
    uint flags;
    float4 positionAndRadius;
    float4 directionAndOuterCone;
    float4 colorAndIntensity;
    float4 shapeAndInnerCone;
    XRAdvancedTextureReference cookieTexture;
    XRAdvancedGpuHandle shadowRecord;
    uint layerMask;
    uint viewMaskLo;
    uint viewMaskHi;
    uint reserved0;
    uint reserved1;
    uint reserved2;

};
struct XRAdvancedProbeRecord
{
    uint stableProbeId;
    uint generation;
    uint type;
    uint flags;
    float4 positionAndRadius;
    float4 influenceInner;
    float4 influenceOuter;
    float4 influenceOffsetAndShape;
    float4 proxyCenterAndEnable;
    float4 proxyHalfExtents;
    float4 proxyRotation;
    XRAdvancedTextureReference irradiance;
    XRAdvancedTextureReference prefilteredRadiance;
    uint viewMaskLo;
    uint viewMaskHi;
    uint priority;
    uint reserved;

};
struct XRAdvancedDecalRecord
{
    XRAdvancedGpuHandle identity;
    XRAdvancedGpuHandle material;
    uint flags;
    uint viewMaskLo;
    uint viewMaskHi;
    uint layerMask;
    float4x4 worldToDecal;
    float4x4 previousWorldToDecal;
    float4 halfExtentsAndFade;
    XRAdvancedTextureReference maskTexture;

};
struct XRAdvancedShadowRecord
{
    uint stableShadowId;
    uint generation;
    uint type;
    uint flags;
    XRAdvancedTextureReference textureReference;
    float4x4 worldToShadow;
    float4x4 previousWorldToShadow;
    float4 uvScaleBias;
    float4 depthBiasAndFilter;
    uint textureLayer;
    uint encoding;
    uint cascadeOffset;
    uint cascadeCount;
    uint viewMaskLo;
    uint viewMaskHi;
    uint lastRenderedFrameLo;
    uint lastRenderedFrameHi;
    float4 momentParameters;
    float4 depthRangeAndCascade;
    float4 renderedLightPositionAndFar;

};
struct XRAdvancedTextureRecord
{
    uint stableTextureId;
    uint generation;
    uint dimension;
    uint flags;
    uint width;
    uint height;
    uint depthOrLayers;
    uint mipCount;
    uint formatClass;
    uint encodedReferenceIndex;
    XRAdvancedGpuHandle defaultSampler;
    float4 uvScaleBias;

};
struct XRAdvancedSamplerRecord
{
    uint stableSamplerId;
    uint generation;
    uint filterMode;
    uint flags;
    uint addressU;
    uint addressV;
    uint addressW;
    uint compareOperation;
    float4 lodBiasMinMaxAnisotropy;
    float4 borderColor;

};
struct XRAdvancedViewRecord
{
    float4x4 view;
    float4x4 projectionJittered;
    float4x4 projectionUnjittered;
    float4x4 viewProjectionJittered;
    float4x4 viewProjectionUnjittered;
    float4x4 previousView;
    float4x4 previousProjectionJittered;
    float4x4 previousProjectionUnjittered;
    float4x4 previousViewProjectionJittered;
    float4x4 previousViewProjectionUnjittered;
    float4x4 inverseViewProjectionJittered;
    float4x4 inverseViewProjectionUnjittered;
    float4 cameraPositionAndNear;
    float4 cameraForwardAndFar;
    float4 renderSizeAndInverse;
    float4 outputSizeAndInverse;
    float4 currentAndPreviousJitter;
    float4 depthParams;
    uint viewId;
    uint outputLayer;
    uint flags;
    uint historyKeyLo;
    uint historyKeyHi;
    uint viewGeneration;
    uint viewMaskLo;
    uint viewMaskHi;
    float4 foveationCenterAndBias;
    float4 foveationRadii;
    uint sourceCameraIdentityLo;
    uint sourceCameraIdentityHi;
    uint sourceCameraIdentityPadding0;
    uint sourceCameraIdentityPadding1;

};
struct XRAdvancedVisibilityPackedVertex
{
    float3 position;
    uint normalOct;
    uint tangentOctAndSign;
    uint texCoord0Half;
    uint texCoord1Half;
    uint color0Rgba8;
    uint color1Rgba8;
    uint custom0;
    uint sourceVertex;
    uint flags;
    uint reserved0;
    uint reserved1;
    uint reserved2;
    uint reserved3;

};
struct XRAdvancedVisibilityMeshletDescriptor
{
    float4 boundsSphere;
    uint vertexOffset;
    uint triangleByteOffset;
    uint vertexCount;
    uint triangleCount;
    float4 cone;
    float4 coneApex;
    uint packedCone;
    uint reserved0;
    uint reserved1;
    uint reserved2;

};
struct XRAdvancedPreparedDrawDeformationRecord
{
    XRAdvancedGpuHandle geometry;
    XRAdvancedGpuHandle deformation;
    uint currentVertexOffset;
    uint previousVertexOffset;
    uint vertexCount;
    uint flags;

};
struct XRAdvancedSurface
{
    uint2 identity;
    uint producer;
    uint viewIndex;
    uint materialDenseIndex;
    uint shadingKernelDenseIndex;
    uint primitiveSection;
    uint layerMask;
    uint requiredAttributeMask;
    uint authoredRenderPass;
    float3 worldPosition;
    // Invocation-local helper reconstruction state, never part of a buffer ABI.
    float3 interpolatedWorldPosition;
    float3 worldPositionDx;
    float3 worldPositionDy;
    uint visibilityMetadata;
    float2 samplePixelPosition;
    float3 geometricNormal;
    float3 shadingNormal;
    float3 tangent;
    float3 bitangent;
    float2 texCoord0;
    float2 texCoord1;
    float2 uberTexCoord2;
    float2 uberTexCoord3;
    float3 uberLocalPosition;
    float3 uberLocalPositionDx;
    float3 uberLocalPositionDy;
    float2 uberTexCoord2Dx;
    float2 uberTexCoord2Dy;
    float2 uberTexCoord3Dx;
    float2 uberTexCoord3Dy;
    float4 uberColorDx;
    float4 uberColorDy;
    float4 color0;
    float4 color1;
    uint custom0;
    float3 barycentrics;
    float3 barycentricDx;
    float3 barycentricDy;
    float2 texCoord0Dx;
    float2 texCoord0Dy;
    float2 texCoord1Dx;
    float2 texCoord1Dy;
    float4 currentUnjitteredClip;
    float4 previousUnjitteredClip;
    float2 motionNdc;
    float depth;
    float tangentHandedness;
    uint flags;
    uint invalidReason;

};

struct ShadeParameters
{
    uint2 screenExtent; uint2 tilesCount;
    uint viewIndex; uint viewCount; uint kernelIndex; uint flags;
    uint depthSlices; uint maxLightIndices; uint lightCount; uint maxKernelTiles;
    float4 backgroundColor;
    uint cohortIndex; uint cohortCount; uint tileCapacity; uint bindingCount;
    uint2 standardDeferredHash; uint2 standardForwardHash; uint2 standardMaskedHash; uint2 mirrorHash;
    uint standardWordCount; uint baseColorWord; uint rmseWord; uint materialFlagsWord;
    uint emissionColorWord; uint emissionStrengthWord; uint emissionMetadataWord; uint emissionUvWord;
    uint emissionRotationWord; uint reserved0; uint reserved1; uint renderTimeBits;
};
[[vk::binding(0, 0)]] StructuredBuffer<uint> SceneArena;
[[vk::binding(1, 0)]] StructuredBuffer<uint> GeometryArena;
[[vk::binding(2, 0)]] StructuredBuffer<uint> PreparedDrawDeformations;
[[vk::binding(3, 0)]] StructuredBuffer<uint> MaterialGroups;
[[vk::binding(4, 0)]] StructuredBuffer<uint> ShadeTiles;
[[vk::binding(5, 0)]] StructuredBuffer<uint> ShadeCounts;
[[vk::binding(6, 0)]] StructuredBuffer<uint> TextureBindings;
[[vk::binding(7, 0)]] ConstantBuffer<XRAdvancedViewRecord> FrozenView;
[[vk::binding(8, 0)]] ConstantBuffer<ShadeParameters> Parameters;
#define u_Push Parameters
static const uint XR_ADV_INVALID_DENSE_INDEX = 0xffffffffu;
static const uint XR_ADV_VISIBILITY_PAYLOAD_VERSION = 1u;
static const uint XR_ADV_SURFACE_CONTRACT_VERSION = 1u;
static uint textureFailure;
#define XR_ADV_DIAGNOSTIC_DRAW 0u
#define XR_ADV_DIAGNOSTIC_INSTANCE 0u
#define XR_ADV_DIAGNOSTIC_MESH 0u
#define XR_ADV_DIAGNOSTIC_MATERIAL 0u
#define XR_ADV_DIAGNOSTIC_TEXTURE 0u
#define XR_ADV_DIAGNOSTIC_SHADOW 0u
#define XR_ADV_SHADING_REQUIRED 2u
#define XR_ADV_SHADING_REPAIR 1u
#define XR_ADV_KERNEL_REQUIRED_ANALYTICAL_DERIVATIVES 2048u
#define XR_ADV_KERNEL_TILE_ANALYTICAL_DERIVATIVES 4u
#define XR_ADV_STANDARD_WORD_COUNT Parameters.standardWordCount
#define XR_ADV_STANDARD_BASE_COLOR_WORD Parameters.baseColorWord
#define XR_ADV_STANDARD_RMSE_WORD Parameters.rmseWord
#define XR_ADV_STANDARD_FLAGS_WORD Parameters.materialFlagsWord
#define XR_ADV_STANDARD_EMISSION_COLOR_WORD Parameters.emissionColorWord
#define XR_ADV_STANDARD_EMISSION_STRENGTH_WORD Parameters.emissionStrengthWord
#define XR_ADV_STANDARD_EMISSION_TEXTURE_METADATA_WORD Parameters.emissionMetadataWord
#define XR_ADV_STANDARD_EMISSION_UV_SCALE_OFFSET_WORD Parameters.emissionUvWord
#define XR_ADV_STANDARD_EMISSION_UV_ROTATION_WORD Parameters.emissionRotationWord
#define XR_ADV_STANDARD_DEFERRED_LAYOUT_LO Parameters.standardDeferredHash.x
#define XR_ADV_STANDARD_DEFERRED_LAYOUT_HI Parameters.standardDeferredHash.y
#define XR_ADV_STANDARD_FORWARD_LAYOUT_LO Parameters.standardForwardHash.x
#define XR_ADV_STANDARD_FORWARD_LAYOUT_HI Parameters.standardForwardHash.y
#define XR_ADV_STANDARD_MASKED_LAYOUT_LO Parameters.standardMaskedHash.x
#define XR_ADV_STANDARD_MASKED_LAYOUT_HI Parameters.standardMaskedHash.y
#define XR_ADV_PROJECTIVE_MIRROR_LAYOUT_LO Parameters.mirrorHash.x
#define XR_ADV_PROJECTIVE_MIRROR_LAYOUT_HI Parameters.mirrorHash.y
#define XR_ADV_PROJECTIVE_MIRROR_WORD_COUNT 64u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW0_TEXTURE_WORD 0u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW0_MATRIX_WORD 4u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW0_IDENTITY_LO_WORD 20u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW0_IDENTITY_HI_WORD 21u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW0_VALID_WORD 22u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW0_FRAMEBUFFER_Y_DOWN_WORD 23u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW1_TEXTURE_WORD 1u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW1_MATRIX_WORD 24u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW1_IDENTITY_LO_WORD 40u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW1_IDENTITY_HI_WORD 41u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW1_VALID_WORD 42u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW1_FRAMEBUFFER_Y_DOWN_WORD 43u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW2_TEXTURE_WORD 2u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW2_MATRIX_WORD 44u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW2_IDENTITY_LO_WORD 60u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW2_IDENTITY_HI_WORD 61u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW2_VALID_WORD 62u
#define XR_ADV_PROJECTIVE_MIRROR_VIEW2_FRAMEBUFFER_Y_DOWN_WORD 63u

uint SceneCount(uint table) { return SceneArena[table * 8u + 1u]; }
uint SceneRow(uint table, uint index) { return SceneArena[table * 8u] + index * SceneArena[table * 8u + 2u]; }
uint GeometryCount(uint stream, uint stride) { return GeometryArena[stream * 4u + 1u] / stride; }
uint GeometryRow(uint stream, uint index, uint stride) { return GeometryArena[stream * 4u] + index * stride; }
uint PreparedCount() { uint count, stride; PreparedDrawDeformations.GetDimensions(count, stride); return count / 8u; }
uint MaterialGroupCount() { uint count, stride; MaterialGroups.GetDimensions(count, stride); return count; }
uint TileWordCount() { uint count, stride; ShadeTiles.GetDimensions(count, stride); return count; }
uint TextureBindingCount() { uint count, stride; TextureBindings.GetDimensions(count, stride); return min(count / 8u, Parameters.bindingCount); }
uint2 LookupSegment(uint table) { return uint2(table, SceneArena[table * 8u + 4u]); }
uint XR_ADV_ResolveHandle(XRAdvancedGpuHandle handle, uint table, uint count, uint category)
{
    if (handle.index == 0u || handle.generation == 0u || handle.index >= count) return XR_ADV_INVALID_DENSE_INDEX;
    uint word = SceneArena[table * 8u + 3u] + handle.index * 2u;
    uint dense = SceneArena[word + 1u];
    return SceneArena[word] == handle.generation && dense < SceneCount(table) ? dense : XR_ADV_INVALID_DENSE_INDEX;
}
uint XR_ADV_ResolveVisibilityHandle(XRAdvancedGpuHandle handle, uint2 segment, uint category)
{ return XR_ADV_ResolveHandle(handle, segment.x, segment.y, category); }
uint XR_ADV_ResolveVisibilityStableIndex(uint index, uint2 segment, uint category)
{
    if (index == 0u || index >= segment.y) return XR_ADV_INVALID_DENSE_INDEX;
    uint word = SceneArena[segment.x * 8u + 3u] + index * 2u;
    uint dense = SceneArena[word + 1u];
    return SceneArena[word] != 0u && dense < SceneCount(segment.x) ? dense : XR_ADV_INVALID_DENSE_INDEX;
}
#define VisibilityDrawLookupSegment LookupSegment(0u)
#define VisibilityInstanceLookupSegment LookupSegment(1u)
#define VisibilityGeometryLookupSegment LookupSegment(2u)
#define VisibilityMaterialLookupSegment LookupSegment(3u)
#define VisibilityShadowLookupSegment LookupSegment(6u)
#define VisibilityTextureLookupSegment LookupSegment(7u)
#define VisibilitySamplerLookupSegment LookupSegment(8u)
#define VisibilityTransformLookupSegment LookupSegment(17u)
#define VisibilityShadingKernelLookupSegment LookupSegment(21u)
XRAdvancedGpuHandle ReadXRAdvancedGpuHandle(uint word)
{
    XRAdvancedGpuHandle result;
    result.index = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    return result;
}
XRAdvancedBufferReference ReadXRAdvancedBufferReference(uint word)
{
    XRAdvancedBufferReference result;
    result.bufferHandle = ReadXRAdvancedGpuHandle(word + 0u);
    result.byteOffset = uint2(SceneArena[word + 2u], SceneArena[word + 3u]);
    result.elementOffset = SceneArena[word + 4u];
    result.elementCount = SceneArena[word + 5u];
    result.elementStride = SceneArena[word + 6u];
    result.flags = SceneArena[word + 7u];
    return result;
}
XRAdvancedTextureReference ReadXRAdvancedTextureReference(uint word)
{
    XRAdvancedTextureReference result;
    result.handle = ReadXRAdvancedGpuHandle(word + 0u);
    result.fallback = SceneArena[word + 2u];
    result.reserved = SceneArena[word + 3u];
    return result;
}
XRAdvancedSamplerReference ReadXRAdvancedSamplerReference(uint word)
{
    XRAdvancedSamplerReference result;
    result.handle = ReadXRAdvancedGpuHandle(word + 0u);
    result.fallback = SceneArena[word + 2u];
    result.reserved = SceneArena[word + 3u];
    return result;
}
XRAdvancedEncodedTextureReference ReadXRAdvancedEncodedTextureReference(uint word)
{
    XRAdvancedEncodedTextureReference result;
    result.payload0 = SceneArena[word + 0u];
    result.payload1 = SceneArena[word + 1u];
    result.payload2 = SceneArena[word + 2u];
    result.flags = SceneArena[word + 3u];
    return result;
}
XRAdvancedMaterialTextureBinding ReadXRAdvancedMaterialTextureBinding(uint word)
{
    XRAdvancedMaterialTextureBinding result;
    result.textureReference = ReadXRAdvancedTextureReference(word + 0u);
    result.samplerReference = ReadXRAdvancedSamplerReference(word + 4u);
    return result;
}
XRAdvancedDrawRecord ReadXRAdvancedDrawRecord(uint word)
{
    XRAdvancedDrawRecord result;
    result.instance = ReadXRAdvancedGpuHandle(word + 0u);
    result.geometry = ReadXRAdvancedGpuHandle(word + 2u);
    result.material = ReadXRAdvancedGpuHandle(word + 4u);
    result.deformation = ReadXRAdvancedGpuHandle(word + 6u);
    result.renderState = ReadXRAdvancedGpuHandle(word + 8u);
    result.editorIdentity = ReadXRAdvancedGpuHandle(word + 10u);
    result.currentTransform = ReadXRAdvancedGpuHandle(word + 12u);
    result.previousTransform = ReadXRAdvancedGpuHandle(word + 14u);
    result.primitiveSection = SceneArena[word + 16u];
    result.flags = SceneArena[word + 17u];
    result.reserved0 = SceneArena[word + 18u];
    result.reserved1 = SceneArena[word + 19u];
    return result;
}
XRAdvancedTransformRecord ReadXRAdvancedTransformRecord(uint word)
{
    XRAdvancedTransformRecord result;
    result.world = float4x4(asfloat(SceneArena[word + 0u]), asfloat(SceneArena[word + 1u]), asfloat(SceneArena[word + 2u]), asfloat(SceneArena[word + 3u]), asfloat(SceneArena[word + 4u]), asfloat(SceneArena[word + 5u]), asfloat(SceneArena[word + 6u]), asfloat(SceneArena[word + 7u]), asfloat(SceneArena[word + 8u]), asfloat(SceneArena[word + 9u]), asfloat(SceneArena[word + 10u]), asfloat(SceneArena[word + 11u]), asfloat(SceneArena[word + 12u]), asfloat(SceneArena[word + 13u]), asfloat(SceneArena[word + 14u]), asfloat(SceneArena[word + 15u]));
    result.frameSlot = SceneArena[word + 16u];
    result.flags = SceneArena[word + 17u];
    result.reserved0 = SceneArena[word + 18u];
    result.reserved1 = SceneArena[word + 19u];
    return result;
}
XRAdvancedInstanceRecord ReadXRAdvancedInstanceRecord(uint word)
{
    XRAdvancedInstanceRecord result;
    result.currentWorld = float4x4(asfloat(SceneArena[word + 0u]), asfloat(SceneArena[word + 1u]), asfloat(SceneArena[word + 2u]), asfloat(SceneArena[word + 3u]), asfloat(SceneArena[word + 4u]), asfloat(SceneArena[word + 5u]), asfloat(SceneArena[word + 6u]), asfloat(SceneArena[word + 7u]), asfloat(SceneArena[word + 8u]), asfloat(SceneArena[word + 9u]), asfloat(SceneArena[word + 10u]), asfloat(SceneArena[word + 11u]), asfloat(SceneArena[word + 12u]), asfloat(SceneArena[word + 13u]), asfloat(SceneArena[word + 14u]), asfloat(SceneArena[word + 15u]));
    result.previousWorld = float4x4(asfloat(SceneArena[word + 16u]), asfloat(SceneArena[word + 17u]), asfloat(SceneArena[word + 18u]), asfloat(SceneArena[word + 19u]), asfloat(SceneArena[word + 20u]), asfloat(SceneArena[word + 21u]), asfloat(SceneArena[word + 22u]), asfloat(SceneArena[word + 23u]), asfloat(SceneArena[word + 24u]), asfloat(SceneArena[word + 25u]), asfloat(SceneArena[word + 26u]), asfloat(SceneArena[word + 27u]), asfloat(SceneArena[word + 28u]), asfloat(SceneArena[word + 29u]), asfloat(SceneArena[word + 30u]), asfloat(SceneArena[word + 31u]));
    result.boundsSphere = asfloat(uint4(SceneArena[word + 32u], SceneArena[word + 33u], SceneArena[word + 34u], SceneArena[word + 35u]));
    result.boundsMin = asfloat(uint4(SceneArena[word + 36u], SceneArena[word + 37u], SceneArena[word + 38u], SceneArena[word + 39u]));
    result.boundsMax = asfloat(uint4(SceneArena[word + 40u], SceneArena[word + 41u], SceneArena[word + 42u], SceneArena[word + 43u]));
    result.animation = ReadXRAdvancedGpuHandle(word + 44u);
    result.deformation = ReadXRAdvancedGpuHandle(word + 46u);
    result.visibilityFlags = SceneArena[word + 48u];
    result.lodLevel = SceneArena[word + 49u];
    result.layerMask = SceneArena[word + 50u];
    result.renderPassMask = SceneArena[word + 51u];
    result.currentFrameSlot = SceneArena[word + 52u];
    result.previousFrameSlot = SceneArena[word + 53u];
    result.reserved0 = SceneArena[word + 54u];
    result.reserved1 = SceneArena[word + 55u];
    return result;
}
XRAdvancedGeometryRecord ReadXRAdvancedGeometryRecord(uint word)
{
    XRAdvancedGeometryRecord result;
    result.currentVertexData = ReadXRAdvancedBufferReference(word + 0u);
    result.previousVertexData = ReadXRAdvancedBufferReference(word + 8u);
    result.indexData = ReadXRAdvancedBufferReference(word + 16u);
    result.meshletDescriptors = ReadXRAdvancedBufferReference(word + 24u);
    result.meshletVertexIndices = ReadXRAdvancedBufferReference(word + 32u);
    result.meshletTriangleWords = ReadXRAdvancedBufferReference(word + 40u);
    result.fallbackGeometry = ReadXRAdvancedGpuHandle(word + 48u);
    result.vertexBase = SceneArena[word + 50u];
    result.vertexCount = SceneArena[word + 51u];
    result.indexBase = SceneArena[word + 52u];
    result.indexCount = SceneArena[word + 53u];
    result.meshletFirst = SceneArena[word + 54u];
    result.meshletCount = SceneArena[word + 55u];
    result.vertexLayoutId = uint2(SceneArena[word + 56u], SceneArena[word + 57u]);
    result.reservedLayout0 = SceneArena[word + 58u];
    result.reservedLayout1 = SceneArena[word + 59u];
    result.boundsSphere = asfloat(uint4(SceneArena[word + 60u], SceneArena[word + 61u], SceneArena[word + 62u], SceneArena[word + 63u]));
    result.boundsMin = asfloat(uint4(SceneArena[word + 64u], SceneArena[word + 65u], SceneArena[word + 66u], SceneArena[word + 67u]));
    result.boundsMax = asfloat(uint4(SceneArena[word + 68u], SceneArena[word + 69u], SceneArena[word + 70u], SceneArena[word + 71u]));
    result.materialSectionFirst = SceneArena[word + 72u];
    result.materialSectionCount = SceneArena[word + 73u];
    result.primitiveTopology = SceneArena[word + 74u];
    result.source = SceneArena[word + 75u];
    result.residency = SceneArena[word + 76u];
    result.missingBehavior = SceneArena[word + 77u];
    result.cookedLayoutVersion = SceneArena[word + 78u];
    result.flags = SceneArena[word + 79u];
    return result;
}
XRAdvancedMaterialRecord ReadXRAdvancedMaterialRecord(uint word)
{
    XRAdvancedMaterialRecord result;
    result.stableRowId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.shadingKernelId = SceneArena[word + 2u];
    result.shadingKernelGeneration = SceneArena[word + 3u];
    result.materialLayoutHash = uint2(SceneArena[word + 4u], SceneArena[word + 5u]);
    result.renderStateClass = SceneArena[word + 6u];
    result.coverageMode = SceneArena[word + 7u];
    result.requiredAttributeMask = SceneArena[word + 8u];
    result.textureReferenceOffset = SceneArena[word + 9u];
    result.textureReferenceCount = SceneArena[word + 10u];
    result.constantWordOffset = SceneArena[word + 11u];
    result.constantWordCount = SceneArena[word + 12u];
    result.featureFlags = SceneArena[word + 13u];
    result.eligibilityFlags = SceneArena[word + 14u];
    result.reserved = SceneArena[word + 15u];
    return result;
}
XRAdvancedShadingKernelRecord ReadXRAdvancedShadingKernelRecord(uint word)
{
    XRAdvancedShadingKernelRecord result;
    result.stableKernelId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.materialLayoutHash = uint2(SceneArena[word + 2u], SceneArena[word + 3u]);
    result.requiredAttributeMask = SceneArena[word + 4u];
    result.supportedCoverageMask = SceneArena[word + 5u];
    result.supportedEligibility = SceneArena[word + 6u];
    result.supportedFeatures = SceneArena[word + 7u];
    result.shaderIdentityHash = uint2(SceneArena[word + 8u], SceneArena[word + 9u]);
    result.renderStateClassMask = SceneArena[word + 10u];
    result.flags = SceneArena[word + 11u];
    result.reserved0 = SceneArena[word + 12u];
    result.reserved1 = SceneArena[word + 13u];
    result.reserved2 = SceneArena[word + 14u];
    result.reserved3 = SceneArena[word + 15u];
    return result;
}
XRAdvancedLightRecord ReadXRAdvancedLightRecord(uint word)
{
    XRAdvancedLightRecord result;
    result.stableLightId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.type = SceneArena[word + 2u];
    result.flags = SceneArena[word + 3u];
    result.positionAndRadius = asfloat(uint4(SceneArena[word + 4u], SceneArena[word + 5u], SceneArena[word + 6u], SceneArena[word + 7u]));
    result.directionAndOuterCone = asfloat(uint4(SceneArena[word + 8u], SceneArena[word + 9u], SceneArena[word + 10u], SceneArena[word + 11u]));
    result.colorAndIntensity = asfloat(uint4(SceneArena[word + 12u], SceneArena[word + 13u], SceneArena[word + 14u], SceneArena[word + 15u]));
    result.shapeAndInnerCone = asfloat(uint4(SceneArena[word + 16u], SceneArena[word + 17u], SceneArena[word + 18u], SceneArena[word + 19u]));
    result.cookieTexture = ReadXRAdvancedTextureReference(word + 20u);
    result.shadowRecord = ReadXRAdvancedGpuHandle(word + 24u);
    result.layerMask = SceneArena[word + 26u];
    result.viewMaskLo = SceneArena[word + 27u];
    result.viewMaskHi = SceneArena[word + 28u];
    result.reserved0 = SceneArena[word + 29u];
    result.reserved1 = SceneArena[word + 30u];
    result.reserved2 = SceneArena[word + 31u];
    return result;
}
XRAdvancedProbeRecord ReadXRAdvancedProbeRecord(uint word)
{
    XRAdvancedProbeRecord result;
    result.stableProbeId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.type = SceneArena[word + 2u];
    result.flags = SceneArena[word + 3u];
    result.positionAndRadius = asfloat(uint4(SceneArena[word + 4u], SceneArena[word + 5u], SceneArena[word + 6u], SceneArena[word + 7u]));
    result.influenceInner = asfloat(uint4(SceneArena[word + 8u], SceneArena[word + 9u], SceneArena[word + 10u], SceneArena[word + 11u]));
    result.influenceOuter = asfloat(uint4(SceneArena[word + 12u], SceneArena[word + 13u], SceneArena[word + 14u], SceneArena[word + 15u]));
    result.influenceOffsetAndShape = asfloat(uint4(SceneArena[word + 16u], SceneArena[word + 17u], SceneArena[word + 18u], SceneArena[word + 19u]));
    result.proxyCenterAndEnable = asfloat(uint4(SceneArena[word + 20u], SceneArena[word + 21u], SceneArena[word + 22u], SceneArena[word + 23u]));
    result.proxyHalfExtents = asfloat(uint4(SceneArena[word + 24u], SceneArena[word + 25u], SceneArena[word + 26u], SceneArena[word + 27u]));
    result.proxyRotation = asfloat(uint4(SceneArena[word + 28u], SceneArena[word + 29u], SceneArena[word + 30u], SceneArena[word + 31u]));
    result.irradiance = ReadXRAdvancedTextureReference(word + 32u);
    result.prefilteredRadiance = ReadXRAdvancedTextureReference(word + 36u);
    result.viewMaskLo = SceneArena[word + 40u];
    result.viewMaskHi = SceneArena[word + 41u];
    result.priority = SceneArena[word + 42u];
    result.reserved = SceneArena[word + 43u];
    return result;
}
XRAdvancedDecalRecord ReadXRAdvancedDecalRecord(uint word)
{
    XRAdvancedDecalRecord result;
    result.identity = ReadXRAdvancedGpuHandle(word + 0u);
    result.material = ReadXRAdvancedGpuHandle(word + 2u);
    result.flags = SceneArena[word + 4u];
    result.viewMaskLo = SceneArena[word + 5u];
    result.viewMaskHi = SceneArena[word + 6u];
    result.layerMask = SceneArena[word + 7u];
    result.worldToDecal = float4x4(asfloat(SceneArena[word + 8u]), asfloat(SceneArena[word + 9u]), asfloat(SceneArena[word + 10u]), asfloat(SceneArena[word + 11u]), asfloat(SceneArena[word + 12u]), asfloat(SceneArena[word + 13u]), asfloat(SceneArena[word + 14u]), asfloat(SceneArena[word + 15u]), asfloat(SceneArena[word + 16u]), asfloat(SceneArena[word + 17u]), asfloat(SceneArena[word + 18u]), asfloat(SceneArena[word + 19u]), asfloat(SceneArena[word + 20u]), asfloat(SceneArena[word + 21u]), asfloat(SceneArena[word + 22u]), asfloat(SceneArena[word + 23u]));
    result.previousWorldToDecal = float4x4(asfloat(SceneArena[word + 24u]), asfloat(SceneArena[word + 25u]), asfloat(SceneArena[word + 26u]), asfloat(SceneArena[word + 27u]), asfloat(SceneArena[word + 28u]), asfloat(SceneArena[word + 29u]), asfloat(SceneArena[word + 30u]), asfloat(SceneArena[word + 31u]), asfloat(SceneArena[word + 32u]), asfloat(SceneArena[word + 33u]), asfloat(SceneArena[word + 34u]), asfloat(SceneArena[word + 35u]), asfloat(SceneArena[word + 36u]), asfloat(SceneArena[word + 37u]), asfloat(SceneArena[word + 38u]), asfloat(SceneArena[word + 39u]));
    result.halfExtentsAndFade = asfloat(uint4(SceneArena[word + 40u], SceneArena[word + 41u], SceneArena[word + 42u], SceneArena[word + 43u]));
    result.maskTexture = ReadXRAdvancedTextureReference(word + 44u);
    return result;
}
XRAdvancedShadowRecord ReadXRAdvancedShadowRecord(uint word)
{
    XRAdvancedShadowRecord result;
    result.stableShadowId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.type = SceneArena[word + 2u];
    result.flags = SceneArena[word + 3u];
    result.textureReference = ReadXRAdvancedTextureReference(word + 4u);
    result.worldToShadow = float4x4(asfloat(SceneArena[word + 8u]), asfloat(SceneArena[word + 9u]), asfloat(SceneArena[word + 10u]), asfloat(SceneArena[word + 11u]), asfloat(SceneArena[word + 12u]), asfloat(SceneArena[word + 13u]), asfloat(SceneArena[word + 14u]), asfloat(SceneArena[word + 15u]), asfloat(SceneArena[word + 16u]), asfloat(SceneArena[word + 17u]), asfloat(SceneArena[word + 18u]), asfloat(SceneArena[word + 19u]), asfloat(SceneArena[word + 20u]), asfloat(SceneArena[word + 21u]), asfloat(SceneArena[word + 22u]), asfloat(SceneArena[word + 23u]));
    result.previousWorldToShadow = float4x4(asfloat(SceneArena[word + 24u]), asfloat(SceneArena[word + 25u]), asfloat(SceneArena[word + 26u]), asfloat(SceneArena[word + 27u]), asfloat(SceneArena[word + 28u]), asfloat(SceneArena[word + 29u]), asfloat(SceneArena[word + 30u]), asfloat(SceneArena[word + 31u]), asfloat(SceneArena[word + 32u]), asfloat(SceneArena[word + 33u]), asfloat(SceneArena[word + 34u]), asfloat(SceneArena[word + 35u]), asfloat(SceneArena[word + 36u]), asfloat(SceneArena[word + 37u]), asfloat(SceneArena[word + 38u]), asfloat(SceneArena[word + 39u]));
    result.uvScaleBias = asfloat(uint4(SceneArena[word + 40u], SceneArena[word + 41u], SceneArena[word + 42u], SceneArena[word + 43u]));
    result.depthBiasAndFilter = asfloat(uint4(SceneArena[word + 44u], SceneArena[word + 45u], SceneArena[word + 46u], SceneArena[word + 47u]));
    result.textureLayer = SceneArena[word + 48u];
    result.encoding = SceneArena[word + 49u];
    result.cascadeOffset = SceneArena[word + 50u];
    result.cascadeCount = SceneArena[word + 51u];
    result.viewMaskLo = SceneArena[word + 52u];
    result.viewMaskHi = SceneArena[word + 53u];
    result.lastRenderedFrameLo = SceneArena[word + 54u];
    result.lastRenderedFrameHi = SceneArena[word + 55u];
    result.momentParameters = asfloat(uint4(SceneArena[word + 56u], SceneArena[word + 57u], SceneArena[word + 58u], SceneArena[word + 59u]));
    result.depthRangeAndCascade = asfloat(uint4(SceneArena[word + 60u], SceneArena[word + 61u], SceneArena[word + 62u], SceneArena[word + 63u]));
    result.renderedLightPositionAndFar = asfloat(uint4(SceneArena[word + 64u], SceneArena[word + 65u], SceneArena[word + 66u], SceneArena[word + 67u]));
    return result;
}
XRAdvancedTextureRecord ReadXRAdvancedTextureRecord(uint word)
{
    XRAdvancedTextureRecord result;
    result.stableTextureId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.dimension = SceneArena[word + 2u];
    result.flags = SceneArena[word + 3u];
    result.width = SceneArena[word + 4u];
    result.height = SceneArena[word + 5u];
    result.depthOrLayers = SceneArena[word + 6u];
    result.mipCount = SceneArena[word + 7u];
    result.formatClass = SceneArena[word + 8u];
    result.encodedReferenceIndex = SceneArena[word + 9u];
    result.defaultSampler = ReadXRAdvancedGpuHandle(word + 10u);
    result.uvScaleBias = asfloat(uint4(SceneArena[word + 12u], SceneArena[word + 13u], SceneArena[word + 14u], SceneArena[word + 15u]));
    return result;
}
XRAdvancedSamplerRecord ReadXRAdvancedSamplerRecord(uint word)
{
    XRAdvancedSamplerRecord result;
    result.stableSamplerId = SceneArena[word + 0u];
    result.generation = SceneArena[word + 1u];
    result.filterMode = SceneArena[word + 2u];
    result.flags = SceneArena[word + 3u];
    result.addressU = SceneArena[word + 4u];
    result.addressV = SceneArena[word + 5u];
    result.addressW = SceneArena[word + 6u];
    result.compareOperation = SceneArena[word + 7u];
    result.lodBiasMinMaxAnisotropy = asfloat(uint4(SceneArena[word + 8u], SceneArena[word + 9u], SceneArena[word + 10u], SceneArena[word + 11u]));
    result.borderColor = asfloat(uint4(SceneArena[word + 12u], SceneArena[word + 13u], SceneArena[word + 14u], SceneArena[word + 15u]));
    return result;
}
XRAdvancedDrawRecord XR_ADV_LoadDraw(uint dense) { return ReadXRAdvancedDrawRecord(SceneRow(0u, dense)); }
XRAdvancedInstanceRecord XR_ADV_LoadInstance(uint dense) { return ReadXRAdvancedInstanceRecord(SceneRow(1u, dense)); }
XRAdvancedGeometryRecord XR_ADV_LoadGeometry(uint dense) { return ReadXRAdvancedGeometryRecord(SceneRow(2u, dense)); }
XRAdvancedMaterialRecord XR_ADV_LoadMaterial(uint dense) { return ReadXRAdvancedMaterialRecord(SceneRow(3u, dense)); }
XRAdvancedLightRecord XR_ADV_LoadLight(uint dense) { return ReadXRAdvancedLightRecord(SceneRow(5u, dense)); }
XRAdvancedShadowRecord XR_ADV_LoadShadow(uint dense) { return ReadXRAdvancedShadowRecord(SceneRow(6u, dense)); }
XRAdvancedTextureRecord XR_ADV_LoadTexture(uint dense) { return ReadXRAdvancedTextureRecord(SceneRow(7u, dense)); }
XRAdvancedSamplerRecord XR_ADV_LoadSampler(uint dense) { return ReadXRAdvancedSamplerRecord(SceneRow(8u, dense)); }
XRAdvancedProbeRecord XR_ADV_LoadProbe(uint dense) { return ReadXRAdvancedProbeRecord(SceneRow(13u, dense)); }
XRAdvancedDecalRecord XR_ADV_LoadDecal(uint dense) { return ReadXRAdvancedDecalRecord(SceneRow(15u, dense)); }
XRAdvancedTransformRecord XR_ADV_LoadTransform(uint dense) { return ReadXRAdvancedTransformRecord(SceneRow(17u, dense)); }
XRAdvancedShadingKernelRecord XR_ADV_LoadShadingKernel(uint dense) { return ReadXRAdvancedShadingKernelRecord(SceneRow(21u, dense)); }
XRAdvancedVisibilityPackedVertex ReadXRAdvancedVisibilityPackedVertex(uint word)
{
    XRAdvancedVisibilityPackedVertex result;
    result.position = asfloat(uint3(GeometryArena[word + 0u], GeometryArena[word + 1u], GeometryArena[word + 2u]));
    result.normalOct = GeometryArena[word + 3u];
    result.tangentOctAndSign = GeometryArena[word + 4u];
    result.texCoord0Half = GeometryArena[word + 5u];
    result.texCoord1Half = GeometryArena[word + 6u];
    result.color0Rgba8 = GeometryArena[word + 7u];
    result.color1Rgba8 = GeometryArena[word + 8u];
    result.custom0 = GeometryArena[word + 9u];
    result.sourceVertex = GeometryArena[word + 10u];
    result.flags = GeometryArena[word + 11u];
    result.reserved0 = GeometryArena[word + 12u];
    result.reserved1 = GeometryArena[word + 13u];
    result.reserved2 = GeometryArena[word + 14u];
    result.reserved3 = GeometryArena[word + 15u];
    return result;
}
XRAdvancedVisibilityMeshletDescriptor ReadXRAdvancedVisibilityMeshletDescriptor(uint word)
{
    XRAdvancedVisibilityMeshletDescriptor result;
    result.boundsSphere = asfloat(uint4(GeometryArena[word + 0u], GeometryArena[word + 1u], GeometryArena[word + 2u], GeometryArena[word + 3u]));
    result.vertexOffset = GeometryArena[word + 4u];
    result.triangleByteOffset = GeometryArena[word + 5u];
    result.vertexCount = GeometryArena[word + 6u];
    result.triangleCount = GeometryArena[word + 7u];
    result.cone = asfloat(uint4(GeometryArena[word + 8u], GeometryArena[word + 9u], GeometryArena[word + 10u], GeometryArena[word + 11u]));
    result.coneApex = asfloat(uint4(GeometryArena[word + 12u], GeometryArena[word + 13u], GeometryArena[word + 14u], GeometryArena[word + 15u]));
    result.packedCone = GeometryArena[word + 16u];
    result.reserved0 = GeometryArena[word + 17u];
    result.reserved1 = GeometryArena[word + 18u];
    result.reserved2 = GeometryArena[word + 19u];
    return result;
}
XRAdvancedGpuHandle PreparedXRAdvancedGpuHandle(uint word)
{
    XRAdvancedGpuHandle result;
    result.index = PreparedDrawDeformations[word + 0u];
    result.generation = PreparedDrawDeformations[word + 1u];
    return result;
}
XRAdvancedPreparedDrawDeformationRecord PreparedXRAdvancedPreparedDrawDeformationRecord(uint word)
{
    XRAdvancedPreparedDrawDeformationRecord result;
    result.geometry = PreparedXRAdvancedGpuHandle(word + 0u);
    result.deformation = PreparedXRAdvancedGpuHandle(word + 2u);
    result.currentVertexOffset = PreparedDrawDeformations[word + 4u];
    result.previousVertexOffset = PreparedDrawDeformations[word + 5u];
    result.vertexCount = PreparedDrawDeformations[word + 6u];
    result.flags = PreparedDrawDeformations[word + 7u];
    return result;
}
XRAdvancedViewRecord XR_ADV_LoadView(uint index)
{
    XRAdvancedViewRecord result = FrozenView;
    result.view = transpose(FrozenView.view);
    result.projectionJittered = transpose(FrozenView.projectionJittered);
    result.projectionUnjittered = transpose(FrozenView.projectionUnjittered);
    result.viewProjectionJittered = transpose(FrozenView.viewProjectionJittered);
    result.viewProjectionUnjittered = transpose(FrozenView.viewProjectionUnjittered);
    result.previousView = transpose(FrozenView.previousView);
    result.previousProjectionJittered = transpose(FrozenView.previousProjectionJittered);
    result.previousProjectionUnjittered = transpose(FrozenView.previousProjectionUnjittered);
    result.previousViewProjectionJittered = transpose(FrozenView.previousViewProjectionJittered);
    result.previousViewProjectionUnjittered = transpose(FrozenView.previousViewProjectionUnjittered);
    result.inverseViewProjectionJittered = transpose(FrozenView.inverseViewProjectionJittered);
    result.inverseViewProjectionUnjittered = transpose(FrozenView.inverseViewProjectionUnjittered);
    return result;
}
uint XR_ADV_LoadMaterialConstant(XRAdvancedMaterialRecord material, uint relative) { return SceneArena[SceneRow(11u, material.constantWordOffset + relative)]; }
XRAdvancedMaterialTextureBinding XR_ADV_LoadMaterialTextureBinding(XRAdvancedMaterialRecord material, uint relative) { return ReadXRAdvancedMaterialTextureBinding(SceneRow(12u, material.textureReferenceOffset + relative)); }
float2 unpackHalf(uint value) { return float2(f16tof32(value & 65535u), f16tof32(value >> 16u)); }
bool XR_ADV_IsValidHandle(XRAdvancedGpuHandle handle)
{
    return handle.index != 0u && handle.generation != 0u;
}
bool XR_ADV_HandleMatches(
    XRAdvancedGpuHandle handle,
    uint stableId,
    uint generation)
{
    return handle.index == stableId && handle.generation == generation;
}
float4 XR_ADV_TransformRowVector(float4 value, float4x4 m) { return mul(value, m); }
float3 XR_ADV_TransformPositionRowVector(float3 value, float4x4 m) { return mul(float4(value, 1.0), m).xyz; }
float3 XR_ADV_TransformDirectionRowVector(float3 value, float4x4 m) { return mul(float4(value, 0.0), m).xyz; }
bool XR_ADV_GeometryIsResident(XRAdvancedGeometryRecord geometryRecord)
{
    return geometryRecord.residency == 2u;
}
static const uint XR_ADV_VIEW_DEPTH_ZERO_TO_ONE = 1u << 0u;
static const uint XR_ADV_VIEW_FRAMEBUFFER_TEXTURE_Y_DOWN = 1u << 6u;
static const uint XR_ADV_VIEW_TEMPORAL_HISTORY_VALID = 1u << 7u;
static const uint XR_ADV_VIEW_EXCLUDE_PROJECTIVE_MIRRORS = 1u << 8u;

float XR_ADV_FoveationDerivativeScale(XRAdvancedViewRecord viewRecord, float2 targetUv)
{
    if (viewRecord.foveationCenterAndBias.w == 0.0)
        return 1.0;
    float2 delta = (targetUv - viewRecord.foveationCenterAndBias.xy) * 2.0;
    float eccentricity = length(delta);
    float guardRadius = viewRecord.foveationRadii.y;
    float outerRadius = viewRecord.foveationRadii.w;
    float peripheral = smoothstep(guardRadius, outerRadius, eccentricity);
    return exp2(peripheral * viewRecord.foveationCenterAndBias.z);
}
float2 XR_ADV_ClipNdcToFramebufferUv(
    XRAdvancedViewRecord viewRecord,
    float2 clipNdc)
{
    float2 framebufferUv = clipNdc * 0.5 + 0.5;
    if ((viewRecord.flags & XR_ADV_VIEW_FRAMEBUFFER_TEXTURE_Y_DOWN) != 0u)
        framebufferUv.y = 1.0 - framebufferUv.y;
    return framebufferUv;
}
float2 XR_ADV_FramebufferUvToClipNdc(
    XRAdvancedViewRecord viewRecord,
    float2 framebufferUv)
{
    if ((viewRecord.flags & XR_ADV_VIEW_FRAMEBUFFER_TEXTURE_Y_DOWN) != 0u)
        framebufferUv.y = 1.0 - framebufferUv.y;
    return framebufferUv * 2.0 - 1.0;
}
float4 XR_ADV_CurrentJitteredClip(
    XRAdvancedViewRecord viewRecord,
    float3 worldPosition)
{
    return XR_ADV_TransformRowVector(
        float4(worldPosition, 1.0),
        viewRecord.viewProjectionJittered);
}
#define XR_ADV_VIS_INVALID 0xFFFFFFFFu
#define XR_ADV_VIS_PRODUCER_STATIC_MESHLET 0u
#define XR_ADV_VIS_PRODUCER_SKINNED_MESHLET 1u
#define XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED 2u
#define XR_ADV_VIS_PRODUCER_CPU_STATIC_INDEXED 3u
#define XR_ADV_VIS_PRODUCER_CPU_PRE_SKINNED 4u

#define XR_ADV_VIS_PRODUCER_MASK 0x7u
#define XR_ADV_VIS_ORIGIN_SHIFT 3u
#define XR_ADV_VIS_MASKED_SHIFT 4u
#define XR_ADV_VIS_FRONT_FACE_SHIFT 5u
#define XR_ADV_VIS_VELOCITY_VALID_SHIFT 6u
#define XR_ADV_VIS_VIEW_SHIFT 8u
#define XR_ADV_VIS_VERSION_SHIFT 16u
#define XR_ADV_VIS_SELECTION_VALID_SHIFT 24u
#define XR_ADV_VERTEX_HAS_TEXCOORD1 0x00000001u

bool XR_ADV_IsSupportedVisibilityProducer(uint producer)
{
    return producer <=
        XR_ADV_VIS_PRODUCER_CPU_PRE_SKINNED;
}

#define XR_ADV_PREPARED_DEFORMATION_ACTIVE 1u
#define XR_ADV_PREPARED_DEFORMATION_PREVIOUS_VALID 2u
#define XR_ADV_PREPARED_DEFORMATION_TEMPORAL_STATE_PRESENT 4u
#define XR_ADV_PREPARED_DEFORMATION_TEMPORAL_REASON_SHIFT 16u
#define XR_ADV_PREPARED_DEFORMATION_TEMPORAL_REASON_MASK 0x000F0000u

bool XR_ADV_PreparedDeformationActive(
    XRAdvancedPreparedDrawDeformationRecord record)
{
    return (record.flags & XR_ADV_PREPARED_DEFORMATION_ACTIVE) != 0u;
}
bool XR_ADV_PreparedDeformationPreviousValid(
    XRAdvancedPreparedDrawDeformationRecord record)
{
    return (record.flags &
        XR_ADV_PREPARED_DEFORMATION_PREVIOUS_VALID) != 0u;
}
bool XR_ADV_PreparedDeformationTemporalStatePresent(
    XRAdvancedPreparedDrawDeformationRecord record)
{
    return (record.flags &
        XR_ADV_PREPARED_DEFORMATION_TEMPORAL_STATE_PRESENT) != 0u;
}
uint XR_ADV_PreparedDeformationTemporalReason(
    XRAdvancedPreparedDrawDeformationRecord record)
{
    return (record.flags &
        XR_ADV_PREPARED_DEFORMATION_TEMPORAL_REASON_MASK) >>
        XR_ADV_PREPARED_DEFORMATION_TEMPORAL_REASON_SHIFT;
}
bool XR_ADV_TryLoadPreparedDrawTemporalState(
    uint drawDense,
    XRAdvancedDrawRecord draw,
    out XRAdvancedPreparedDrawDeformationRecord record)
{
    record = XRAdvancedPreparedDrawDeformationRecord(
        XRAdvancedGpuHandle(0u, 0u),
        XRAdvancedGpuHandle(0u, 0u),
        0u, 0u, 0u, 0u);
    if (drawDense >=
        PreparedCount())
    {
        return false;
    }

    record = PreparedXRAdvancedPreparedDrawDeformationRecord(drawDense * 8u);
    return XR_ADV_PreparedDeformationTemporalStatePresent(record) &&
        record.geometry.index == draw.geometry.index &&
        record.geometry.generation == draw.geometry.generation &&
        record.deformation.index == draw.deformation.index &&
        record.deformation.generation == draw.deformation.generation &&
        record.vertexCount != 0u;
}
bool XR_ADV_TryLoadPreparedDrawDeformation(
    uint drawDense,
    XRAdvancedDrawRecord draw,
    out XRAdvancedPreparedDrawDeformationRecord record)
{
    return XR_ADV_TryLoadPreparedDrawTemporalState(
            drawDense,
            draw,
            record) &&
        XR_ADV_PreparedDeformationActive(record);
}
bool XR_ADV_TryResolvePixelMaterial(uint2 identity, uint metadata,
    out uint materialDense, out uint kernelDense)
{
    materialDense = XR_ADV_INVALID_DENSE_INDEX;
    kernelDense = XR_ADV_INVALID_DENSE_INDEX;
    if (identity.x == 0u || identity.x == XR_ADV_VIS_INVALID ||
        identity.y == XR_ADV_VIS_INVALID || metadata == XR_ADV_VIS_INVALID ||
        ((metadata >> XR_ADV_VIS_VERSION_SHIFT) & 0xFFu) != XR_ADV_VISIBILITY_PAYLOAD_VERSION ||
        !XR_ADV_IsSupportedVisibilityProducer(metadata & XR_ADV_VIS_PRODUCER_MASK))
        return false;
    uint drawDense = XR_ADV_ResolveVisibilityStableIndex(identity.x,
        VisibilityDrawLookupSegment, XR_ADV_DIAGNOSTIC_DRAW);
    if (drawDense == XR_ADV_INVALID_DENSE_INDEX || drawDense >= SceneCount(0u))
        return false;
    XRAdvancedDrawRecord draw = XR_ADV_LoadDraw(drawDense);
    materialDense = XR_ADV_ResolveVisibilityHandle(draw.material,
        VisibilityMaterialLookupSegment, XR_ADV_DIAGNOSTIC_MATERIAL);
    if (materialDense == XR_ADV_INVALID_DENSE_INDEX || materialDense >= SceneCount(3u))
        return false;
    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(materialDense);
    kernelDense = XR_ADV_ResolveVisibilityHandle(
        XRAdvancedGpuHandle(material.shadingKernelId, material.shadingKernelGeneration),
        VisibilityShadingKernelLookupSegment, XR_ADV_DIAGNOSTIC_MATERIAL);
    if (kernelDense == XR_ADV_INVALID_DENSE_INDEX || kernelDense >= SceneCount(21u))
        return false;
    XRAdvancedShadingKernelRecord kernel = XR_ADV_LoadShadingKernel(kernelDense);
    return all(equal(kernel.materialLayoutHash, material.materialLayoutHash)) &&
        material.coverageMode < 32u &&
        (kernel.supportedCoverageMask & (1u << material.coverageMode)) != 0u;
}
bool2 equal(float2 a, float2 b) { return a == b; }
bool2 greaterThanEqual(float2 a, float2 b) { return a >= b; }
bool2 lessThanEqual(float2 a, float2 b) { return a <= b; }
bool2 lessThan(float2 a, float2 b) { return a < b; }
bool2 greaterThan(float2 a, float2 b) { return a > b; }
bool3 equal(float3 a, float3 b) { return a == b; }
bool3 greaterThanEqual(float3 a, float3 b) { return a >= b; }
bool3 lessThanEqual(float3 a, float3 b) { return a <= b; }
bool3 lessThan(float3 a, float3 b) { return a < b; }
bool3 greaterThan(float3 a, float3 b) { return a > b; }
bool4 equal(float4 a, float4 b) { return a == b; }
bool4 greaterThanEqual(float4 a, float4 b) { return a >= b; }
bool4 lessThanEqual(float4 a, float4 b) { return a <= b; }
bool4 lessThan(float4 a, float4 b) { return a < b; }
bool4 greaterThan(float4 a, float4 b) { return a > b; }
bool2 equal(uint2 a, uint2 b) { return a == b; }
bool2 greaterThanEqual(uint2 a, uint2 b) { return a >= b; }
bool2 lessThanEqual(uint2 a, uint2 b) { return a <= b; }
bool2 lessThan(uint2 a, uint2 b) { return a < b; }
bool2 greaterThan(uint2 a, uint2 b) { return a > b; }
bool3 equal(uint3 a, uint3 b) { return a == b; }
bool3 greaterThanEqual(uint3 a, uint3 b) { return a >= b; }
bool3 lessThanEqual(uint3 a, uint3 b) { return a <= b; }
bool3 lessThan(uint3 a, uint3 b) { return a < b; }
bool3 greaterThan(uint3 a, uint3 b) { return a > b; }
bool4 equal(uint4 a, uint4 b) { return a == b; }
bool4 greaterThanEqual(uint4 a, uint4 b) { return a >= b; }
bool4 lessThanEqual(uint4 a, uint4 b) { return a <= b; }
bool4 lessThan(uint4 a, uint4 b) { return a < b; }
bool4 greaterThan(uint4 a, uint4 b) { return a > b; }
bool2 equal(int2 a, int2 b) { return a == b; }
bool2 greaterThanEqual(int2 a, int2 b) { return a >= b; }
bool2 lessThanEqual(int2 a, int2 b) { return a <= b; }
bool2 lessThan(int2 a, int2 b) { return a < b; }
bool2 greaterThan(int2 a, int2 b) { return a > b; }
bool3 equal(int3 a, int3 b) { return a == b; }
bool3 greaterThanEqual(int3 a, int3 b) { return a >= b; }
bool3 lessThanEqual(int3 a, int3 b) { return a <= b; }
bool3 lessThan(int3 a, int3 b) { return a < b; }
bool3 greaterThan(int3 a, int3 b) { return a > b; }
bool4 equal(int4 a, int4 b) { return a == b; }
bool4 greaterThanEqual(int4 a, int4 b) { return a >= b; }
bool4 lessThanEqual(int4 a, int4 b) { return a <= b; }
bool4 lessThan(int4 a, int4 b) { return a < b; }
bool4 greaterThan(int4 a, int4 b) { return a > b; }
#define XR_ADV_ATTRIBUTE_POSITION 0x00000001u
#define XR_ADV_ATTRIBUTE_NORMAL 0x00000002u
#define XR_ADV_ATTRIBUTE_TANGENT 0x00000004u
#define XR_ADV_ATTRIBUTE_TEXCOORD0 0x00000008u
#define XR_ADV_ATTRIBUTE_TEXCOORD1 0x00000010u
#define XR_ADV_ATTRIBUTE_COLOR0 0x00000020u
#define XR_ADV_ATTRIBUTE_JOINT_INDICES 0x00000040u
#define XR_ADV_ATTRIBUTE_JOINT_WEIGHTS 0x00000080u
#define XR_ADV_ATTRIBUTE_CUSTOM0 0x00000100u
#define XR_ADV_ATTRIBUTE_FLAT 0x00000200u
#define XR_ADV_ATTRIBUTE_DEFORMED_POSITION 0x00000400u
#define XR_ADV_ATTRIBUTE_ANALYTICAL_DERIVATIVES 0x00000800u
#define XR_ADV_ATTRIBUTE_COLOR1 0x00001000u

#ifndef XR_ADV_REQUIRED_ATTRIBUTE_MASK
#define XR_ADV_REQUIRED_ATTRIBUTE_MASK 0xFFFFFFFFu
#endif

#define XR_ADV_SURFACE_VALID 0x00000001u
#define XR_ADV_SURFACE_BACK_FACING 0x00000002u
#define XR_ADV_SURFACE_FLAT_ATTRIBUTES 0x00000004u
#define XR_ADV_SURFACE_DEFORMED 0x00000008u
#define XR_ADV_SURFACE_DERIVATIVES_VALID 0x00000010u
#define XR_ADV_SURFACE_VELOCITY_VALID 0x00000020u
#define XR_ADV_SURFACE_REACTIVE 0x00000040u
#define XR_ADV_SURFACE_MASKED_EDGE 0x00000080u
#define XR_ADV_SURFACE_MIRRORED_TRANSFORM 0x00000100u
#define XR_ADV_SURFACE_CONSERVATIVE_MIP 0x00000200u
#define XR_ADV_TEMPORAL_VELOCITY_REASON_SHIFT 16u
#define XR_ADV_TEMPORAL_VELOCITY_REASON_MASK 0x000F0000u


#define XR_ADV_RECONSTRUCTION_OK 0u
#define XR_ADV_RECONSTRUCTION_BACKGROUND 1u
#define XR_ADV_RECONSTRUCTION_PAYLOAD_VERSION 2u
#define XR_ADV_RECONSTRUCTION_DRAW_NOT_RESIDENT 3u
#define XR_ADV_RECONSTRUCTION_STALE_GENERATION 4u
#define XR_ADV_RECONSTRUCTION_GEOMETRY_MISSING 5u
#define XR_ADV_RECONSTRUCTION_GEOMETRY_NOT_RESIDENT 6u
#define XR_ADV_RECONSTRUCTION_PRIMITIVE_OUT_OF_RANGE 7u
#define XR_ADV_RECONSTRUCTION_MATERIAL_NOT_RESIDENT 8u
#define XR_ADV_RECONSTRUCTION_KERNEL_NOT_RESIDENT 9u
#define XR_ADV_RECONSTRUCTION_VIEW_OUT_OF_RANGE 10u
#define XR_ADV_RECONSTRUCTION_DEGENERATE_TRIANGLE 11u
#define XR_ADV_RECONSTRUCTION_NON_FINITE 12u
#define XR_ADV_RECONSTRUCTION_CURRENT_GEOMETRY_MISSING 13u
#define XR_ADV_RECONSTRUCTION_PREVIOUS_GEOMETRY_MISSING 14u
#define XR_ADV_RECONSTRUCTION_TEXCOORD1_MISSING 15u

XRAdvancedSurface XR_ADV_InvalidSurface(
    uint2 identity,
    uint reason)
{
    XRAdvancedSurface surface = (XRAdvancedSurface)0;
    surface.identity = identity;
    surface.producer = 0u;
    surface.viewIndex = 0u;
    surface.materialDenseIndex = 0u;
    surface.shadingKernelDenseIndex = 0u;
    surface.primitiveSection = 0u;
    surface.layerMask = 0u;
    surface.requiredAttributeMask = 0u;
    surface.worldPosition = float3(0.0);
    surface.interpolatedWorldPosition = float3(0.0);
    surface.visibilityMetadata = 0u;
    surface.samplePixelPosition = float2(0.0);
    surface.geometricNormal = float3(0.0, 1.0, 0.0);
    surface.shadingNormal = surface.geometricNormal;
    surface.tangent = float3(1.0, 0.0, 0.0);
    surface.bitangent = float3(0.0, 0.0, 1.0);
    surface.texCoord0 = float2(0.0);
    surface.texCoord1 = float2(0.0);
    surface.color0 = float4(1.0);
    surface.color1 = float4(1.0);
    surface.custom0 = 0u;
    surface.barycentrics = float3(0.0);
    surface.barycentricDx = float3(0.0);
    surface.barycentricDy = float3(0.0);
    surface.texCoord0Dx = float2(0.0);
    surface.texCoord0Dy = float2(0.0);
    surface.texCoord1Dx = float2(0.0);
    surface.texCoord1Dy = float2(0.0);
    surface.currentUnjitteredClip = float4(0.0);
    surface.previousUnjitteredClip = float4(0.0);
    surface.motionNdc = float2(0.0);
    surface.depth = 1.0;
    surface.tangentHandedness = 1.0;
    surface.flags = 0u;
    surface.invalidReason = reason;
    return surface;
}
bool XR_ADV_SurfaceHasAttribute(
    XRAdvancedSurface surface,
    uint attributeMask)
{
    return (surface.requiredAttributeMask & attributeMask) != 0u;
}
