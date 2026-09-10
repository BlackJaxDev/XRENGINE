// Native counterpart of Compute/Indirect/GPURenderCopyCount3.comp.
// Explicitly sized fields keep the counter ABI identical on both frontends.
struct Counts { uint first; uint second; uint third; };
[[vk::binding(0, 0)]] StructuredBuffer<Counts> SrcCountBuffer;
[[vk::binding(1, 0)]] RWStructuredBuffer<Counts> DstCountBuffer;

[shader("compute")]
[numthreads(1, 1, 1)]
void copyCounts()
{
    DstCountBuffer[0] = SrcCountBuffer[0];
}
