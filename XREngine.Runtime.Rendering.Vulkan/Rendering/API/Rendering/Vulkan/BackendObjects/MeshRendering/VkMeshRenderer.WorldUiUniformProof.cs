using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>Checks the exact native crop and model bytes bound for this draw.</summary>
    private bool WorldUiUniformsMatch(
        UIPublishedWorldMeshCommand command,
        in PendingMeshDraw draw,
        XRMaterial material,
        VulkanProgramInterfaceEntry entry,
        in VulkanWorldUiDescriptorBindingWitness witness,
        int frameIndex,
        int drawUniformSlot)
    {
        if (!command.WorldMatrixIsModelMatrix)
            return command.WorldCrop is null;
        if (draw.ModelMatrix != command.WorldMatrix ||
            entry.Generation != witness.InterfaceGeneration ||
            entry.PipelineLayout.Handle != witness.LayoutHandle ||
            _program is not { } currentProgram ||
            currentProgram.DescriptorLayoutFingerprint != entry.LayoutFingerprint ||
            currentProgram.DescriptorSchemaFingerprint != entry.SchemaFingerprint ||
            !HasCurrentWorldUiDescriptorSlot(in witness, material) ||
            !VulkanWorldUiUniformContract.TryFindMember(entry.AutoUniformBlocks,
                "ModelMatrix", EShaderType.Vertex, EShaderVarType._mat4,
                out AutoUniformBlockInfo? modelBlock,
                out AutoUniformMember modelMember))
            return false;

        Matrix4x4 worldMatrix = command.WorldMatrix;
        if (!WorldUiUniformMemberMatches(modelBlock!, modelMember,
                in worldMatrix, frameIndex, drawUniformSlot, in witness))
            return false;
        if (command.WorldCrop is null)
            return HasCurrentWorldUiDescriptorSlot(in witness, material);
        if (command.WorldCropMinMax is not { } minMax ||
            command.WorldCropModelToCanvas is not { } modelToCanvas ||
            !VulkanWorldUiUniformContract.TryFindMember(entry.AutoUniformBlocks,
                UIWorldCropShaderContract.MinMaxUniform,
                EShaderType.Fragment, EShaderVarType._vec4,
                out AutoUniformBlockInfo? cropBlock,
                out AutoUniformMember cropMember) ||
            !VulkanWorldUiUniformContract.TryFindMember(entry.AutoUniformBlocks,
                UIWorldCropShaderContract.MatrixUniform,
                EShaderType.Fragment, EShaderVarType._mat4,
                out AutoUniformBlockInfo? matrixBlock,
                out AutoUniformMember matrixMember))
            return false;

        return WorldUiUniformMemberMatches(cropBlock!, cropMember,
                   in minMax, frameIndex, drawUniformSlot, in witness) &&
               WorldUiUniformMemberMatches(matrixBlock!, matrixMember,
                   in modelToCanvas, frameIndex, drawUniformSlot, in witness) &&
               HasCurrentWorldUiDescriptorSlot(in witness, material);
    }

    private bool WorldUiUniformMemberMatches<T>(
        AutoUniformBlockInfo block, AutoUniformMember member,
        in T expected, int frameIndex, int drawUniformSlot,
        in VulkanWorldUiDescriptorBindingWitness witness)
        where T : unmanaged
    {
        if (_program is null ||
            !_autoUniformBuffers.TryGetValue(block.InstanceName,
                out AutoUniformBuffer[]? buffers) || buffers.Length == 0 ||
            !TryGetWorldUiBoundAutoBuffer(block, buffers, frameIndex,
                drawUniformSlot, in witness, out AutoUniformBuffer target))
            return false;

        int length = Unsafe.SizeOf<T>();
        if (member.Size != length || member.Offset > target.Size ||
            length > target.Size - member.Offset)
            return false;
        if (target.UsesMappedFrameArena)
        {
            if (BackendContext.Resources.MappedFrameArena is not { } arena ||
                !arena.TryBeginRead(target.MappedSlice,
                    out VulkanMappedFrameReadScope read))
                return false;
            using (read)
                return VulkanWorldUiUniformContract.BytesMatch(read.Bytes, member.Offset,
                    in expected);
        }
        VulkanMappedMemorySlice mappedSlice = target.MappedMemorySlice;
        if (!target.UsesMappedMemoryLease ||
            !BackendContext.Resources.Buffers.TryAcquireRead(BackendContext,
                in mappedSlice,
                out VulkanMappedMemoryReadLease lease))
            return false;
        using (lease)
            return VulkanWorldUiUniformContract.BytesMatch(lease.Bytes, member.Offset,
                in expected);
    }

    private bool TryGetWorldUiBoundAutoBuffer(
        AutoUniformBlockInfo block, AutoUniformBuffer[] buffers,
        int frameIndex, int drawUniformSlot,
        in VulkanWorldUiDescriptorBindingWitness witness,
        out AutoUniformBuffer target)
    {
        target = default;
        if (_program is null || witness.Allocation is not { } allocation ||
            block.Set >= witness.SetCount ||
            (witness.ActiveMask & (1u << (int)block.Set)) == 0u)
            return false;
        int index = ResolvePublishedAutoUniformBufferIndex(block, frameIndex,
            drawUniformSlot, buffers.Length);
        target = buffers[index];
        if (target.Buffer.Handle == 0UL || target.Size < block.Size)
            return false;

        DescriptorBindingInfo? matched = null;
        int dynamicOrdinal = 0;
        for (int i = 0; i < _program.DescriptorBindings.Count; i++)
        {
            DescriptorBindingInfo binding = _program.DescriptorBindings[i];
            if (binding.Set != block.Set)
                continue;
            if (binding.Binding == block.Binding)
            {
                if (matched is not null)
                    return false;
                matched = binding;
                break;
            }
            if (binding.DescriptorType == DescriptorType.UniformBufferDynamic)
                dynamicOrdinal += checked((int)
                    VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding));
        }
        if (matched is not { } selected ||
            selected.DescriptorType != DescriptorType.UniformBufferDynamic ||
            VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(selected) != 1u ||
            target.Offset > uint.MaxValue)
            return false;

        VulkanWorldUiDescriptorSetWitness set =
            witness.SetWitnesses[(int)block.Set];
        if (dynamicOrdinal >= set.OffsetCount ||
            witness.DynamicOffsets[set.OffsetStart + dynamicOrdinal] !=
                (uint)target.Offset)
            return false;

        VkMeshRenderer.DescriptorWriteKey key = new(witness.Slot,
            set.Handle, block.Set, block.Binding, selected.DescriptorType, 1u);
        FrameOpSignatureHasher hash = new();
        hash.Add((int)selected.DescriptorType);
        hash.Add(1u);
        hash.Add(target.Buffer.Handle);
        hash.Add(GetResourceGeneration(ObjectType.Buffer,
            target.Buffer.Handle));
        hash.Add(0UL);
        hash.Add((ulong)block.Size);
        return allocation.DescriptorWriteSignatures.TryGetValue(key,
                   out ulong publishedSignature) &&
               publishedSignature == hash.ToHash() &&
               WorldUiBoundSetCurrent(in witness, (int)block.Set);
    }

}
