namespace XREngine.Rendering.UI;

/// <summary>One atomic backend identity and revision.</summary>
internal sealed record UITextShapingBackendState(IUITextShapingBackend? Backend, long Generation);
