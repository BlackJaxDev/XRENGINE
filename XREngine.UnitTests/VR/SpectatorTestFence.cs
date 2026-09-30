using XREngine.Rendering;
namespace XREngine.UnitTests.VR;
internal sealed class SpectatorTestFence : XRGpuFence
{
    public EGpuFenceStatus Status { get; set; } = EGpuFenceStatus.Pending;
    public EGpuFenceSubmissionStatus Submission { get; set; } = EGpuFenceSubmissionStatus.Submitted;
    public override EGpuFenceSubmissionStatus SubmissionStatus => Submission;
    public int PollCount { get; private set; }
    protected override EGpuFenceStatus PollCore() { ++PollCount; return Status; }
    protected override void DisposeCore() { }
}
