// Browser standalone producers publish depth, projected R16F, and radial R16F
// without conversion. This receiver preserves the authored 8/8 Vogel PCSS
// controls. Same-triangle helper samples preserve the complete biased receiver
// coordinate derivatives, including mapped normals and varying spot offsets.
#if !defined(XR_ADV_STANDALONE_SHADOW_SCHEMA_VERSION) || XR_ADV_STANDALONE_SHADOW_SCHEMA_VERSION != 1
#error Native standalone shadow sampling requires schema version 1.
#endif

float2 XR_WEB_ShadowVogelTap(int index, float rotation)
{
    float sampleIndex = float(index) + 0.5;
    float angle = sampleIndex * 2.39996323 + rotation;
    return sqrt(sampleIndex / 8.0) * float2(cos(angle), sin(angle));
}

float XR_WEB_ShadowRotation(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) * 6.28318530718;
}

float3 XR_WEB_ShadowNormalize(float3 value)
{
    return value * rsqrt(max(dot(value, value), 1e-20));
}

static bool standaloneShadowHelpersReady = false;
static float3 standaloneShadowPositionX;
static float3 standaloneShadowPositionY;
static float3 standaloneShadowNormalX;
static float3 standaloneShadowNormalY;

bool XR_WEB_ReconstructShadowHelper(XRAdvancedSurface surface, float2 pixel,
    out float3 position, out float3 normal)
{
    XRAdvancedSurface helper;
    position = float3(0.0);
    normal = float3(0.0, 0.0, 1.0);
    // Extrapolate this primitive's own attributes across a helper lane. No
    // neighboring visibility/depth access, coverage test, or subgroup assumption.
    if (!XR_ADV_TryReconstructSurface(surface.identity, surface.visibilityMetadata,
        surface.depth, pixel, helper)) return false;
    helper.worldPosition = helper.interpolatedWorldPosition;
    position = helper.worldPosition;
    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(helper.materialDenseIndex);
    normal = normalize(helper.shadingNormal);
    if (XR_ADV_IsUberBaseMaterial(material))
    {
        uint word,features,pipeline;
        if(!XR_ADV_TryLoadUberBase(material,word,features,pipeline))return false;
        UberBaseMesh mesh=XR_ADV_UberMesh(helper);
        // Canonical Uber receiver shadows use the geometric vertex normal.
        normal=mesh.vertexNormal;
    }
    else if (XR_ADV_IsEngineSurfaceMaterial(material))
    {
        uint word, kind, roleFlags;
        if (!XR_ADV_TryLoadEngineSurface(material, word, kind, roleFlags)) return false;
        if (kind == XR_ADV_ENGINE_SURFACE_TEXTURE_NORMAL)
            normal = XR_ADV_EngineSurfaceMappedNormal(
                XR_ADV_SampleEngineSurfaceRole(word, XR_ADV_ENGINE_SURFACE_NORMAL_ROLE, helper).rgb, helper);
    }
    else if ((XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD) & 2u) != 0u &&
        XR_ADV_SurfaceHasAttribute(helper, XR_ADV_ATTRIBUTE_TANGENT))
    {
        float3 normalTex = XR_ADV_StandardTexture(material, 1u, helper.texCoord0,
            helper.texCoord0Dx, helper.texCoord0Dy, float4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
        normal = normalize(helper.tangent * normalTex.x + helper.bitangent * normalTex.y + normal * normalTex.z);
    }
    XR_ADV_ApplySelectedDecalReceiverNormals(helper, normal);
    return XR_ADV_IsFinite(position) && XR_ADV_IsFinite(normal);
}

