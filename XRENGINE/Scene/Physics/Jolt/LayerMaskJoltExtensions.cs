using JoltPhysicsSharp;

namespace XREngine.Scene;

public static class LayerMaskJoltExtensions
{
    public static ObjectLayer AsJoltObjectLayer(this LayerMask layerMask)
    {
        uint mask = unchecked((uint)layerMask.Value);
        uint group = GetLowestSetBitIndex(mask);
        if (mask == 0)
            mask = uint.MaxValue;
        return ObjectLayerPairFilterMask.GetObjectLayer(group, mask);
    }

    public static uint AsJoltObjectLayerGroup(this LayerMask layerMask)
        => ObjectLayerPairFilterMask.GetGroup(layerMask.AsJoltObjectLayer());

    public static uint AsJoltObjectLayerMask(this LayerMask layerMask)
        => ObjectLayerPairFilterMask.GetMask(layerMask.AsJoltObjectLayer());

    private static uint GetLowestSetBitIndex(uint value)
    {
        if (value == 0)
            return 0;

        uint index = 0;
        while ((value & 1u) == 0u)
        {
            value >>= 1;
            index++;
        }

        return index;
    }
}