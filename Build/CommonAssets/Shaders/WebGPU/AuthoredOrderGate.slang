// The gate changes only the completed vertex position. The original fragment
// entry and active candidate varyings retain their exact source behavior.
struct AuthoredOrderGateData
{
    uint sourceIndex;
    uint activeRank;
    uint sourceCount;
    uint enabled;
};

[[vk::binding(2, 0)]] StructuredBuffer<uint> authoredSourceRanks;
[[vk::binding(3, 0)]] ConstantBuffer<AuthoredOrderGateData> authoredOrderGate;

float4 authoredOrderPosition(float4 position)
{
    if (authoredOrderGate.enabled == 0u)
        return position;
    uint rankCount;
    uint rankStride;
    authoredSourceRanks.GetDimensions(rankCount, rankStride);
    if (authoredOrderGate.sourceIndex >= authoredOrderGate.sourceCount ||
        authoredOrderGate.sourceIndex >= rankCount ||
        authoredOrderGate.activeRank >= authoredOrderGate.sourceCount)
        return float4(2.0, 2.0, 2.0, 1.0);
    if (authoredSourceRanks[authoredOrderGate.sourceIndex] != authoredOrderGate.activeRank)
        return float4(2.0, 2.0, 2.0, 1.0);
    return position;
}
