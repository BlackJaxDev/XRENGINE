namespace XREngine;

public readonly record struct ViewRecordingWorkItem(
    EVrViewRenderMode RenderMode,
    int WorkerIndex,
    ViewRenderContext View,
    ViewFoveationContext Foveation)
{
    public bool HasImmutableFoveationInput => Foveation.Equals(View.Foveation);
}
