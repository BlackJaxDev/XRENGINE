using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    private readonly record struct FrameOpResourcePlannerPreparationToken(
        FrameOpResourcePlannerSwitchingState? SwitchingState,
        ResourcePlannerRuntimeState PreviousState,
        FrameOp[]? Operations,
        bool Active);

    private FrameOpResourcePlannerPreparationToken BeginFrameOpResourcePlannerPreparation(FrameOp[] operations)
    {
        if (!_deviceContext.IsOperational || !FrameOpResourcePlannerSwitchingEnabled || operations.Length == 0)
            return default;

        return new(
            ActiveFrameOpResourcePlannerSwitchingState,
            CaptureResourcePlannerRuntimeState(),
            operations,
            true);
    }

    private ResourcePlannerRuntimeState PublishFrameOpResourcePlannerPreparationState(
        in FrameOpResourcePlannerPreparationToken token)
    {
        ResourcePlannerRuntimeState state = CaptureResourcePlannerRuntimeState();
        if (!token.Active || token.SwitchingState is null || token.Operations is not { Length: > 0 })
            return state;

        VulkanFrameOpPlannerStateKey key = VulkanFramePlanner.BuildFrameOpPlannerStateKey(
            VulkanFramePlanner.SelectPrimaryPlannerContext(token.Operations));
        token.SwitchingState.States[key] = state;
        MarkFrameOpResourcePlannerStateUsed(token.SwitchingState, key);
        return state;
    }

    private void EndFrameOpResourcePlannerPreparation(in FrameOpResourcePlannerPreparationToken token)
    {
        if (!token.Active)
            return;

        RestoreUsableFrameOpPlannerState(token.PreviousState);
    }
}
