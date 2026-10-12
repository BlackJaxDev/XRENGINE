namespace XREngine.Rendering;

/// <summary>Defers a retained resource generation while its backend creates an owned physical dependency.</summary>
public class RenderResourcePreparationPendingException(string message) : Exception(message);
