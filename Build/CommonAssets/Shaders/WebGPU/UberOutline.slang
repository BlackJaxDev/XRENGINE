// Canonical single-view Uber inverse-hull lowering. The source reader admits
// only the pre-outline feature closure represented by OutlineFeatures. Position
// and normal attributes already contain any shared compute deformation.

struct OutlineViewUniforms
{
    float4x4 viewProjection;
    float4 cameraPosition;
    float4 u_ScreenParams;
    float u_Time;
};

struct OutlineObjectUniforms
{
    float4x4 modelMatrix;
    float4x4 normalMatrix;
};

struct OutlineMaterialUniforms
{
    uint OutlineFeatures; // 1: authored alpha masks; 2: authored dissolve
    float4 _MainTex_ST;
    float2 _MainTexPan;
    int _MainTexUV;
    float4 _Color;
    float _MainVertexColoringEnabled;
    float _MainVertexColoringLinearSpace;
    float _MainVertexColoring;
    float _MainUseVertexColorAlpha;
    float4 _AlphaMask_ST;
    float2 _AlphaMaskPan;
    int _AlphaMaskUV;
    int _MainAlphaMaskMode;
    float _AlphaMaskBlendStrength;
    float _AlphaMaskValue;
    float _AlphaMaskInvert;
    float _Cutoff;
    int _Mode;
    float _AlphaForceOpaque;
    float _AlphaMod;
    float4 _OutlineColor;
    float _OutlineWidth;
    float _OutlineEmission;
    float _OutlineLit;
    float _OutlineDistanceFadeStart;
    float _OutlineDistanceFadeEnd;
    float _OutlineTextureTint;
    float _OutlineVertexColorTint;
    int _OutlineExpansionMode;
    int _OutlineSpace;
    float3 _OutlinePersonaDirection;
    float3 _OutlineDropShadowOffset;
    float _OutlineFixedSize;
    float _OutlineUseVertexColors;
    float _OutlineZOffset;
    float4 _OutlineTexture_ST;
    float2 _OutlineTexturePan;
    int _OutlineTextureUV;
    float4 _OutlineMask_ST;
    float2 _OutlineMaskPan;
    int _OutlineMaskUV;
    float _OutlineHueShift;
    float _OutlineHueShiftSpeed;
    float _OutlineShadowStrength;
    float _DissolveType;
    float _DissolveProgress;
    float4 _DissolveEdgeColor;
    float _DissolveEdgeWidth;
    float _DissolveEdgeEmission;
    float4 _DissolveNoiseTexture_ST;
    float _DissolveNoiseStrength;
    float3 _DissolveStartPoint;
    float3 _DissolveEndPoint;
    float _DissolveInvert;
    float2 _DissolveNoiseTexturePan;
    int _DissolveNoiseTextureUV;
    float4 _DissolveDetailNoise_ST;
    float2 _DissolveDetailNoisePan;
    int _DissolveDetailNoiseUV;
    float _DissolveDetailStrength;
    float4 _DissolveMask_ST;
    float2 _DissolveMaskPan;
    int _DissolveMaskUV;
    float _DissolveMaskInvert;
    float _DissolveContinuous;
    int _DissolveCoordinateSpace;
    float2 _DissolveTileGrid;
    float _DissolveHueShift;
    float4 _DissolveEdgeGradient_ST;
    float4 _DissolveEdgeTexture_ST;
    float2 _DissolveEdgeTexturePan;
    int _DissolveEdgeTextureUV;
    float _DissolveCutoff;
};

