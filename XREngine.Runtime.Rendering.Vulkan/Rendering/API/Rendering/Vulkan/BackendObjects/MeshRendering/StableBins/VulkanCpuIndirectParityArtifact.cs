using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fixed-capacity evidence artifact for validating CPU-direct indexed draws
/// against a CPU-built indirect command stream. It is intentionally detached
/// from lane resolution and command recording: a mismatch rejects only this
/// diagnostic artifact and can never select a fallback submission path.
/// </summary>
internal sealed class VulkanCpuIndirectParityArtifact
{
    private readonly DrawIndexedIndirectCommand[] _indirectCommands;
    private readonly VulkanDirectIndirectParityRecord[] _directRecords;
    private readonly VulkanDirectIndirectParityRecord[] _indirectRecords;

    internal VulkanCpuIndirectParityArtifact(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _indirectCommands = new DrawIndexedIndirectCommand[capacity];
        _directRecords = new VulkanDirectIndirectParityRecord[capacity];
        _indirectRecords = new VulkanDirectIndirectParityRecord[capacity];
    }

    internal int Capacity => _indirectCommands.Length;
    internal int Count { get; private set; }
    internal bool IsSealed { get; private set; }
    internal VulkanCpuIndirectParityFailure Failure { get; private set; }
    internal int MismatchIndex { get; private set; } = -1;
    internal ReadOnlySpan<DrawIndexedIndirectCommand> IndirectCommands
        => _indirectCommands.AsSpan(0, Count);
    internal ReadOnlySpan<VulkanDirectIndirectParityRecord> DirectRecords
        => _directRecords.AsSpan(0, Count);
    internal ReadOnlySpan<VulkanDirectIndirectParityRecord> IndirectRecords
        => _indirectRecords.AsSpan(0, Count);

    internal void Reset()
    {
        Count = 0;
        IsSealed = false;
        Failure = VulkanCpuIndirectParityFailure.None;
        MismatchIndex = -1;
    }

    /// <summary>Records an explicit diagnostic precondition rejection.</summary>
    internal void Reject(VulkanCpuIndirectParityFailure failure)
    {
        Reset();
        Failure = failure;
    }

    /// <summary>
    /// Produces the CPU indirect stream and independently compares its
    /// reconstructed records with the direct frozen records. The caller must
    /// supply only an already-frozen stable-bin stream.
    /// </summary>
    internal bool TryBuild(
        ReadOnlySpan<VulkanPreparedStableBinRecord> records)
    {
        Reset();
        if (records.Length > _indirectCommands.Length)
        {
            Failure = VulkanCpuIndirectParityFailure.CapacityExceeded;
            return false;
        }

        for (int index = 0; index < records.Length; ++index)
        {
            VulkanPreparedStableBinRecord record = records[index];
            VulkanPreparedVisibilityDirectDraw direct =
                record.VisibilityDirectDraw;
            if (!direct.IsValid)
            {
                Failure = VulkanCpuIndirectParityFailure.InvalidFrozenDraw;
                MismatchIndex = index;
                return false;
            }

            // Keep direct and indirect construction independent. The command
            // is the only source for the indirect comparison record below.
            DrawIndexedIndirectCommand indirect = new()
            {
                IndexCount = direct.IndexCount,
                InstanceCount = direct.InstanceCount,
                FirstIndex = direct.FirstIndex,
                VertexOffset = direct.VertexOffset,
                FirstInstance = direct.FirstInstance,
            };
            _indirectCommands[index] = indirect;
            _directRecords[index] = new VulkanDirectIndirectParityRecord(
                record.Template,
                direct.IndexCount,
                direct.InstanceCount,
                direct.FirstIndex,
                direct.VertexOffset,
                direct.FirstInstance,
                record.VisibilityMaterialIndex,
                record.VisibilityObjectIndex,
                checked((ulong)index));
            _indirectRecords[index] = new VulkanDirectIndirectParityRecord(
                record.Template,
                indirect.IndexCount,
                indirect.InstanceCount,
                indirect.FirstIndex,
                indirect.VertexOffset,
                indirect.FirstInstance,
                record.VisibilityMaterialIndex,
                record.VisibilityObjectIndex,
                checked((ulong)index));
        }

        Count = records.Length;
        for (int index = 0; index < Count; ++index)
        {
            if (_directRecords[index].Matches(in _indirectRecords[index]))
                continue;

            Failure = VulkanCpuIndirectParityFailure.RecordMismatch;
            MismatchIndex = index;
            return false;
        }

        IsSealed = true;
        return true;
    }
}

/// <summary>Evidence-only CPU indirect parity outcome. It never changes lanes.</summary>
internal enum VulkanCpuIndirectParityFailure : byte
{
    None = 0,
    FrozenStreamUnavailable = 1,
    CapacityExceeded = 2,
    InvalidFrozenDraw = 3,
    RecordMismatch = 4,
}
