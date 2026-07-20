using JoltPhysicsSharp;

namespace XREngine.Scene;

public static class LayerMaskJoltExtensions
{
    public static ObjectLayer AsJoltObjectLayer(this LayerMask layerMask)
    {
        uint mask = unchecked((uint)layerMask.Value);
        // Jolt's mask filter expects the group itself to be a bit mask, not a
        // zero-based bit index. Passing index 0 produced a group of zero, which
        // made the default engine layer unable to collide with anything.
        uint group = GetLowestSetBit(mask);
        if (mask == 0)
            mask = uint.MaxValue;
        return ObjectLayerPairFilterMask.GetObjectLayer(group, mask);
    }

    public static uint AsJoltObjectLayerGroup(this LayerMask layerMask)
        => ObjectLayerPairFilterMask.GetGroup(layerMask.AsJoltObjectLayer());

    public static uint AsJoltObjectLayerMask(this LayerMask layerMask)
        => ObjectLayerPairFilterMask.GetMask(layerMask.AsJoltObjectLayer());

    internal static ObjectLayer CreateObjectLayer(ushort collisionGroup, uint groupsMaskWord0)
    {
        uint group = collisionGroup < 32 ? 1u << collisionGroup : 1u;
        uint mask = groupsMaskWord0 == 0 ? uint.MaxValue : groupsMaskWord0;
        return ObjectLayerPairFilterMask.GetObjectLayer(group, mask);
    }

    private static uint GetLowestSetBit(uint value)
        => value == 0 ? 1u : value & (0u - value);
}