[[vk::binding(0, 0)]] ConstantBuffer<OutlineViewUniforms> view;
[[vk::binding(1, 0)]] ConstantBuffer<OutlineObjectUniforms> object;
[[vk::binding(0, 1)]] ConstantBuffer<OutlineMaterialUniforms> material;
[[vk::binding(0, 2)]] Texture2D<float4> _MainTex;
[[vk::binding(1, 2)]] SamplerState _MainTexSampler;
[[vk::binding(2, 2)]] Texture2D<float4> _OutlineTexture;
[[vk::binding(3, 2)]] SamplerState _OutlineTextureSampler;
[[vk::binding(4, 2)]] Texture2D<float4> _OutlineMask;
[[vk::binding(5, 2)]] SamplerState _OutlineMaskSampler;
#ifdef XRE_OUTLINE_ALPHA_MASKS
[[vk::binding(6, 2)]] Texture2D<float4> _AlphaMask;
[[vk::binding(7, 2)]] SamplerState _AlphaMaskSampler;
#endif
#ifdef XRE_OUTLINE_DISSOLVE
[[vk::binding(8, 2)]] Texture2D<float4> _DissolveNoiseTexture;
[[vk::binding(9, 2)]] SamplerState _DissolveNoiseTextureSampler;
[[vk::binding(10, 2)]] Texture2D<float4> _DissolveDetailNoise;
[[vk::binding(11, 2)]] SamplerState _DissolveDetailNoiseSampler;
[[vk::binding(12, 2)]] Texture2D<float4> _DissolveMask;
[[vk::binding(13, 2)]] SamplerState _DissolveMaskSampler;
[[vk::binding(14, 2)]] Texture2D<float4> _DissolveEdgeGradient;
[[vk::binding(15, 2)]] SamplerState _DissolveEdgeGradientSampler;
[[vk::binding(16, 2)]] Texture2D<float4> _DissolveEdgeTexture;
[[vk::binding(17, 2)]] SamplerState _DissolveEdgeTextureSampler;
#endif

struct OutlineVertexInput
{
    // Slang's WGSL allocator uses the numeric semantic index; a contiguous
    // TEXCOORD family preserves the exact locations in the cooked recipe.
    [[vk::location(0)]] float3 position : TEXCOORD0;
    [[vk::location(1)]] float3 normal : TEXCOORD1;
    [[vk::location(2)]] float2 uv0 : TEXCOORD2;
    [[vk::location(3)]] float2 uv1 : TEXCOORD3;
    [[vk::location(4)]] float2 uv2 : TEXCOORD4;
    [[vk::location(5)]] float2 uv3 : TEXCOORD5;
    [[vk::location(6)]] float4 color0 : TEXCOORD6;
};

struct OutlineVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldPosition : TEXCOORD0;
    [[vk::location(1)]] float3 worldNormal : TEXCOORD1;
    [[vk::location(2)]] float3 localPosition : TEXCOORD2;
    [[vk::location(3)]] float2 uv0 : TEXCOORD3;
    [[vk::location(4)]] float2 uv1 : TEXCOORD4;
    [[vk::location(5)]] float2 uv2 : TEXCOORD5;
    [[vk::location(6)]] float2 uv3 : TEXCOORD6;
    [[vk::location(7)]] float4 color0 : COLOR0;
};

float3 transformOutlineNormal(float3 direction)
{
    float3 column0 = float3(object.modelMatrix[0][0], object.modelMatrix[1][0], object.modelMatrix[2][0]);
    float3 column1 = float3(object.modelMatrix[0][1], object.modelMatrix[1][1], object.modelMatrix[2][1]);
    float3 column2 = float3(object.modelMatrix[0][2], object.modelMatrix[1][2], object.modelMatrix[2][2]);
    // uniforms.glsl constructs these cofactor columns explicitly. Preserve its
    // determinant sign for mirrored models when normalizing the result.
    return cross(column1, column2) * direction.x
        + cross(column2, column0) * direction.y
        + cross(column0, column1) * direction.z;
}

[shader("vertex")]
OutlineVertexOutput uberOutlineVertex(OutlineVertexInput input)
{
    float3 position = input.position;
    float3 normal = input.normal;
    float vertexWidth = lerp(1.0, max(input.color0.a, 0.0), saturate(material._OutlineUseVertexColors));
    float width = material._OutlineWidth * 0.01 * vertexWidth;
    float3 direction = normal;
    if (material._OutlineExpansionMode == 1)
        direction = normalize(position + normal * 0.001);
    else if (material._OutlineExpansionMode == 2)
        direction = normalize(material._OutlinePersonaDirection);
    else if (material._OutlineExpansionMode == 3)
        direction = normalize(material._OutlineDropShadowOffset);

    if (material._OutlineExpansionMode == 3)
        position += material._OutlineDropShadowOffset * width;
    else if (material._OutlineSpace == 1)
    {
        float3 worldDirection = normalize(transformOutlineNormal(direction));
        // NormalMatrix is the full transpose of inverse(ModelMatrix), so its
        // transpose supplies the canonical world-to-local displacement.
        position += mul(transpose(object.normalMatrix), float4(worldDirection * width, 0.0)).xyz;
    }
    else if (material._OutlineSpace != 2)
        position += normalize(direction) * width;

    OutlineVertexOutput output;
    float4 worldPosition = mul(object.modelMatrix, float4(position, 1.0));
    output.position = mul(view.viewProjection, worldPosition);
    if (material._OutlineSpace == 2 || material._OutlineFixedSize > 0.5)
    {
        float2 screen = max(view.u_ScreenParams.xy, float2(1.0));
        float2 clipNormal = normalize(mul(view.viewProjection, mul(object.modelMatrix, float4(normal, 0.0))).xy);
        // Canonical clip expansion neither changes direction by expansion mode
        // nor applies vertex alpha. FixedSize is additive to local expansion.
        output.position.xy += clipNormal * (material._OutlineWidth * 2.0 / screen) * output.position.w;
    }
    // The engine supplies the same zero-to-one projection as other Slang
    // frontends. Its authored clip-space offset is applied after projection.
    output.position.z += material._OutlineZOffset * 0.0001 * output.position.w;
    output.worldPosition = worldPosition.xyz;
    output.localPosition = position;
    output.worldNormal = normalize(transformOutlineNormal(normal));
    output.uv0 = input.uv0;
    output.uv1 = input.uv1;
    output.uv2 = input.uv2;
    output.uv3 = input.uv3;
    output.color0 = input.color0;
    return output;
}