float3 XR_WEB_ShadowCoordinate(XRAdvancedShadowRecord record, float3 position, float3 normal, float mapWidth)
{
    float normalOffset = max(record.depthRangeAndCascade.w, 0.0);
    if (record.type == 1u)
    {
        float3 forward = XR_WEB_ShadowNormalize(record.uvScaleBias.xyz);
        float cosOuter = clamp(record.uvScaleBias.w, 0.001, 0.999999);
        float tanOuter = sqrt(max(1.0 - cosOuter * cosOuter, 0.0)) / cosOuter;
        float axisDistance = max(dot(position - record.renderedLightPositionAndFar.xyz, forward), 0.0001);
        normalOffset = 2.0 * axisDistance * tanOuter / max(mapWidth, 1.0) * max(record.depthBiasAndFilter.z, 0.0);
    }
    float4 clip = XR_ADV_TransformRowVector(float4(position + normal * normalOffset, 1.0), record.worldToShadow);
    float3 ndc = clip.xyz / clip.w;
    return float3(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5, ndc.z);
}

float XR_WEB_ShadowPlaneBias(XRAdvancedShadowRecord record, XRAdvancedSurface surface,
    float3 coordinate, float footprint, float mapWidth)
{
    if (record.depthBiasAndFilter.y <= 0.0) return 0.0;
    float2 sign = float2((uint(surface.samplePixelPosition.x) & 1u) == 0u ? 1.0 : -1.0,
        (uint(surface.samplePixelPosition.y) & 1u) == 0u ? 1.0 : -1.0);
    if (!standaloneShadowHelpersReady)
    {
        // Slang out parameters lower to WGSL function-address-space pointers.
        // Publish into the private invocation cache only after both calls finish.
        float3 positionX, normalX, positionY, normalY;
        bool valid = true;
        // Keep one call site for the complete surface reconstruction instead
        // of duplicating it. Both helper lanes still execute in X-then-Y order.
        [loop]
        for (uint axis = 0u; axis < 2u; axis++)
        {
            float2 offset = axis == 0u ? float2(sign.x, 0.0) : float2(0.0, sign.y);
            float3 position, normal;
            bool helperValid = XR_WEB_ReconstructShadowHelper(surface,
                surface.samplePixelPosition + offset, position, normal);
            valid = valid && helperValid;
            if (axis == 0u) { positionX = position; normalX = normal; }
            else { positionY = position; normalY = normal; }
        }
        if (!valid) { textureFailure |= 1u << 16u; return 0.0; }
        standaloneShadowPositionX = positionX;
        standaloneShadowNormalX = normalX;
        standaloneShadowPositionY = positionY;
        standaloneShadowNormalY = normalY;
        standaloneShadowHelpersReady = true;
    }
    float3 coordinateDx = (XR_WEB_ShadowCoordinate(record, standaloneShadowPositionX, standaloneShadowNormalX, mapWidth) - coordinate) * sign.x;
    float3 coordinateDy = (XR_WEB_ShadowCoordinate(record, standaloneShadowPositionY, standaloneShadowNormalY, mapWidth) - coordinate) * sign.y;
    if (!XR_ADV_IsFinite(coordinateDx) || !XR_ADV_IsFinite(coordinateDy))
    { textureFailure |= 1u << 16u; return 0.0; }
    float2 uvDx = coordinateDx.xy;
    float2 uvDy = coordinateDy.xy;
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    if (abs(determinant) <= 1e-8) return 0.0;
    float2 gradient = float2(uvDy.y * coordinateDx.z - uvDx.y * coordinateDy.z,
        -uvDy.x * coordinateDx.z + uvDx.x * coordinateDy.z) / determinant;
    return dot(abs(gradient), float2(footprint)) * max(record.depthBiasAndFilter.y, 0.0);
}

