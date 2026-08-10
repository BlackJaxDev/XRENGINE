namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Transitional adapter for producers which still naturally express a render pass
/// as a closure. The immutable prepared command input never retains this adapter.
/// </summary>
internal sealed class OpenXrEyeFrameOpDelegateEmitter(Action emit) : IOpenXrEyeFrameOpEmitter
{
    public void Emit(in OpenXrEyeFrameOpEmission emission) => emit();
}
