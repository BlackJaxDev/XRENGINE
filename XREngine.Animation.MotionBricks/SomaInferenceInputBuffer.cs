namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Preallocated host tensor buffers and encoding scratch space for one SOMA MotionBricks inference call.
/// Reuse one instance per inference session to keep encoding free of heap allocations.
/// </summary>
public sealed class SomaInferenceInputBuffer
{
    public float[] GlobalRootValues { get; } = new float[SomaModelContract.GlobalRootTensorElementCount];
    public int[] HasGlobalRootValues { get; } = new int[SomaModelContract.MaskTensorElementCount];
    public float[] LocalRootValues { get; } = new float[SomaModelContract.LocalRootTensorElementCount];
    public int[] HasLocalRootValues { get; } = new int[SomaModelContract.MaskTensorElementCount];
    public float[] LocalPoses { get; } = new float[SomaModelContract.LocalPosesTensorElementCount];
    public int[] HasLocalPoses { get; } = new int[SomaModelContract.MaskTensorElementCount];
    public int[] AllowedPredNumTokens { get; } = new int[SomaModelContract.AllowedTokensTensorElementCount];

    /// <summary>
    /// Flat scratch storage for the 8 world-space guide poses (4 context + 4 target) x 30 joints.
    /// </summary>
    internal SomaPoseTransform[] GuideWorldPoses { get; } =
        new SomaPoseTransform[SomaModelContract.ContextAndTargetPoseCount * SomaModelContract.JointCount];

    /// <summary>
    /// Alignment transform computed from the last context pose during encoding and reused during decoding.
    /// </summary>
    public SomaPoseTransform GuideAlignment { get; internal set; } = SomaPoseTransform.Identity;

    public void Clear()
    {
        Array.Clear(GlobalRootValues);
        Array.Clear(HasGlobalRootValues);
        Array.Clear(LocalRootValues);
        Array.Clear(HasLocalRootValues);
        Array.Clear(LocalPoses);
        Array.Clear(HasLocalPoses);
        Array.Clear(AllowedPredNumTokens);
        Array.Clear(GuideWorldPoses);
        GuideAlignment = SomaPoseTransform.Identity;
    }
}
