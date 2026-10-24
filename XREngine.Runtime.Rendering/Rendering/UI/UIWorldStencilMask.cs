namespace XREngine.Rendering.UI;

/// <summary>Checks the stencil bits that a bound attachment can store.</summary>
internal static class UIWorldStencilMask
{
    internal static bool CoversAttachment(int queriedMask, int stencilBits)
    {
        if (stencilBits is <= 0 or > 32)
            return false;
        uint required = stencilBits == 32
            ? uint.MaxValue : (1u << stencilBits) - 1u;
        return (unchecked((uint)queriedMask) & required) == required;
    }
}
