using System.Numerics;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-independent signatures captured by immutable frame-operation inputs.
/// Keeping these functions beside the snapshots prevents the command path from
/// reaching back through the renderer facade merely to hash already-captured data.
/// </summary>
internal static class VulkanFrameOpSnapshotSignatures
{
    private const int MaxPassMetadataSignatureCacheEntries = 128;

    internal static VulkanFrameOpPlannerStateKey BuildPlannerStateKey(
        in FrameOpContext context)
        => new(
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context),
            context.LogicalViewId,
            context.ResourceRegistrySignatureSnapshot ??
                context.ResourceRegistry?.DescriptorSignature ?? 0,
            ComputePassMetadataSignature(context.PassMetadata),
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.SubmissionQueueFamily);

    /// <summary>
    /// Matches a context to a sealed planner key without rescanning pass metadata
    /// when the immutable metadata publication is the same object captured by
    /// the frame-plan slot.
    /// </summary>
    internal static bool MatchesPlannerStateKey(
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey key,
        IReadOnlyCollection<RenderPassMetadata>? capturedPassMetadata)
    {
        if (context.ContextKind != key.ContextKind ||
            context.PipelineIdentity != key.PipelineIdentity ||
            context.ViewportIdentity != key.ViewportIdentity ||
            context.DisplayWidth != key.DisplayWidth ||
            context.DisplayHeight != key.DisplayHeight ||
            context.InternalWidth != key.InternalWidth ||
            context.InternalHeight != key.InternalHeight ||
            context.OutputFrameBufferIdentity != key.OutputFrameBufferIdentity ||
            ResolveResourcePlanOutputTargetIdentity(context) != key.OutputTargetIdentity ||
            context.LogicalViewId != key.LogicalViewId ||
            (context.ResourceRegistrySignatureSnapshot ??
                context.ResourceRegistry?.DescriptorSignature ?? 0) != key.ResourceRegistrySignature ||
            context.ResourceGeneration != key.ResourceGeneration ||
            context.DescriptorGeneration != key.DescriptorGeneration ||
            context.SubmissionQueueFamily != key.SubmissionQueueFamily)
        {
            return false;
        }

        return ReferenceEquals(context.PassMetadata, capturedPassMetadata) ||
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature;
    }

    internal static ulong HashUniformBindings(
        Dictionary<string, ProgramUniformValue> uniforms)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((string name, ProgramUniformValue value) in uniforms)
        {
            HashCode item = new();
            item.Add(name, StringComparer.Ordinal);
            item.Add((int)value.Type);
            item.Add(value.IsArray);
            HashUniformValue(ref item, value);
            AddUnorderedItemHash(ref xor, ref sum, unchecked((ulong)item.ToHashCode()));
        }

        return FinishUnorderedHash(uniforms.Count, xor, sum);
    }

    internal static ulong HashUniformBindingLayout(
        Dictionary<string, ProgramUniformValue> uniforms)
        => HashBindingNames(uniforms.Keys, uniforms.Count);

    internal static ulong HashUniformBindings(
        Dictionary<string, ProgramUniformValue> uniforms,
        HashSet<string> selectedNames)
    {
        ulong xor = 0;
        ulong sum = 0;
        int count = 0;
        foreach (string name in selectedNames)
        {
            if (!uniforms.TryGetValue(name, out ProgramUniformValue value))
                continue;

            HashCode item = new();
            item.Add(name, StringComparer.Ordinal);
            item.Add((int)value.Type);
            item.Add(value.IsArray);
            HashUniformValue(ref item, value);
            AddUnorderedItemHash(ref xor, ref sum, unchecked((ulong)item.ToHashCode()));
            count++;
        }

        return FinishUnorderedHash(count, xor, sum);
    }

    internal static ulong HashUniformBindings(
        Dictionary<string, ProgramUniformValue> uniforms,
        EUniformRequirements selectedRequirements)
    {
        ulong xor = 0;
        ulong sum = 0;
        int count = 0;
        foreach ((string name, ProgramUniformValue value) in uniforms)
        {
            if ((UniformRequirementsDetection.GetRequirement(name) & selectedRequirements) == 0)
                continue;

            HashCode item = new();
            item.Add(name, StringComparer.Ordinal);
            item.Add((int)value.Type);
            item.Add(value.IsArray);
            HashUniformValue(ref item, value);
            AddUnorderedItemHash(ref xor, ref sum, unchecked((ulong)item.ToHashCode()));
            count++;
        }

        return FinishUnorderedHash(count, xor, sum);
    }

    internal static ulong HashSamplerUnitBindings(
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures,
        bool includeMutableFrameSourceDescriptors)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((uint unit, XRTexture texture) in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(unit);
            if (!includeMutableFrameSourceDescriptors &&
                samplerNamesByUnit.TryGetValue(unit, out string? samplerName) &&
                VulkanMeshRenderingConventions.IsFrameSourceSamplerName(samplerName))
            {
                item.Add(VulkanMeshRenderingConventions.FrameSourceMutableDescriptorSignature);
            }
            else
            {
                descriptorSignatures.AddSignature(ref item, texture);
            }

            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashSamplerNameBindings(
        Dictionary<string, XRTexture> samplers,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures,
        bool includeMutableFrameSourceDescriptors)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((string name, XRTexture texture) in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(name);
            if (!includeMutableFrameSourceDescriptors &&
                VulkanMeshRenderingConventions.IsFrameSourceSamplerName(name))
            {
                item.Add(VulkanMeshRenderingConventions.FrameSourceMutableDescriptorSignature);
            }
            else
            {
                descriptorSignatures.AddSignature(ref item, texture);
            }

            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashImageBindings(
        Dictionary<uint, ProgramImageBinding> images,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((uint unit, ProgramImageBinding binding) in images)
        {
            FrameOpSignatureHasher item = new();
            item.Add(unit);
            descriptorSignatures.AddSignature(ref item, binding.Texture);
            item.Add(binding.Level);
            item.Add(binding.Layered);
            item.Add(binding.Layer);
            item.Add((int)binding.Access);
            item.Add((int)binding.Format);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(images.Count, xor, sum);
    }

    internal static ulong HashBufferBindings(Dictionary<uint, VulkanComputeBufferBinding> buffers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((uint unit, VulkanComputeBufferBinding binding) in buffers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(unit);
            item.Add(binding.Data.GetHashCode());
            item.Add(binding.Buffer.Handle);
            item.Add(binding.Range);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(buffers.Count, xor, sum);
    }

    internal static ulong HashSamplerUnitBindingLayout(
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((uint unit, XRTexture _) in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(unit);
            item.Add(samplerNamesByUnit.TryGetValue(unit, out string? name) ? name : string.Empty);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashSamplerNameBindingLayout(Dictionary<string, XRTexture> samplers)
        => HashBindingNames(samplers.Keys, samplers.Count);

    internal static ulong HashImageBindingLayout(Dictionary<uint, ProgramImageBinding> images)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach ((uint unit, ProgramImageBinding binding) in images)
        {
            FrameOpSignatureHasher item = new();
            item.Add(unit);
            item.Add(binding.Level);
            item.Add(binding.Layered);
            item.Add(binding.Layer);
            item.Add((int)binding.Access);
            item.Add((int)binding.Format);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(images.Count, xor, sum);
    }

    internal static ulong HashBufferBindingLayout(Dictionary<uint, VulkanComputeBufferBinding> buffers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (uint unit in buffers.Keys)
        {
            FrameOpSignatureHasher item = new();
            item.Add(unit);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(buffers.Count, xor, sum);
    }

    internal static void AddUnorderedItemHash(ref ulong xor, ref ulong sum, ulong itemHash)
    {
        unchecked
        {
            xor ^= itemHash;
            sum += BitOperations.RotateLeft(itemHash, (int)(itemHash & 31));
        }
    }

    internal static ulong FinishUnorderedHash(int count, ulong xor, ulong sum)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(count);
        hash.Add(xor);
        hash.Add(sum);
        return hash.ToHash();
    }

    private static int ResolveResourcePlanOutputTargetIdentity(in FrameOpContext context)
    {
        if (context.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return context.OutputTargetIdentity;

        return context.OutputFrameBufferIdentity != 0
            ? context.OutputFrameBufferIdentity
            : HashCode.Combine(
                (int)context.ContextKind,
                context.PipelineIdentity,
                context.ViewportIdentity);
    }

    private static int ComputePassMetadataSignature(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        int revisionStamp = ComputePassMetadataRevisionStamp(passMetadata);
        if (!VulkanFramePlanner.PassMetadataSignatureCache.TryGetValue(
                passMetadata,
                out RenderPassMetadataSignatureCacheEntry? cacheEntry))
        {
            if (VulkanFramePlanner.PassMetadataSignatureCache.Count >=
                MaxPassMetadataSignatureCacheEntries)
            {
                VulkanFramePlanner.PassMetadataSignatureCache.Clear();
            }

            cacheEntry = VulkanFramePlanner.PassMetadataSignatureCache.GetOrAdd(
                passMetadata,
                static _ => new RenderPassMetadataSignatureCacheEntry());
        }

        if (cacheEntry.RevisionStamp == revisionStamp)
            return cacheEntry.Signature;

        lock (cacheEntry)
        {
            if (cacheEntry.RevisionStamp == revisionStamp)
                return cacheEntry.Signature;

            int signature = ComputePassMetadataSignatureUncached(passMetadata);
            cacheEntry.Signature = signature;
            cacheEntry.RevisionStamp = revisionStamp;
            return signature;
        }
    }

    private static int ComputePassMetadataSignatureUncached(
        IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        HashCode hash = new();
        hash.Add(passMetadata.Count);
        foreach (RenderPassMetadata pass in passMetadata)
            AddPassMetadataToHash(ref hash, pass);
        return hash.ToHashCode();
    }

    private static int ComputePassMetadataRevisionStamp(
        IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        if (passMetadata is RenderPassMetadataSnapshot snapshot)
            return snapshot.RevisionStamp;

        HashCode hash = new();
        hash.Add(passMetadata.Count);
        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.DeclarationOrder);
            hash.Add(pass.Revision);
        }

        return hash.ToHashCode();
    }

    private static void AddPassMetadataToHash(ref HashCode hash, RenderPassMetadata pass)
    {
        hash.Add(pass.PassIndex);
        hash.Add(pass.DeclarationOrder);
        hash.Add((int)pass.Stage);
        hash.Add(pass.Name, StringComparer.Ordinal);
        hash.Add(pass.Revision);
        for (int index = 0; index < pass.ResourceUsages.Count; index++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[index];
            hash.Add(usage.ResourceName, StringComparer.Ordinal);
            hash.Add((int)usage.ResourceType);
            hash.Add((int)usage.Access);
            hash.Add((int)usage.LoadOp);
            hash.Add((int)usage.StoreOp);
            hash.Add(usage.SubresourceRange.BaseMipLevel);
            hash.Add(usage.SubresourceRange.MipLevelCount);
            hash.Add(usage.SubresourceRange.BaseArrayLayer);
            hash.Add(usage.SubresourceRange.ArrayLayerCount);
        }

        for (int index = 0; index < pass.ExplicitDependencies.Count; index++)
            hash.Add(pass.ExplicitDependencies[index]);
        for (int index = 0; index < pass.DescriptorSchemas.Count; index++)
            hash.Add(pass.DescriptorSchemas[index], StringComparer.Ordinal);
    }

    private static ulong HashBindingNames(
        IEnumerable<string> names,
        int count)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (string name in names)
        {
            FrameOpSignatureHasher item = new();
            item.Add(name);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(count, xor, sum);
    }

    private static void HashUniformValue(ref HashCode hash, ProgramUniformValue value)
    {
        if (value.ReferenceValue is { } referenceValue)
        {
            HashUniformValue(ref hash, referenceValue);
            return;
        }

        if (!value.HasInlineValue)
        {
            hash.Add(0);
            return;
        }

        switch (value.Type)
        {
            case EShaderVarType._float: hash.Add(value.Float); break;
            case EShaderVarType._int:
            case EShaderVarType._bool: hash.Add(value.Int); break;
            case EShaderVarType._uint: hash.Add(value.UInt); break;
            case EShaderVarType._double: hash.Add(value.Double); break;
            case EShaderVarType._vec2: hash.Add(value.Vector2); break;
            case EShaderVarType._vec3: hash.Add(value.Vector3); break;
            case EShaderVarType._vec4: hash.Add(value.Vector4); break;
            case EShaderVarType._mat4: hash.Add(value.Matrix4x4); break;
            case EShaderVarType._dvec2: hash.Add(new DVector2(value.DVector4.X, value.DVector4.Y)); break;
            case EShaderVarType._dvec3: hash.Add(new DVector3(value.DVector4.X, value.DVector4.Y, value.DVector4.Z)); break;
            case EShaderVarType._dvec4: hash.Add(value.DVector4); break;
            case EShaderVarType._ivec2: hash.Add(new IVector2(value.IVector4.X, value.IVector4.Y)); break;
            case EShaderVarType._ivec3: hash.Add(new IVector3(value.IVector4.X, value.IVector4.Y, value.IVector4.Z)); break;
            case EShaderVarType._ivec4: hash.Add(value.IVector4); break;
            case EShaderVarType._uvec2: hash.Add(new UVector2(value.UVector4.X, value.UVector4.Y)); break;
            case EShaderVarType._uvec3: hash.Add(new UVector3(value.UVector4.X, value.UVector4.Y, value.UVector4.Z)); break;
            case EShaderVarType._uvec4: hash.Add(value.UVector4); break;
            default: hash.Add(0); break;
        }
    }

    private static void HashUniformValue(ref HashCode hash, object? value)
    {
        if (value is null)
        {
            hash.Add(0);
            return;
        }

        if (value is Array array)
        {
            hash.Add(array.Length);
            HashUniformArray(ref hash, array);
            return;
        }

        hash.Add(value);
    }

    private static void HashUniformArray(ref HashCode hash, Array array)
    {
        switch (array)
        {
            case float[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case int[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case uint[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case bool[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case Vector2[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case Vector3[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case Vector4[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            case Matrix4x4[] values:
                for (int index = 0; index < values.Length; index++) hash.Add(values[index]);
                return;
            default:
                for (int index = 0; index < array.Length; index++)
                    HashUniformValue(ref hash, array.GetValue(index));
                return;
        }
    }
}
