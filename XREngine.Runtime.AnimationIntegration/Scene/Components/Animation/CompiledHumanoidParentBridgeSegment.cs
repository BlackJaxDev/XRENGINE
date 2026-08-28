using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// One concrete transform between a mapped semantic child and its nearest mapped
/// ancestor. Auxiliary segments are evaluated from the current scratch pose;
/// all other helper transforms remain their finalized neutral matrices.
/// </summary>
internal readonly struct CompiledHumanoidParentBridgeSegment
{
    public CompiledHumanoidParentBridgeSegment(Matrix4x4 neutralLocalTransform, int auxiliaryBoneIndex)
    {
        NeutralLocalTransform = neutralLocalTransform;
        AuxiliaryBoneIndex = auxiliaryBoneIndex;
    }

    public Matrix4x4 NeutralLocalTransform { get; }
    /// <summary>Auxiliary-bone index, or -1 for a fixed neutral helper.</summary>
    public int AuxiliaryBoneIndex { get; }
}
