using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanShaderReflection
{
    /// <summary>
    /// Certifies the compiled forward entry's two color outputs. Call this only
    /// for an artifact produced by the editor fragment adapter, once per link.
    /// </summary>
    internal static bool ValidateEditorHighlightOutputs(byte[] spirv, uint maskLocation, out string failureReason)
        => ValidateEditorHighlightOutputs(spirv, maskLocation, uint.MaxValue, out failureReason);

    internal static bool ValidateEditorHighlightOutputs(byte[] spirv, uint maskLocation, uint pickingLocation, out string failureReason)
    {
        failureReason = string.Empty;
        if (maskLocation == 0 || spirv.Length < 20 || spirv.Length % sizeof(uint) != 0 ||
            MemoryMarshal.Read<uint>(spirv) != 0x07230203u)
        {
            failureReason = "The editor mask requires valid SPIR-V and a separate color location.";
            return false;
        }
        try
        {
            return new SpirvModule(spirv, ShaderStageFlags.FragmentBit)
                .ValidateEditorHighlightOutputs(maskLocation, pickingLocation, out failureReason);
        }
        catch (InvalidOperationException exception)
        {
            failureReason = "The editor mask output interface could not be decoded: " + exception.Message;
            return false;
        }
    }

    private sealed partial class SpirvModule
    {
        private HashSet<uint>? _fragmentMainInterface;
        private int _fragmentMainCount;
        private bool _hasDecorationGroups;

        internal bool ValidateEditorHighlightOutputs(uint maskLocation, uint pickingLocation, out string failureReason)
        {
            failureReason = string.Empty;
            if (_fragmentMainCount != 1 || _fragmentMainInterface is null || _hasDecorationGroups)
            {
                failureReason = "The editor mask requires one fragment main entry and direct output decorations.";
                return false;
            }

            bool hasSceneColor = false;
            bool hasMask = false;
            bool hasPicking = pickingLocation == uint.MaxValue;
            foreach (uint id in _fragmentMainInterface)
            {
                if (!_variables.TryGetValue(id, out SpirvVariable? variable))
                {
                    failureReason = "The fragment entry refers to an unknown interface variable.";
                    return false;
                }
                if (variable.StorageClass != SpirvStorageClass.Output)
                    continue;
                _decorations.TryGetValue(id, out SpirvDecorations? decoration);
                if (decoration?.BuiltIn == true)
                    continue;
                if (decoration?.Location is not uint location ||
                    decoration.Index.GetValueOrDefault() != 0 || decoration.Component.GetValueOrDefault() != 0 ||
                    !IsFloat4Output(variable))
                {
                    failureReason = "The editor mask requires direct float32 vec4 outputs with index and component zero.";
                    return false;
                }
                if (location == 0 && !hasSceneColor)
                    hasSceneColor = true;
                else if (location == maskLocation && !hasMask)
                    hasMask = true;
                else if (location == pickingLocation && !hasPicking)
                    hasPicking = true;
                else
                {
                    failureReason = "The fragment entry has an additional or conflicting color output.";
                    return false;
                }
            }
            if (hasSceneColor && hasMask && hasPicking)
                return true;
            failureReason = "The fragment entry must expose scene color and each editor output.";
            return false;
        }

        private bool IsFloat4Output(SpirvVariable variable)
            => _types.TryGetValue(variable.TypeId, out SpirvType? pointer) &&
                pointer.Kind == SpirvTypeKind.Pointer && pointer.StorageClass == SpirvStorageClass.Output &&
                pointer.ElementTypeId is uint vectorId && _types.TryGetValue(vectorId, out SpirvType? vector) &&
                vector.Kind == SpirvTypeKind.Vector && vector.ComponentCount == 4 &&
                vector.ElementTypeId is uint scalarId && _types.TryGetValue(scalarId, out SpirvType? scalar) &&
                scalar.Kind == SpirvTypeKind.Float && scalar.Width == 32;

        private void ParseFragmentEntryPoint(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3 || operands[0] != 4u)
                return;
            ReadOnlySpan<byte> nameBytes = MemoryMarshal.Cast<uint, byte>(operands[2..]);
            int end = nameBytes.IndexOf((byte)0);
            if (end < 0)
                throw new InvalidOperationException("The fragment entry name is not terminated.");
            if (!nameBytes[..end].SequenceEqual("main"u8))
                return;
            ++_fragmentMainCount;
            _fragmentMainInterface ??= [];
            int firstInterfaceOperand = 2 + (end + 4) / 4;
            foreach (uint id in operands[firstInterfaceOperand..])
                _fragmentMainInterface.Add(id);
        }

        private void ParseOutputFloatType(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 2)
                _types[operands[0]] = new SpirvType(operands[0])
                {
                    Kind = SpirvTypeKind.Float,
                    Width = operands[1],
                };
        }

        private void ParseOutputVectorType(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 3)
                _types[operands[0]] = new SpirvType(operands[0])
                {
                    Kind = SpirvTypeKind.Vector,
                    ElementTypeId = operands[1],
                    ComponentCount = operands[2],
                };
        }
    }
}
