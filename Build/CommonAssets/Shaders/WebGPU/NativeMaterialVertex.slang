// Same isolated authored function as the raster wrapper, after morph and skin.
import NativeVertexFunction;

struct NativeMaterialVertexUniforms
{
    float4 input0;
    float4 input1;
    float4 input2;
    float4 input3;
    float4 previousInput0;
    float4 previousInput1;
    float4 previousInput2;
    float4 previousInput3;
    uint sourceCurrentStream;
    uint sourceCurrentOffset;
    uint sourcePreviousStream;
    uint sourcePreviousOffset;
    uint currentOutputOffset;
    uint previousOutputOffset;
    uint vertexCount;
    uint previousValid;
};

[[vk::binding(0, 0)]] StructuredBuffer<uint> sourceGeometry;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> outputGeometry;
[[vk::binding(2, 0)]] ConstantBuffer<NativeMaterialVertexUniforms> nativeVertexParameters;

#include "NativeVertexNormal.slang"

bool nativeSourceRange(uint stream, uint offset, uint count, uint words)
{
    if (words < 28u || (stream != 0u && stream != 2u && stream != 3u) || (offset & 63u) != 0u) return false;
    uint directory = stream * 4u;
    uint start = sourceGeometry[directory];
    uint bytes = sourceGeometry[directory + 1u];
    if (start < 28u || start > words || (bytes & 63u) != 0u || bytes / 4u > words - start) return false;
    return offset <= bytes && count <= (bytes - offset) / 64u;
}

bool nativeOutputRange(uint stream, uint offset, uint count, uint words)
{
    if (words < 28u || (offset & 63u) != 0u) return false;
    uint directory = stream * 4u;
    uint start = outputGeometry[directory];
    uint bytes = outputGeometry[directory + 1u];
    if (start < 28u || start > words || (bytes & 63u) != 0u || bytes / 4u > words - start) return false;
    return offset <= bytes && count <= (bytes - offset) / 64u;
}

void nativeWriteVertex(uint sourceWord, uint outputWord, bool previous)
{
    float3 position = float3(asfloat(sourceGeometry[sourceWord]), asfloat(sourceGeometry[sourceWord + 1u]), asfloat(sourceGeometry[sourceWord + 2u]));
    float3 normal = nativeDecodeNormal(sourceGeometry[sourceWord + 3u]);
    applyNativeVertex(position, normal,
        previous ? nativeVertexParameters.previousInput0 : nativeVertexParameters.input0,
        previous ? nativeVertexParameters.previousInput1 : nativeVertexParameters.input1,
        previous ? nativeVertexParameters.previousInput2 : nativeVertexParameters.input2,
        previous ? nativeVertexParameters.previousInput3 : nativeVertexParameters.input3);
    for (uint word = 0u; word < 16u; ++word) outputGeometry[outputWord + word] = sourceGeometry[sourceWord + word];
    outputGeometry[outputWord] = asuint(position.x);
    outputGeometry[outputWord + 1u] = asuint(position.y);
    outputGeometry[outputWord + 2u] = asuint(position.z);
    outputGeometry[outputWord + 3u] = nativeEncodeNormal(normal);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void nativeMaterialVertex(uint3 dispatchThread : SV_DispatchThreadID)
{
    uint vertex = dispatchThread.x;
    uint count = nativeVertexParameters.vertexCount;
    if (vertex >= count) return;
    uint sourceWords, sourceStride, outputWords, outputStride;
    sourceGeometry.GetDimensions(sourceWords, sourceStride);
    outputGeometry.GetDimensions(outputWords, outputStride);
    uint currentStream = nativeVertexParameters.sourceCurrentStream;
    uint currentOffset = nativeVertexParameters.sourceCurrentOffset;
    bool previous = nativeVertexParameters.previousValid != 0u;
    uint previousStream = previous ? nativeVertexParameters.sourcePreviousStream : currentStream;
    uint previousOffset = previous ? nativeVertexParameters.sourcePreviousOffset : currentOffset;
    if (!nativeSourceRange(currentStream, currentOffset, count, sourceWords) ||
        !nativeSourceRange(previousStream, previousOffset, count, sourceWords) ||
        !nativeOutputRange(2u, nativeVertexParameters.currentOutputOffset, count, outputWords) ||
        !nativeOutputRange(3u, nativeVertexParameters.previousOutputOffset, count, outputWords)) return;
    uint currentWord = sourceGeometry[currentStream * 4u] + currentOffset / 4u + vertex * 16u;
    uint previousWord = sourceGeometry[previousStream * 4u] + previousOffset / 4u + vertex * 16u;
    uint currentOutputWord = outputGeometry[8u] + nativeVertexParameters.currentOutputOffset / 4u + vertex * 16u;
    uint previousOutputWord = outputGeometry[12u] + nativeVertexParameters.previousOutputOffset / 4u + vertex * 16u;
    nativeWriteVertex(currentWord, currentOutputWord, false);
    nativeWriteVertex(previousWord, previousOutputWord, previous);
}