float XR_WEB_SampleStandalonePoint(XRAdvancedShadowRecord record, XRAdvancedTextureRecord textureRecord,
    XRAdvancedEncodedTextureReference reference, XRAdvancedSurface surface, float3 normal)
{
    float directionTexel = 2.0 / max(float(textureRecord.width), 1.0);
    float3 lightToReceiver = surface.worldPosition - record.renderedLightPositionAndFar.xyz;
    float receiverDistance = length(lightToReceiver);
    float farZ = record.depthRangeAndCascade.y;
    float3 lightDirection = receiverDistance > 1e-6 ? -lightToReceiver / receiverDistance : float3(0.0, 0.0, 1.0);
    float noL = max(dot(normal, lightDirection), 0.0);
    float normalOffset = receiverDistance * directionTexel * max(record.depthBiasAndFilter.z, 0.0);
    float3 shadowDirection = surface.worldPosition + normal * normalOffset - record.renderedLightPositionAndFar.xyz;
    float distance = length(shadowDirection);
    if (receiverDistance >= farZ || distance >= farZ || distance <= record.depthRangeAndCascade.x + normalOffset)
        return 1.0;
    float noLSafe = max(noL, 0.05);
    float tanTheta = sqrt(max(1.0 - noLSafe * noLSafe, 0.0)) / noLSafe;
    float filterTexels = max(1.0, clamp(record.momentParameters.y * 256.0, 0.0, 8.0));
    float texelRelativeBias = directionTexel * (max(record.depthBiasAndFilter.x, 0.0) +
        max(record.depthBiasAndFilter.y, 0.0) * tanTheta * filterTexels);
    float receiverDepth = distance * (1.0 - max(texelRelativeBias, 1.0 / 512.0));
    float searchRadius = directionTexel * max(1.0, clamp(record.momentParameters.x * 256.0, 0.0, 4.0));
    float minRadius = directionTexel * max(1.0, clamp(record.momentParameters.z * 256.0, 0.0, 4.0));
    float maxRadius = directionTexel * max(1.0, clamp(record.momentParameters.w * 256.0, 0.0, 4.0));
    float3 direction = normalize(shadowDirection);
    float3 up = abs(direction.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    float3 tangent = normalize(cross(up, direction));
    float3 bitangent = cross(direction, tangent);
    float rotation = XR_WEB_ShadowRotation(surface.samplePixelPosition);
    float blockerSum = 0.0;
    int blockerCount = 0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float2 disk = XR_WEB_ShadowVogelTap(tap, rotation) * max(searchRadius, 0.000001);
        float3 sampleDirection = normalize(direction + tangent * disk.x + bitangent * disk.y);
        float depth = XR_ADV_SampleTextureCubeLod(reference, sampleDirection, 0.0).r * farZ;
        if (depth < receiverDepth) { blockerSum += depth; blockerCount++; }
    }
    if (blockerCount == 0) return 1.0;
    float averageBlocker = blockerSum / float(blockerCount);
    float angularSourceRadius = max(record.depthBiasAndFilter.w, 0.0) / max(averageBlocker, 0.0001);
    float penumbra = (receiverDepth - averageBlocker) / max(averageBlocker, 0.0001) * angularSourceRadius;
    float radius = clamp(penumbra, max(minRadius, 0.000001), max(maxRadius, minRadius));
    float visibility = 0.0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float2 disk = XR_WEB_ShadowVogelTap(tap, rotation) * radius;
        float3 sampleDirection = normalize(direction + tangent * disk.x + bitangent * disk.y);
        float depth = XR_ADV_SampleTextureCubeLod(reference, sampleDirection, 0.0).r * farZ;
        visibility += float(receiverDepth <= depth);
    }
    return visibility * 0.125;
}