float2 polarUV(float3 direction)
{
    return float2(atan2(direction.z, direction.x) / 6.28318530718 + 0.5, acos(direction.y) / 3.14159265359);
}

float2 getUV(int mode, OutlineVertexOutput input, float3 worldNormal, float3 viewDirection)
{
    switch (mode)
    {
        case 0: return input.uv0;
        case 1: return input.uv1;
        case 2: return input.uv2;
        case 3: return input.uv3;
        case 4: return polarUV(reflect(-viewDirection, worldNormal));
        case 5: return input.worldPosition.xz;
        case 6: return polarUV(worldNormal);
        case 8: return input.localPosition.xy;
        default: return input.uv0;
    }
}

float2 textureUV(float2 uv, float4 transform, float2 pan)
{
    return uv * transform.xy + transform.zw + pan * view.u_Time;
}

float3 sRGBToLinear(float3 color)
{
    return color * (color * (color * 0.305306011 + 0.682171111) + 0.012522878);
}

float3 rgbToHsv(float3 color)
{
    float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(color.bg, k.wz), float4(color.gb, k.xy), step(color.b, color.g));
    float4 q = lerp(float4(p.xyw, color.r), float4(color.r, p.yzx), step(p.x, color.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 hsvToRgb(float3 color)
{
    float4 k = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(color.xxx + k.xyz) * 6.0 - k.www);
    return color.z * lerp(k.xxx, saturate(p - k.xxx), color.y);
}

float3 hueShift(float3 color, float shift)
{
    float3 hsv = rgbToHsv(color);
    hsv.x = frac(hsv.x + shift);
    return hsvToRgb(hsv);
}

float4 calculateBaseColor(OutlineVertexOutput input, float3 normal, float3 viewDirection)
{
    float2 uv = textureUV(getUV(material._MainTexUV, input, normal, viewDirection), material._MainTex_ST, material._MainTexPan);
    float4 baseColor = _MainTex.Sample(_MainTexSampler, uv) * material._Color;
    if (material._MainVertexColoringEnabled > 0.5)
    {
        float4 vertexColor = input.color0;
        if (material._MainVertexColoringLinearSpace > 0.5)
            vertexColor.rgb = sRGBToLinear(vertexColor.rgb);
        baseColor.rgb *= lerp(float3(1.0), vertexColor.rgb, material._MainVertexColoring);
        baseColor.a *= lerp(1.0, vertexColor.a, material._MainUseVertexColorAlpha);
    }
    return baseColor;
}

float calculateAlpha(float4 baseColor, OutlineVertexOutput input, float3 normal, float3 viewDirection)
{
    float alpha = baseColor.a;
#ifdef XRE_OUTLINE_ALPHA_MASKS
    if ((material.OutlineFeatures & 1u) != 0u && material._MainAlphaMaskMode > 0)
    {
        float2 uv = textureUV(getUV(material._AlphaMaskUV, input, normal, viewDirection), material._AlphaMask_ST, material._AlphaMaskPan);
        float mask = _AlphaMask.Sample(_AlphaMaskSampler, uv).r;
        if (material._AlphaMaskInvert > 0.5)
            mask = 1.0 - mask;
        mask = mask * material._AlphaMaskBlendStrength + material._AlphaMaskValue;
        switch (material._MainAlphaMaskMode)
        {
            case 1: alpha = mask; break;
            case 2: alpha *= mask; break;
            case 3: alpha = saturate(alpha + mask); break;
            case 4: alpha = saturate(alpha - mask); break;
        }
    }
#endif
    alpha = saturate(alpha + material._AlphaMod);
    if (material._AlphaForceOpaque > 0.5)
        alpha = 1.0;
    return alpha;
}

#ifdef XRE_OUTLINE_DISSOLVE
bool applyDissolve(OutlineVertexOutput input, float3 normal, float3 viewDirection, inout float3 color, inout float3 emission)
{
    float2 noiseBaseUV = getUV(material._DissolveNoiseTextureUV, input, normal, viewDirection);
    float2 noiseUV = textureUV(noiseBaseUV, material._DissolveNoiseTexture_ST, material._DissolveNoiseTexturePan);
    float baseNoise = _DissolveNoiseTexture.Sample(_DissolveNoiseTextureSampler, noiseUV).r;
    float2 detailUV = textureUV(getUV(material._DissolveDetailNoiseUV, input, normal, viewDirection), material._DissolveDetailNoise_ST, material._DissolveDetailNoisePan);
    float detailNoise = _DissolveDetailNoise.Sample(_DissolveDetailNoiseSampler, detailUV).r;
    float combinedNoise = baseNoise + (detailNoise - 0.5) * 2.0 * material._DissolveDetailStrength;
    float2 maskUV = textureUV(getUV(material._DissolveMaskUV, input, normal, viewDirection), material._DissolveMask_ST, material._DissolveMaskPan);
    float mask = _DissolveMask.Sample(_DissolveMaskSampler, maskUV).r;
    if (material._DissolveMaskInvert > 0.5)
        mask = 1.0 - mask;

    float value = 0.0;
    int type = int(material._DissolveType);
    if (type == 0)
    {
        float3 direction = material._DissolveEndPoint - material._DissolveStartPoint;
        direction = dot(direction, direction) > 0.001 ? direction : float3(0.0, 1.0, 0.0);
        float linear = dot(input.localPosition, normalize(direction)) + 0.5;
        value = lerp(linear, linear + (combinedNoise - 0.5) * 2.0, material._DissolveNoiseStrength);
    }
    else if (type == 1)
    {
        float radius = length(material._DissolveEndPoint - material._DissolveStartPoint);
        float spherical = length(input.worldPosition - material._DissolveStartPoint) / max(radius, 0.001);
        value = lerp(spherical, spherical + (combinedNoise - 0.5) * 2.0, material._DissolveNoiseStrength);
    }
    else if (type == 2)
    {
        float3 coordinate = material._DissolveCoordinateSpace == 0 ? input.localPosition : input.worldPosition;
        float3 direction = material._DissolveEndPoint - material._DissolveStartPoint;
        float totalDistance = length(direction);
        float pointToPoint = totalDistance < 0.001 ? material._DissolveProgress
            : dot(coordinate - material._DissolveStartPoint, normalize(direction)) / totalDistance;
        value = lerp(pointToPoint, pointToPoint + (combinedNoise - 0.5) * 2.0, material._DissolveNoiseStrength);
    }
    else if (type == 3)
    {
        float3 coordinate = material._DissolveCoordinateSpace == 0 ? input.localPosition : input.worldPosition;
        float radius = max(length(material._DissolveEndPoint - material._DissolveStartPoint), 0.001);
        float centerOut = length(coordinate - material._DissolveStartPoint) / radius;
        value = lerp(centerOut, centerOut + (combinedNoise - 0.5) * 2.0, material._DissolveNoiseStrength);
    }
    else if (type == 4)
    {
        float2 grid = max(material._DissolveTileGrid, float2(1.0));
        float2 tile = floor(frac(noiseBaseUV) * grid);
        float tileIndex = (tile.y * grid.x + tile.x) / max(grid.x * grid.y - 1.0, 1.0);
        value = lerp(tileIndex, tileIndex + (combinedNoise - 0.5) * 2.0, material._DissolveNoiseStrength);
    }
    if (material._DissolveContinuous > 0.5)
        value = frac(value + material._DissolveProgress);
    value *= mask;
    if (material._DissolveInvert > 0.5)
        value = 1.0 - value;

    float threshold = material._DissolveProgress;
    float edgeWidth = material._DissolveEdgeWidth;
    float dissolveAlpha = step(threshold, value);
    float edgeFactor = 0.0;
    if (value >= threshold && value < threshold + edgeWidth && edgeWidth > 0.001)
        edgeFactor = 1.0 - (value - threshold) / edgeWidth;
    // Texture reads precede varying coverage/edge branches so WebGPU can prove
    // their implicit derivatives execute in converged control flow. Sampling
    // extra discarded or non-edge fragments has no observable surface effect.
    float2 gradientUV = float2(edgeFactor, 0.5) * material._DissolveEdgeGradient_ST.xy + material._DissolveEdgeGradient_ST.zw;
    float3 gradient = _DissolveEdgeGradient.Sample(_DissolveEdgeGradientSampler, gradientUV).rgb;
    float2 edgeUV = textureUV(getUV(material._DissolveEdgeTextureUV, input, normal, viewDirection), material._DissolveEdgeTexture_ST, material._DissolveEdgeTexturePan);
    float3 edgeTexture = _DissolveEdgeTexture.Sample(_DissolveEdgeTextureSampler, edgeUV).rgb;
    if (edgeFactor > 0.0)
    {
        float3 edgeColor = hueShift(material._DissolveEdgeColor.rgb, material._DissolveHueShift) * gradient * edgeTexture;
        color = lerp(color, edgeColor, edgeFactor * material._DissolveEdgeColor.a);
        emission += edgeColor * edgeFactor * material._DissolveEdgeEmission;
    }
    // The canonical dissolve branch leaves fragment alpha unchanged.
    return dissolveAlpha < material._DissolveCutoff;
}
#endif

[shader("fragment")]
float4 uberOutlineFragment(OutlineVertexOutput input, bool frontFacing : SV_IsFrontFace) : SV_Target0
{
    float3 viewDirection = normalize(view.cameraPosition.xyz - input.worldPosition);
    float3 normal = normalize(input.worldNormal) * (frontFacing ? 1.0 : -1.0);
    float4 baseColor = calculateBaseColor(input, normal, viewDirection);
    float alpha = calculateAlpha(baseColor, input, normal, viewDirection);
    bool dissolveDiscard = false;
#ifdef XRE_OUTLINE_DISSOLVE
    float3 emission = float3(0.0);
    if ((material.OutlineFeatures & 2u) != 0u)
        dissolveDiscard = applyDissolve(input, normal, viewDirection, baseColor.rgb, emission);
#endif

    float2 uv = textureUV(getUV(material._OutlineTextureUV, input, normal, viewDirection), material._OutlineTexture_ST, material._OutlineTexturePan);
    float2 maskUV = textureUV(getUV(material._OutlineMaskUV, input, normal, viewDirection), material._OutlineMask_ST, material._OutlineMaskPan);
    float4 textureColor = _OutlineTexture.Sample(_OutlineTextureSampler, uv);
    float mask = _OutlineMask.Sample(_OutlineMaskSampler, maskUV).r;
    float4 color = material._OutlineColor;
    color.rgb = hueShift(color.rgb * textureColor.rgb, material._OutlineHueShift + material._OutlineHueShiftSpeed * view.u_Time);
    color.rgb = lerp(color.rgb, color.rgb * baseColor.rgb, saturate(material._OutlineTextureTint));
    color.rgb = lerp(color.rgb, color.rgb * input.color0.rgb, saturate(material._OutlineVertexColorTint));
    float rim = pow(1.0 - saturate(dot(normal, viewDirection)), 2.0);
    color.rgb *= lerp(float3(1.0), float3(lerp(1.0 - material._OutlineShadowStrength, 1.0, rim)), saturate(material._OutlineLit));
    color.rgb += color.rgb * material._OutlineEmission;
    float distanceFade = 1.0 - smoothstep(min(material._OutlineDistanceFadeStart, material._OutlineDistanceFadeEnd),
        max(material._OutlineDistanceFadeStart, material._OutlineDistanceFadeEnd), length(input.worldPosition - view.cameraPosition.xyz));
    color.a *= alpha * textureColor.a * mask * distanceFade;
    // Defer coverage rejection until every implicit texture read has executed.
    // The canonical ordering of cutout, dissolve and outline alpha tests is
    // preserved in the predicate; no discarded fragment reaches the output.
    if ((material._Mode == 1 && alpha < material._Cutoff) || dissolveDiscard || color.a <= 0.0001)
        discard;
    // The source exits here before ordinary opacity forcing or emission output.
    return color;
}
