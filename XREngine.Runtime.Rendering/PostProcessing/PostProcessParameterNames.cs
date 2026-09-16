namespace XREngine.Rendering.PostProcessing;

public static class PostProcessParameterNames
{
    public const string TonemappingOperator = "Tonemapping";
    public const string MobiusTransition = "MobiusTransition";

    public const string TemporalFeedbackMin = "FeedbackMin";
    public const string TemporalFeedbackMax = "FeedbackMax";
    public const string TemporalVarianceGamma = "VarianceGamma";
    public const string TemporalCatmullRadius = "CatmullRadius";
    public const string TemporalDepthRejectThreshold = "DepthRejectThreshold";
    public const string TemporalReactiveTransparencyRange = "ReactiveTransparencyRange";
    public const string TemporalReactiveVelocityScale = "ReactiveVelocityScale";
    public const string TemporalReactiveLumaThreshold = "ReactiveLumaThreshold";
    public const string TemporalDepthDiscontinuityScale = "DepthDiscontinuityScale";
    public const string TemporalConfidencePower = "ConfidencePower";
    public const string TemporalDebugViewMode = "DebugViewMode";
}