float XR_WEB_SampleStandaloneShadow(XRAdvancedShadowRecord record, XRAdvancedTextureRecord textureRecord,
    XRAdvancedEncodedTextureReference reference, XRAdvancedSurface surface, float3 normal, out uint reason)
{
    reason = XR_ADV_SHADOW_OK;
    if (record.encoding != 0u || (record.flags & 224u) != 96u ||
        record.type > 3u || record.type == 2u ||
        record.type == 0u && (textureRecord.dimension != 1u || reference.payload1 != 1u) ||
        record.type == 1u && (textureRecord.dimension != 1u || reference.payload1 != 0u) ||
        record.type == 3u && (textureRecord.dimension != 3u || reference.payload1 != 0u))
    {
        reason = XR_ADV_SHADOW_UNSUPPORTED;
        return 1.0;
    }
    if (record.type == 3u)
        return XR_WEB_SampleStandalonePoint(record, textureRecord, reference, surface, normal);

    float2 mapSize = float2(textureRecord.width, textureRecord.height);
    float texelUv = max(1.0 / mapSize.x, 1.0 / mapSize.y);
    float nearZ = max(record.depthRangeAndCascade.x, 0.0001);
    float farZ = max(record.depthRangeAndCascade.y, nearZ + 0.0001);
    float normalOffset = max(record.depthRangeAndCascade.w, 0.0);
    float constantBias = max(record.depthRangeAndCascade.z, 0.0);
    float axisDistance = 0.0;
    if (record.type == 1u)
    {
        // Whole-map spot records use this otherwise-unused atlas vector for
        // the exact authored forward and cone; inherited scale cannot alter it.
        float3 forward = XR_WEB_ShadowNormalize(record.uvScaleBias.xyz);
        float cosOuter = clamp(record.uvScaleBias.w, 0.001, 0.999999);
        float tanOuter = sqrt(max(1.0 - cosOuter * cosOuter, 0.0)) / cosOuter;
        axisDistance = max(dot(surface.worldPosition - record.renderedLightPositionAndFar.xyz, forward), 0.0001);
        float worldTexel = 2.0 * axisDistance * tanOuter / max(mapSize.x, 1.0);
        normalOffset = worldTexel * max(record.depthBiasAndFilter.z, 0.0);
        float z = max(axisDistance, nearZ + 0.0001);
        constantBias = max(worldTexel * record.depthBiasAndFilter.x, 0.0) * nearZ * farZ /
            max((farZ - nearZ) * z * z, 1e-8);
    }
    float3 offsetPosition = surface.worldPosition + normal * normalOffset;
    float4 clip = XR_ADV_TransformRowVector(float4(offsetPosition, 1.0), record.worldToShadow);
    if (clip.w <= 0.0 || !XR_ADV_IsFinite(clip)) { reason = XR_ADV_SHADOW_OUTSIDE; return 1.0; }
    float3 ndc = clip.xyz / clip.w;
    float3 coordinate = float3(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5, ndc.z);
    float slopeBias = XR_WEB_ShadowPlaneBias(record, surface, coordinate,
        max(record.momentParameters.y, record.type == 1u ? 1.0 / mapSize.x : texelUv), mapSize.x);
    float receiver = coordinate.z - (constantBias + max(slopeBias, 0.0));
    if (record.type == 1u && axisDistance >= farZ || any(coordinate < float3(0.0)) || any(coordinate > float3(1.0)))
    { reason = XR_ADV_SHADOW_OUTSIDE; return 1.0; }
    float rotation = XR_WEB_ShadowRotation(surface.samplePixelPosition);
    float searchRadius = max(record.momentParameters.x, texelUv);
    float blockerSum = 0.0;
    int blockerCount = 0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float2 uv = coordinate.xy + XR_WEB_ShadowVogelTap(tap, rotation) * searchRadius;
        int2 texel = int2(clamp(uv * mapSize, float2(0.0), mapSize - 1.0));
        float depth = XR_ADV_LoadShadow2D(reference, texel);
        if (depth < receiver) { blockerSum += depth; blockerCount++; }
    }
    if (blockerCount == 0) return 1.0;
    float averageBlocker = blockerSum / float(blockerCount);
    float minRadius = max(record.momentParameters.z, texelUv);
    float maxRadius = max(record.momentParameters.w, minRadius);
    float penumbra = abs(receiver - averageBlocker) / max(abs(averageBlocker), 0.0001) * max(record.depthBiasAndFilter.w, 0.0);
    float radius = max(clamp(penumbra, minRadius, maxRadius), texelUv);
    float visibility = 0.0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
        visibility += XR_ADV_CompareShadow2D(reference,
            coordinate.xy + XR_WEB_ShadowVogelTap(tap, rotation) * radius, receiver);
    return visibility * 0.125;
}
