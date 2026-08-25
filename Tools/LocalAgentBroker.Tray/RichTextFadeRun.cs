namespace XREngine.LocalAgentBroker.Tray;

/// <summary>One text range participating in the chunk fade-in animation.</summary>
internal readonly record struct RichTextFadeRun(int Start, int Length, Color TargetColor);
