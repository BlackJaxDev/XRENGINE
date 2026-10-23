namespace XREngine.Rendering.UI;

/// <summary>Retains the resolved worker input with its calculated output.</summary>
internal sealed record UILayoutWorkerResult(UILayoutSnapshot ResolvedInput, UILayoutOutputPageView Output);
