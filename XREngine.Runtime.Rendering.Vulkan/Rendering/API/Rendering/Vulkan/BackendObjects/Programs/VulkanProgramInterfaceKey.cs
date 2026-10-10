using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one immutable Vulkan program interface in a device generation.</summary>
internal sealed class VulkanProgramInterfaceKey : IEquatable<VulkanProgramInterfaceKey>
{
    private readonly VulkanProgramInterfaceStage[] _stages;
    private readonly int _hash;

    internal VulkanProgramInterfaceKey(
        VulkanProgramInterfaceStage[] stages,
        int shaderConfigVersion,
        bool clipDepthRemap,
        EShaderType? clipDepthRemapStage,
        ulong transformFeedbackLayoutVersion,
        EVulkanDescriptorBackend descriptorBackend,
        uint externalSetMask,
        ulong externalGlobalLayout,
        ulong externalVisibilityLayout,
        ulong externalResourceLayout,
        ShaderStageFlags pushStages,
        uint pushOffset,
        uint pushSize,
        bool validateDescriptorContract)
    {
        _stages = stages;
        ShaderConfigVersion = shaderConfigVersion;
        ClipDepthRemap = clipDepthRemap;
        ClipDepthRemapStage = clipDepthRemapStage;
        TransformFeedbackLayoutVersion = transformFeedbackLayoutVersion;
        DescriptorBackend = descriptorBackend;
        ExternalSetMask = externalSetMask;
        ExternalGlobalLayout = externalGlobalLayout;
        ExternalVisibilityLayout = externalVisibilityLayout;
        ExternalResourceLayout = externalResourceLayout;
        PushStages = pushStages;
        PushOffset = pushOffset;
        PushSize = pushSize;
        ValidateDescriptorContract = validateDescriptorContract;

        HashCode hash = new();
        hash.Add(shaderConfigVersion);
        hash.Add(clipDepthRemap);
        hash.Add(clipDepthRemapStage);
        hash.Add(transformFeedbackLayoutVersion);
        hash.Add(descriptorBackend);
        hash.Add(externalSetMask);
        hash.Add(externalGlobalLayout);
        hash.Add(externalVisibilityLayout);
        hash.Add(externalResourceLayout);
        hash.Add(pushStages);
        hash.Add(pushOffset);
        hash.Add(pushSize);
        hash.Add(validateDescriptorContract);
        foreach (VulkanProgramInterfaceStage stage in stages)
        {
            hash.Add(stage.Slot);
            hash.Add(RuntimeHelpers.GetHashCode(stage.Shader));
            hash.Add(RuntimeHelpers.GetHashCode(stage.Wrapper));
            hash.Add(stage.SourceRevision);
            hash.Add(stage.ModuleHandle);
            hash.Add(stage.ArtifactIdentity, StringComparer.Ordinal);
            hash.Add(stage.EntryPoint, StringComparer.Ordinal);
        }
        _hash = hash.ToHashCode();
    }

    internal int ShaderConfigVersion { get; }
    internal int StageCount => _stages.Length;
    internal bool ClipDepthRemap { get; }
    internal EShaderType? ClipDepthRemapStage { get; }
    internal ulong TransformFeedbackLayoutVersion { get; }
    internal EVulkanDescriptorBackend DescriptorBackend { get; }
    internal uint ExternalSetMask { get; }
    internal ulong ExternalGlobalLayout { get; }
    internal ulong ExternalVisibilityLayout { get; }
    internal ulong ExternalResourceLayout { get; }
    internal ShaderStageFlags PushStages { get; }
    internal uint PushOffset { get; }
    internal uint PushSize { get; }
    internal bool ValidateDescriptorContract { get; }

    public bool Equals(VulkanProgramInterfaceKey? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || _hash != other._hash || _stages.Length != other._stages.Length ||
            ShaderConfigVersion != other.ShaderConfigVersion ||
            ClipDepthRemap != other.ClipDepthRemap ||
            ClipDepthRemapStage != other.ClipDepthRemapStage ||
            TransformFeedbackLayoutVersion != other.TransformFeedbackLayoutVersion ||
            DescriptorBackend != other.DescriptorBackend ||
            ExternalSetMask != other.ExternalSetMask ||
            ExternalGlobalLayout != other.ExternalGlobalLayout ||
            ExternalVisibilityLayout != other.ExternalVisibilityLayout ||
            ExternalResourceLayout != other.ExternalResourceLayout ||
            PushStages != other.PushStages || PushOffset != other.PushOffset ||
            PushSize != other.PushSize ||
            ValidateDescriptorContract != other.ValidateDescriptorContract)
            return false;

        for (int index = 0; index < _stages.Length; ++index)
        {
            VulkanProgramInterfaceStage left = _stages[index];
            VulkanProgramInterfaceStage right = other._stages[index];
            if (left.Slot != right.Slot ||
                !ReferenceEquals(left.Shader, right.Shader) ||
                !ReferenceEquals(left.Wrapper, right.Wrapper) ||
                left.SourceRevision != right.SourceRevision ||
                left.ModuleHandle != right.ModuleHandle ||
                !StringComparer.Ordinal.Equals(left.ArtifactIdentity, right.ArtifactIdentity) ||
                !StringComparer.Ordinal.Equals(left.EntryPoint, right.EntryPoint))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj)
        => obj is VulkanProgramInterfaceKey other && Equals(other);

    public override int GetHashCode() => _hash;
}
