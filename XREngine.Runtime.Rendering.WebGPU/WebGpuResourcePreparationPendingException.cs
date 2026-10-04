namespace XREngine.Rendering.WebGPU;

/// <summary>Defers a scene while a required texture producer has no accepted or current-frame image.</summary>
internal sealed class WebGpuResourcePreparationPendingException(string message) : RenderResourcePreparationPendingException(message);
