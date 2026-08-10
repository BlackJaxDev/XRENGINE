namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mutable producer-side partition state for frame-operation resource plans. The type is
/// top-level so command-worker contracts never depend on the renderer facade's nested types.
/// </summary>
internal sealed class FrameOpResourcePlannerSwitchingState
{
    public Dictionary<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> States { get; } =
        new(VulkanFrameOpPlannerStateKeyComparer.Instance);
    public Dictionary<VulkanFrameOpPlannerStateKey, ulong> LastUsedSerials { get; } =
        new(VulkanFrameOpPlannerStateKeyComparer.Instance);
    public HashSet<VulkanFrameOpPlannerStateKey> ActiveKeys { get; } =
        new(VulkanFrameOpPlannerStateKeyComparer.Instance);
    public ulong UsageSerial;
    public bool SwitchingActive;
    public bool MergedPlanActive;
    public bool RecordingScopeActive;
    public bool HasActiveKey;
    public VulkanFrameOpPlannerStateKey ActiveKey;
    public bool HasActiveContext;
    public FrameOpContext ActiveContext;
    public ResourcePlannerRuntimeState PreparationState;
    public bool HasPreparationState;
    public ulong PreparedFrameOpsSignature;
    public ulong PreparedPlanRevision;
    public bool HasPreparedPlan;
}
