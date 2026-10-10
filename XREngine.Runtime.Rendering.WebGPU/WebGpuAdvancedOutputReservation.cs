namespace XREngine.Rendering.WebGPU;

/// <summary>One output owner's reservation and independently completion-retained recorded family.</summary>
internal sealed class WebGpuAdvancedOutputReservation
{
    internal AdvancedVisibilityFamilyReservation Reservation;
    internal XRRenderPipelineInstance? Owner;
    internal EAdvancedOutputReservationBankState State;
    internal uint RecordingSequence;
    internal uint SubmittedSequence;
    internal int NextOperation;
    internal long ResourceGeneration;
    internal AdvancedVisibilityStageBackendRequest Request;
    internal RenderFrameViewSelection FrozenView;
}
