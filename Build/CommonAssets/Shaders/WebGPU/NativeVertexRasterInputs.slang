// The imported function cannot access any raster stage or resource scope.
import NativeVertexFunction;
#include "NativeVertexNormal.slang"
struct NativeVertexInputUniforms
{
    float4 input0;
    float4 input1;
    float4 input2;
    float4 input3;
};
[[vk::binding(1, 1)]] ConstantBuffer<NativeVertexInputUniforms> nativeVertexInputs;
void applyRasterNativeVertex(inout float3 position, inout float3 normal)
{
    normal = nativeDecodeNormal(nativeEncodeNormal(normal));
    applyNativeVertex(position, normal,
        nativeVertexInputs.input0, nativeVertexInputs.input1,
        nativeVertexInputs.input2, nativeVertexInputs.input3);
    normal = nativeDecodeNormal(nativeEncodeNormal(normal));
}
