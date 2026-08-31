using System.Text.Json.Serialization;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one accepted presentationless production submission. A receipt is
/// owned by the host that produced it. Earlier submissions remain queryable for
/// completion; diagnostic readback requires the most recent production submission.
/// </summary>
public readonly struct VulkanExplicitProductionSubmissionReceipt : IEquatable<VulkanExplicitProductionSubmissionReceipt>
{
    [JsonConstructor]
    internal VulkanExplicitProductionSubmissionReceipt(
        ulong ownerIdentity,
        long backendGeneration,
        ulong deviceHandle,
        ulong explicitFrameNumber,
        ulong engineFrameId,
        uint expectedFrameSlot,
        ulong targetGeneration,
        ulong commandBufferHandle,
        ulong graphicsTimelineSignal)
    {
        OwnerIdentity = ownerIdentity;
        BackendGeneration = backendGeneration;
        DeviceHandle = deviceHandle;
        ExplicitFrameNumber = explicitFrameNumber;
        EngineFrameId = engineFrameId;
        ExpectedFrameSlot = expectedFrameSlot;
        TargetGeneration = targetGeneration;
        CommandBufferHandle = commandBufferHandle;
        GraphicsTimelineSignal = graphicsTimelineSignal;
    }

    /// <summary>Opaque identity of the renderer instance that accepted the submission.</summary>
    public ulong OwnerIdentity { get; }
    public long BackendGeneration { get; }
    public ulong DeviceHandle { get; }
    public ulong ExplicitFrameNumber { get; }
    public ulong EngineFrameId { get; }
    public uint ExpectedFrameSlot { get; }
    public ulong TargetGeneration { get; }
    public ulong CommandBufferHandle { get; }
    public ulong GraphicsTimelineSignal { get; }
    public bool IsValid => OwnerIdentity != 0UL && ExplicitFrameNumber != 0UL && GraphicsTimelineSignal != 0UL;

    public bool Equals(VulkanExplicitProductionSubmissionReceipt other)
        => OwnerIdentity == other.OwnerIdentity &&
           BackendGeneration == other.BackendGeneration &&
           DeviceHandle == other.DeviceHandle &&
           ExplicitFrameNumber == other.ExplicitFrameNumber &&
           EngineFrameId == other.EngineFrameId &&
           ExpectedFrameSlot == other.ExpectedFrameSlot &&
           TargetGeneration == other.TargetGeneration &&
           CommandBufferHandle == other.CommandBufferHandle &&
           GraphicsTimelineSignal == other.GraphicsTimelineSignal;

    public override bool Equals(object? obj)
        => obj is VulkanExplicitProductionSubmissionReceipt other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(OwnerIdentity);
        hash.Add(BackendGeneration);
        hash.Add(DeviceHandle);
        hash.Add(ExplicitFrameNumber);
        hash.Add(EngineFrameId);
        hash.Add(ExpectedFrameSlot);
        hash.Add(TargetGeneration);
        hash.Add(CommandBufferHandle);
        hash.Add(GraphicsTimelineSignal);
        return hash.ToHashCode();
    }

    public static bool operator ==(VulkanExplicitProductionSubmissionReceipt left, VulkanExplicitProductionSubmissionReceipt right)
        => left.Equals(right);

    public static bool operator !=(VulkanExplicitProductionSubmissionReceipt left, VulkanExplicitProductionSubmissionReceipt right)
        => !left.Equals(right);
}
