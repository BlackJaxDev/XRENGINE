namespace XREngine.RenderBench;

public enum RenderBenchFixtureKind
{
    Control,
    CommandChainSignature,
    PacketLowering,
    PrimaryCommandEncoding,
    SecondaryCommandRecording,
    CommandBufferReuse,
    DescriptorPublication,
    ResourcePlanning,
    QueueSubmission,
    Upload,
    GpuPass,
    FullPresentationless,
}
