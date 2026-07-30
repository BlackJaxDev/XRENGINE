namespace XREngine.Rendering;

/// <summary>
/// Exact attachment clear values for normal and reversed depth views.
/// </summary>
public static class AdvancedVisibilityClearContract
{
    public const uint IdentityDraw = AdvancedVisibilityBufferContract.InvalidWord;
    public const uint IdentityPrimitive = AdvancedVisibilityBufferContract.InvalidWord;
    public const uint Metadata = AdvancedVisibilityBufferContract.InvalidWord;
    public const uint Selection = AdvancedVisibilityBufferContract.InvalidWord;
    public const uint Stencil = 0u;

    public static float Depth(bool reversedDepth)
        => reversedDepth ? 0.0f : 1.0f;
}
