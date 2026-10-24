namespace XREngine.Editor;

/// <summary>Contains the display text for one realized profiler row.</summary>
internal readonly record struct NativeProfilerDisplayRow(string Scope, string Total, string Self);
