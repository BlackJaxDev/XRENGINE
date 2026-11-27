namespace XREngine.Rendering;

/// <summary>
/// Central 8-bit stencil allocation for the primary <c>DepthStencil</c> attachment in
/// <see cref="AdvancedRenderPipeline"/>. Every pass that reads or writes scene stencil
/// bits must mask its operations to its assigned bit range.
/// </summary>
public static class AdvancedStencilBitLayout
{
    /// <summary>Stencil bit 0 (<c>0x01</c>): editor hover highlight for forward draws.</summary>
    public const uint HoverBit = 0x01u;

    /// <summary>Stencil bit 1 (<c>0x02</c>): editor selection highlight for forward draws.</summary>
    public const uint SelectionBit = 0x02u;

    /// <summary>Stencil bits 0..1 (<c>0x03</c>): combined editor highlight mask.</summary>
    public const uint EditorHighlightMask = HoverBit | SelectionBit;

    /// <summary>Bit offset of the 5-bit portal and mirror node identifier field.</summary>
    public const int PortalNodeShift = 2;

    /// <summary>Stencil bits 2..6 (<c>0x7C</c>): active portal or mirror node identifier mask.</summary>
    public const uint PortalNodeMask = 0x7Cu;

    /// <summary>Maximum non-zero portal node identifier encodable in bits 2..6 (31 nodes per eye layer).</summary>
    public const uint MaxPortalNodeId = PortalNodeMask >> PortalNodeShift;

    /// <summary>
    /// Stencil bit 7 (<c>0x80</c>): gizmo tone-map bypass and TSR reactive mark.
    /// Read by post-processing and temporal super-resolution shaders.
    /// </summary>
    public const uint GizmoBypassBit = 0x80u;

    /// <summary>Union of every assigned stencil bit in the scene <c>DepthStencil</c> attachment.</summary>
    public const uint AllUsedBitsMask = EditorHighlightMask | PortalNodeMask | GizmoBypassBit;

    /// <summary>
    /// Shifts a portal node identifier (<c>0</c> for primary view, <c>1..31</c> for active portal nodes)
    /// into stencil bits 2..6.
    /// </summary>
    /// <param name="nodeId">The portal node identifier in the range <c>0..31</c>.</param>
    /// <returns>The shifted 8-bit stencil reference value within <see cref="PortalNodeMask"/>.</returns>
    public static uint EncodePortalNode(uint nodeId)
    {
        if (nodeId > MaxPortalNodeId)
            throw new ArgumentOutOfRangeException(
                nameof(nodeId),
                nodeId,
                $"Portal stencil node ID must be in the range 0..{MaxPortalNodeId}.");

        return nodeId << PortalNodeShift;
    }

    /// <summary>
    /// Extracts the portal node identifier (<c>0..31</c>) from an 8-bit scene stencil value.
    /// </summary>
    /// <param name="stencilValue">The raw 8-bit stencil value.</param>
    /// <returns>The decoded portal node identifier (<c>0</c> for the primary view).</returns>
    public static uint DecodePortalNode(uint stencilValue)
        => (stencilValue & PortalNodeMask) >> PortalNodeShift;

    /// <summary>
    /// Computes the stencil write mask for a single-pass <c>StencilOp.Invert</c> transition
    /// from <paramref name="fromNodeId"/> to <paramref name="toNodeId"/> when paired with
    /// <c>CompareOp.Equal</c>, <c>CompareMask = PortalNodeMask</c>, and
    /// <c>Reference = EncodePortalNode(fromNodeId)</c>. Because <c>Invert</c> flips only the
    /// bits set in the write mask, <c>from ^ (from ^ to) == to</c> while leaving bits 0..1
    /// and bit 7 untouched.
    /// </summary>
    public static uint ComputePortalTransitionWriteMask(uint fromNodeId, uint toNodeId)
        => (EncodePortalNode(fromNodeId) ^ EncodePortalNode(toNodeId)) & PortalNodeMask;
}
