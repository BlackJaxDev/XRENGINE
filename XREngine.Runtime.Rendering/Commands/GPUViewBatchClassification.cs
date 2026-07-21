using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// GPU-resident classification metadata for one compacted visible command.
/// The exact view mask is copied from culling and is never inferred from a
/// representative eye.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GPUViewBatchClassification
{
    public uint ExactViewMaskLo;
    public uint ExactViewMaskHi;
    public uint PackedPassPipelineState;
    public uint MaterialID;
    public uint MeshID;
    public uint LodLevel;
    public uint DrawID;
    public uint Flags;
}

[Flags]
public enum GPUViewBatchClassificationFlags : uint
{
    None = 0u,
    Transparent = 1u << 0,
    RequiresPerViewLod = 1u << 1,
    RequiresPerViewSort = 1u << 2,
    TraditionalLayerSuppressionEligible = 1u << 3,
    MeshletTaskSuppressionEligible = 1u << 4,
    ExactMaskUnavailable = 1u << 5,
    TransparentExactDomain = 1u << 6,
}

public enum EGpuViewBatchSubmissionPolicy : uint
{
    ConservativeUnion = 0u,
    TraditionalLayerSuppression = 1u,
    MeshletTaskSuppression = 2u,
}

public static class GPUViewBatchClassificationLayout
{
    public const uint UIntCount = 8u;
    public static readonly uint Stride = (uint)Marshal.SizeOf<GPUViewBatchClassification>();
}
