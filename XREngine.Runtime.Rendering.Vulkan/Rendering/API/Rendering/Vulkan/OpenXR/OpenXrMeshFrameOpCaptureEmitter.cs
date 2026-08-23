namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable adapter that keeps deferred mesh requests inside the same capture as
/// direct OpenXR frame operations without allocating a closure per eye.
/// </summary>
internal sealed class OpenXrMeshFrameOpCaptureEmitter : IOpenXrEyeFrameOpEmitter
{
    private readonly VulkanFrameLoop _owner;
    private Action? _actionEmitter;
    private IOpenXrEyeFrameOpEmitter? _eyeEmitter;

    internal OpenXrMeshFrameOpCaptureEmitter(VulkanFrameLoop owner)
    {
        _owner = owner;
        Action = EmitAction;
    }

    internal Action Action { get; }

    internal void Bind(Action emitter)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        EnsureUnbound();
        _actionEmitter = emitter;
    }

    internal void Bind(IOpenXrEyeFrameOpEmitter emitter)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        EnsureUnbound();
        _eyeEmitter = emitter;
    }

    internal void Unbind()
    {
        _actionEmitter = null;
        _eyeEmitter = null;
    }

    public void Emit(in OpenXrEyeFrameOpEmission emission)
    {
        IOpenXrEyeFrameOpEmitter emitter = _eyeEmitter
            ?? throw new InvalidOperationException(
                "The OpenXR eye mesh-capture emitter has no bound source emitter.");
        _owner.EmitOpenXrEyeFrameOpsWithCapturedMeshRequests(
            emitter,
            in emission);
    }

    private void EmitAction()
    {
        Action emitter = _actionEmitter
            ?? throw new InvalidOperationException(
                "The OpenXR mesh-capture action has no bound source emitter.");
        _owner.EmitOpenXrFrameOpsWithCapturedMeshRequests(emitter);
    }

    private void EnsureUnbound()
    {
        if (_actionEmitter is not null || _eyeEmitter is not null)
        {
            throw new InvalidOperationException(
                "Nested OpenXR mesh/frame-operation captures are not supported.");
        }
    }
}
