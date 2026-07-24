using System.Diagnostics;
using System.Numerics;
using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public sealed class RenderPipelineResourceManager
{
    public bool Materialize(XRRenderPipelineInstance instance, RenderResourceGeneration generation)
        => MaterializeIncremental(instance, generation, TimeSpan.MaxValue, int.MaxValue, out bool completed) && completed;

    public bool MaterializeIncremental(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        TimeSpan maxDuration,
        int maxSpecsPerSlice,
        out bool completed)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(generation);
        completed = false;

        if (generation.Status == RenderResourceGenerationStatus.Created)
            generation.BeginBuild();
        else if (generation.Status != RenderResourceGenerationStatus.Building)
        {
            completed = generation.IsReady;
            return generation.IsReady;
        }

        try
        {
            long startTimestamp = Stopwatch.GetTimestamp();
            int materializedThisSlice = 0;
            IReadOnlyList<RenderPipelineResourceSpec> specs = generation.Layout.OrderedSpecs;

            using (instance.PushResourceBuildContext(generation))
            {
                while (generation.MaterializedSpecCount < specs.Count)
                {
                    MaterializeSpec(instance, generation, specs[generation.MaterializedSpecCount]);
                    generation.MaterializedSpecCount++;
                    materializedThisSlice++;

                    if (generation.MaterializedSpecCount < specs.Count
                        && ShouldYieldMaterialization(startTimestamp, maxDuration, materializedThisSlice, maxSpecsPerSlice))
                    {
                        return true;
                    }
                }

                ValidateRequiredResources(generation);
                ValidateDescriptorLayoutParity(generation);
            }

            generation.MarkReady();
            completed = true;
            return true;
        }
        catch (Exception ex) when (IsExpectedBackendImageAllocationDeferral(ex))
        {
            generation.AddDiagnostic(ex.Message);
            if (instance.ActiveGeneration is not null)
            {
                generation.MarkFailed(ex.Message);
                Debug.RenderingWarning(
                    "[RenderResources] Pending generation rolled back under Vulkan allocator pressure. Pipeline={0} Target={1} Progress={2}/{3} Reason={4}. Active generation remains {5}.",
                    instance.ProfilerKey,
                    generation.Key,
                    generation.MaterializedSpecCount,
                    generation.Layout.OrderedSpecs.Count,
                    ex.Message,
                    instance.ActiveGeneration.Key);
                return false;
            }

            Debug.RenderingEvery(
                $"RenderResources.PendingGenerationDeferred.{instance.ProfilerKey}",
                TimeSpan.FromSeconds(1),
                "[RenderResources] Pending generation deferred. Pipeline={0} Target={1} Progress={2}/{3} Reason={4}",
                instance.ProfilerKey,
                generation.Key,
                generation.MaterializedSpecCount,
                generation.Layout.OrderedSpecs.Count,
                ex.Message);
            return true;
        }
        catch (Exception ex)
        {
            generation.MarkFailed(ex.Message);
            Debug.RenderingWarning(
                "[RenderResources] Pending generation failed. Pipeline={0} Target={1} Reason={2}. Active generation remains {3}.",
                instance.ProfilerKey,
                generation.Key,
                ex.Message,
                instance.ActiveGeneration?.Key.ToString() ?? "<none>");
            return false;
        }
    }

    private static bool IsExpectedBackendImageAllocationDeferral(Exception exception)
        => AbstractRenderer.Current is IRuntimeRendererHost renderer
           && renderer.TryGetBackendCapability<IVulkanAllocatorStreamingBackendCapability>(out var allocator)
           && allocator?.IsExpectedImageAllocationDeferral(exception) == true;

    private static bool ShouldYieldMaterialization(
        long startTimestamp,
        TimeSpan maxDuration,
        int materializedThisSlice,
        int maxSpecsPerSlice)
    {
        if (materializedThisSlice <= 0)
            return false;

        if (maxSpecsPerSlice > 0 && materializedThisSlice >= maxSpecsPerSlice)
            return true;

        if (maxDuration == TimeSpan.MaxValue)
            return false;

        return Stopwatch.GetElapsedTime(startTimestamp) >= maxDuration;
    }

    private static void MaterializeSpec(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        RenderPipelineResourceSpec spec)
    {
        switch (spec)
        {
            case TextureSpec textureSpec:
                MaterializeTexture(instance, generation, textureSpec);
                break;
            case TextureViewSpec viewSpec:
                MaterializeTextureView(instance, generation, viewSpec);
                break;
            case RenderBufferSpec renderBufferSpec:
                MaterializeRenderBuffer(instance, generation, renderBufferSpec);
                break;
            case BufferSpec bufferSpec:
                MaterializeBuffer(instance, generation, bufferSpec);
                break;
            case FrameBufferSpec frameBufferSpec:
                MaterializeFrameBuffer(instance, generation, frameBufferSpec);
                break;
            case QuadMaterialSpec quadMaterialSpec:
                MaterializeQuadMaterial(instance, generation, quadMaterialSpec);
                break;
            case ExternalResourceSpec:
                break;
        }
    }

    private static void MaterializeTexture(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        TextureSpec spec)
    {
        TextureResourceDescriptor descriptor = spec.ToDescriptor();
        generation.Registry.RegisterTextureDescriptor(descriptor);

        if (generation.Registry.TryGetTexture(spec.Name, out _))
            return;

        if (spec.Factory is null)
        {
            if (spec.Required && !CanCommitWithoutInstance(generation, spec))
                throw new InvalidOperationException($"Texture '{spec.Name}' has no factory and no concrete instance.");
            return;
        }

        XRTexture texture = spec.Factory();
        texture.Name = spec.Name;
        if (string.IsNullOrWhiteSpace(texture.SamplerName))
            texture.SamplerName = spec.Name;
        ValidateTextureInstance(generation, spec, texture);
        instance.SetTexture(texture, descriptor);
    }

    private static void MaterializeTextureView(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        TextureViewSpec spec)
    {
        TextureResourceDescriptor descriptor = spec.ToDescriptor();
        generation.Registry.RegisterTextureDescriptor(descriptor);

        if (!generation.Registry.TryGetTexture(spec.SourceTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
            throw new InvalidOperationException($"Texture view '{spec.Name}' is missing source texture '{spec.SourceTextureName}'.");

        if (generation.Registry.TryGetTexture(spec.Name, out _))
            return;

        if (spec.Factory is null)
        {
            if (spec.Required && !CanCommitWithoutInstance(generation, spec))
                throw new InvalidOperationException($"Texture view '{spec.Name}' has no factory and no concrete instance.");
            return;
        }

        XRTexture view = spec.Factory();
        view.Name = spec.Name;
        if (string.IsNullOrWhiteSpace(view.SamplerName))
            view.SamplerName = spec.Name;

        XRTexture viewed = view switch
        {
            XRTexture2DView textureView => textureView.ViewedTexture,
            XRTexture2DArrayView textureArrayView => textureArrayView.ViewedTexture,
            XRTextureViewBase textureViewBase => textureViewBase.GetViewedTexture(),
            _ => throw new InvalidOperationException($"Texture view '{spec.Name}' factory produced '{view.GetType().Name}', which is not a texture view.")
        };

        if (!ReferenceEquals(viewed, sourceTexture))
            throw new InvalidOperationException($"Texture view '{spec.Name}' references a source texture outside its generation.");

        ValidateTextureViewInstance(spec, view, sourceTexture);
        instance.SetTexture(view, descriptor);
    }

    private static void MaterializeRenderBuffer(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        RenderBufferSpec spec)
    {
        RenderBufferResourceDescriptor descriptor = spec.ToDescriptor();
        generation.Registry.RegisterRenderBufferDescriptor(descriptor);

        if (generation.Registry.TryGetRenderBuffer(spec.Name, out _))
            return;

        if (spec.Factory is null)
        {
            if (spec.Required && !CanCommitWithoutInstance(generation, spec))
                throw new InvalidOperationException($"Renderbuffer '{spec.Name}' has no factory and no concrete instance.");
            return;
        }

        XRRenderBuffer renderBuffer = spec.Factory();
        renderBuffer.Name = spec.Name;
        instance.SetRenderBuffer(renderBuffer, descriptor);
    }

    private static void MaterializeBuffer(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        BufferSpec spec)
    {
        BufferResourceDescriptor descriptor = spec.ToDescriptor();
        generation.Registry.RegisterBufferDescriptor(descriptor);

        if (generation.Registry.TryGetBuffer(spec.Name, out _))
            return;

        if (spec.Factory is null)
        {
            if (spec.Required && !CanCommitWithoutInstance(generation, spec))
                throw new InvalidOperationException($"Buffer '{spec.Name}' has no factory and no concrete instance.");
            return;
        }

        XRDataBuffer buffer = spec.Factory();
        buffer.AttributeName = spec.Name;
        instance.SetBuffer(buffer, descriptor);
    }

    private static void MaterializeFrameBuffer(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        FrameBufferSpec spec)
    {
        FrameBufferResourceDescriptor descriptor = spec.ToDescriptor();
        generation.Registry.RegisterFrameBufferDescriptor(descriptor);

        if (generation.Registry.TryGetFrameBuffer(spec.Name, out _))
            return;

        ValidateFrameBufferAttachmentDependencies(generation, spec);

        if (spec.Factory is null)
        {
            if (spec.Required && !CanCommitWithoutInstance(generation, spec))
                throw new InvalidOperationException($"Framebuffer '{spec.Name}' has no factory and no concrete instance.");
            return;
        }

        XRFrameBuffer frameBuffer = spec.Factory();
        frameBuffer.Name = spec.Name;
        ValidateFrameBufferInstance(generation, spec, frameBuffer);
        instance.SetFBO(frameBuffer, descriptor);
    }

    private static void MaterializeQuadMaterial(
        XRRenderPipelineInstance instance,
        RenderResourceGeneration generation,
        QuadMaterialSpec spec)
    {
        FrameBufferResourceDescriptor descriptor = new(
            spec.Name,
            spec.Lifetime,
            RenderResourceSizePolicy.Absolute(0u, 0u),
            Array.Empty<FrameBufferAttachmentDescriptor>());
        generation.Registry.RegisterFrameBufferDescriptor(descriptor);

        if (generation.Registry.TryGetFrameBuffer(spec.Name, out _))
            return;

        if (spec.Factory is null)
        {
            if (spec.Required)
                throw new InvalidOperationException($"Quad material '{spec.Name}' has no factory.");
            return;
        }

        XRFrameBuffer frameBuffer = spec.Factory();
        if (frameBuffer is not XRQuadFrameBuffer)
            throw new InvalidOperationException($"Quad material '{spec.Name}' factory must produce an XRQuadFrameBuffer.");

        frameBuffer.Name = spec.Name;
        instance.SetFBO(frameBuffer, descriptor);
    }

    private static void ValidateRequiredResources(RenderResourceGeneration generation)
    {
        foreach (RenderPipelineResourceSpec spec in generation.Layout.OrderedSpecs)
        {
            if (!spec.Required)
                continue;

            bool exists = spec.Kind switch
            {
                RenderPipelineResourceKind.Texture or RenderPipelineResourceKind.TextureView
                    => generation.Registry.TryGetTexture(spec.Name, out _),
                RenderPipelineResourceKind.FrameBuffer
                    => generation.Registry.TryGetFrameBuffer(spec.Name, out _),
                RenderPipelineResourceKind.RenderBuffer
                    => generation.Registry.TryGetRenderBuffer(spec.Name, out _),
                RenderPipelineResourceKind.Buffer
                    => generation.Registry.TryGetBuffer(spec.Name, out _),
                RenderPipelineResourceKind.QuadMaterial
                    => generation.Registry.TryGetFrameBuffer(spec.Name, out _),
                _ => true
            };

            if (!exists)
            {
                if (CanCommitWithoutInstance(generation, spec))
                    continue;

                throw new InvalidOperationException($"Required {spec.Kind} resource '{spec.Name}' was not materialized.");
            }
        }
    }

    internal static void ValidateDescriptorLayoutParity(RenderResourceGeneration generation)
    {
        RenderResourceRegistry registry = generation.Registry;
        foreach (RenderPipelineResourceSpec spec in generation.Layout.OrderedSpecs)
        {
            switch (spec)
            {
                case TextureSpec textureSpec:
                    RequireMatchingDescriptor(
                        spec,
                        textureSpec.ToDescriptor(),
                        registry.TextureRecords.GetValueOrDefault(spec.Name)?.Descriptor);
                    break;
                case TextureViewSpec viewSpec:
                    RequireMatchingDescriptor(
                        spec,
                        viewSpec.ToDescriptor(),
                        registry.TextureRecords.GetValueOrDefault(spec.Name)?.Descriptor);
                    break;
                case BufferSpec bufferSpec:
                    RequireMatchingDescriptor(
                        spec,
                        bufferSpec.ToDescriptor(),
                        registry.BufferRecords.GetValueOrDefault(spec.Name)?.Descriptor);
                    break;
                case RenderBufferSpec renderBufferSpec:
                    RequireMatchingDescriptor(
                        spec,
                        renderBufferSpec.ToDescriptor(),
                        registry.RenderBufferRecords.GetValueOrDefault(spec.Name)?.Descriptor);
                    break;
                case FrameBufferSpec frameBufferSpec:
                {
                    FrameBufferResourceDescriptor expected = frameBufferSpec.ToDescriptor();
                    FrameBufferResourceDescriptor? actual = registry.FrameBufferRecords.GetValueOrDefault(spec.Name)?.Descriptor;
                    if (actual is null ||
                        expected.Name != actual.Name ||
                        expected.Lifetime != actual.Lifetime ||
                        expected.SizePolicy != actual.SizePolicy ||
                        !expected.Attachments.SequenceEqual(actual.Attachments))
                    {
                        ThrowDescriptorLayoutMismatch(spec, expected, actual);
                    }
                    break;
                }
                case QuadMaterialSpec:
                {
                    FrameBufferResourceDescriptor expected = new(
                        spec.Name,
                        spec.Lifetime,
                        RenderResourceSizePolicy.Absolute(0u, 0u),
                        Array.Empty<FrameBufferAttachmentDescriptor>());
                    FrameBufferResourceDescriptor? actual = registry.FrameBufferRecords.GetValueOrDefault(spec.Name)?.Descriptor;
                    if (actual is null ||
                        expected.Name != actual.Name ||
                        expected.Lifetime != actual.Lifetime ||
                        expected.SizePolicy != actual.SizePolicy ||
                        actual.Attachments.Count != 0)
                    {
                        ThrowDescriptorLayoutMismatch(spec, expected, actual);
                    }
                    break;
                }
            }
        }
    }

    private static void RequireMatchingDescriptor<TDescriptor>(
        RenderPipelineResourceSpec spec,
        TDescriptor expected,
        TDescriptor? actual)
        where TDescriptor : class
    {
        if (actual is null || !expected.Equals(actual))
            ThrowDescriptorLayoutMismatch(spec, expected, actual);
    }

    private static void ThrowDescriptorLayoutMismatch(
        RenderPipelineResourceSpec spec,
        object expected,
        object? actual)
        => throw new InvalidOperationException(
            $"Pending descriptor/layout mismatch for {spec.Kind} '{spec.Name}'. Expected={expected} Actual={actual?.ToString() ?? "<missing>"}.");

    private static bool CanCommitWithoutInstance(RenderResourceGeneration generation, RenderPipelineResourceSpec spec)
    {
        if (spec.HistoryPolicy != RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            return false;

        generation.AddDiagnostic(
            $"History resource '{spec.Name}' committed without an initial instance; it will be seeded from the current frame.");
        return true;
    }

    private static void ValidateFrameBufferAttachmentDependencies(RenderResourceGeneration generation, FrameBufferSpec spec)
    {
        foreach (FrameBufferAttachmentDescriptor attachment in spec.Attachments)
        {
            if (generation.Registry.TryGetTexture(attachment.ResourceName, out XRTexture? texture)
                && texture is IFrameBufferAttachement)
            {
                continue;
            }

            if (generation.Registry.TryGetRenderBuffer(attachment.ResourceName, out XRRenderBuffer? renderBuffer)
                && renderBuffer is not null)
            {
                continue;
            }

            throw new InvalidOperationException($"Framebuffer '{spec.Name}' is missing attachable resource '{attachment.ResourceName}'.");
        }
    }

    private static void ValidateFrameBufferInstance(
        RenderResourceGeneration generation,
        FrameBufferSpec spec,
        XRFrameBuffer frameBuffer)
    {
        if (frameBuffer.Targets is not { Length: > 0 } targets)
            throw new InvalidOperationException($"Framebuffer '{spec.Name}' factory produced no attachments.");

        foreach (FrameBufferAttachmentDescriptor descriptorAttachment in spec.Attachments)
        {
            bool found = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, mipLevel, layerIndex) = targets[i];
                if (attachment != descriptorAttachment.Attachment
                    || mipLevel != descriptorAttachment.MipLevel
                    || layerIndex != descriptorAttachment.LayerIndex)
                {
                    continue;
                }

                string? targetName = target switch
                {
                    XRTexture texture => texture.Name,
                    XRRenderBuffer renderBuffer => renderBuffer.Name,
                    _ => null
                };

                if (!string.Equals(targetName, descriptorAttachment.ResourceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                ValidateAttachmentIdentity(generation, spec.Name, descriptorAttachment.ResourceName, target);
                ValidateAttachmentFormat(generation, spec.Name, descriptorAttachment, target);
                ValidateAttachmentSubresourceContract(generation, spec.Name, descriptorAttachment, target);
                found = true;
                break;
            }

            if (!found)
                throw new InvalidOperationException(
                    $"Framebuffer '{spec.Name}' is missing attachment '{descriptorAttachment.Attachment}' -> '{descriptorAttachment.ResourceName}'. Attachments={DescribeFrameBufferAttachments(targets)}");
        }

        ValidateFrameBufferAttachmentDimensions(spec.Name, targets);
        ValidateFrameBufferBackendCompleteness(spec.Name, frameBuffer);
    }

    private static void ValidateAttachmentIdentity(
        RenderResourceGeneration generation,
        string frameBufferName,
        string resourceName,
        IFrameBufferAttachement attachment)
    {
        if (attachment is XRTexture texture)
        {
            if (!generation.Registry.TryGetTexture(resourceName, out XRTexture? registered)
                || !ReferenceEquals(registered, texture))
            {
                throw new InvalidOperationException($"Framebuffer '{frameBufferName}' attachment '{resourceName}' references a texture outside its generation.");
            }

            return;
        }

        if (attachment is XRRenderBuffer renderBuffer)
        {
            if (!generation.Registry.TryGetRenderBuffer(resourceName, out XRRenderBuffer? registered)
                || !ReferenceEquals(registered, renderBuffer))
            {
                throw new InvalidOperationException($"Framebuffer '{frameBufferName}' attachment '{resourceName}' references a renderbuffer outside its generation.");
            }
        }
    }

    private static void ValidateFrameBufferAttachmentDimensions(
        string frameBufferName,
        (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[] targets)
    {
        uint width = 0;
        uint height = 0;
        uint samples = 0;

        for (int i = 0; i < targets.Length; i++)
        {
            IFrameBufferAttachement target = targets[i].Target;
            if (target.Width == 0 || target.Height == 0)
                throw new InvalidOperationException($"Framebuffer '{frameBufferName}' attachment '{i}' has zero dimensions.");

            uint targetSamples = ResolveSampleCount(target);
            if (width == 0)
            {
                width = target.Width;
                height = target.Height;
                samples = targetSamples;
                continue;
            }

            if (target.Width != width || target.Height != height)
                throw new InvalidOperationException(
                    $"Framebuffer '{frameBufferName}' has mismatched attachment sizes. Attachments={DescribeFrameBufferAttachments(targets)}");

            if (targetSamples != samples)
                throw new InvalidOperationException(
                    $"Framebuffer '{frameBufferName}' has mismatched attachment sample counts. Attachments={DescribeFrameBufferAttachments(targets)}");
        }
    }

    private static void ValidateAttachmentFormat(
        RenderResourceGeneration generation,
        string frameBufferName,
        FrameBufferAttachmentDescriptor descriptorAttachment,
        IFrameBufferAttachement target)
    {
        if (!generation.Layout.TryGet(descriptorAttachment.ResourceName, out RenderPipelineResourceSpec? resourceSpec))
            return;

        switch (resourceSpec)
        {
            case TextureSpec textureSpec:
                ESizedInternalFormat? actualTextureFormat = ResolveSizedInternalFormat(target);
                if (textureSpec.SizedInternalFormat is ESizedInternalFormat expectedTextureFormat
                    && actualTextureFormat is ESizedInternalFormat actual
                    && expectedTextureFormat != actual)
                {
                    throw new InvalidOperationException(
                        $"Framebuffer '{frameBufferName}' attachment '{descriptorAttachment.ResourceName}' format mismatch. Expected={expectedTextureFormat} Actual={actual}.");
                }
                break;

            case TextureViewSpec viewSpec:
                ESizedInternalFormat? actualViewFormat = ResolveSizedInternalFormat(target);
                if (viewSpec.SizedInternalFormat is ESizedInternalFormat expectedViewFormat
                    && actualViewFormat is ESizedInternalFormat actualView
                    && expectedViewFormat != actualView)
                {
                    throw new InvalidOperationException(
                        $"Framebuffer '{frameBufferName}' attachment view '{descriptorAttachment.ResourceName}' format mismatch. Expected={expectedViewFormat} Actual={actualView}.");
                }
                break;

            case RenderBufferSpec renderBufferSpec:
                if (target is XRRenderBuffer renderBuffer && renderBuffer.Type != renderBufferSpec.StorageFormat)
                {
                    throw new InvalidOperationException(
                        $"Framebuffer '{frameBufferName}' renderbuffer attachment '{descriptorAttachment.ResourceName}' storage mismatch. Expected={renderBufferSpec.StorageFormat} Actual={renderBuffer.Type}.");
                }
                break;
        }
    }

    private static void ValidateTextureInstance(
        RenderResourceGeneration generation,
        TextureSpec spec,
        XRTexture texture)
    {
        if (texture is XRTextureViewBase)
        {
            throw new InvalidOperationException(
                $"Texture '{spec.Name}' factory produced a texture view. Declare views with TextureViewSpec so their source and subresource range are explicit.");
        }

        (uint expectedWidth, uint expectedHeight) = ResolveExpectedExtent(spec.SizePolicy, generation.Key);
        Vector3 actualDimensions = texture.WidthHeightDepth;
        uint actualWidth = (uint)Math.Max(1, (int)MathF.Round(actualDimensions.X));
        uint actualHeight = (uint)Math.Max(1, (int)MathF.Round(actualDimensions.Y));
        if (actualWidth != expectedWidth || actualHeight != expectedHeight)
        {
            throw new InvalidOperationException(
                $"Texture '{spec.Name}' extent mismatch. Expected={expectedWidth}x{expectedHeight} Actual={actualWidth}x{actualHeight}.");
        }

        uint expectedLayers = Math.Max(spec.Layers, 1u);
        uint actualLayers = ResolveInstanceLayerCount(texture);
        if (actualLayers != expectedLayers)
        {
            throw new InvalidOperationException(
                $"Texture '{spec.Name}' layer mismatch. Expected={expectedLayers} Actual={actualLayers} Type={texture.GetType().Name}.");
        }

        bool actualStereoCompatible = IsStereoCompatibleInstance(texture);
        if (actualStereoCompatible != spec.StereoCompatible)
        {
            throw new InvalidOperationException(
                $"Texture '{spec.Name}' stereo shape mismatch. ExpectedStereo={spec.StereoCompatible} ActualStereo={actualStereoCompatible} Layers={actualLayers} Type={texture.GetType().Name}.");
        }

        uint expectedMipLevels = Math.Max(spec.MipPolicy.MipLevelCount, 1u);
        uint actualMipLevels = ResolveMipLevelCount(texture);
        if (actualMipLevels < expectedMipLevels)
        {
            throw new InvalidOperationException(
                $"Texture '{spec.Name}' mip range mismatch. ExpectedAtLeast={expectedMipLevels} Actual={actualMipLevels}.");
        }

        uint expectedSamples = Math.Max(spec.Samples, 1u);
        uint actualSamples = texture is IFrameBufferAttachement attachment
            ? ResolveSampleCount(attachment)
            : 1u;
        if (actualSamples != expectedSamples)
        {
            throw new InvalidOperationException(
                $"Texture '{spec.Name}' sample-count mismatch. Expected={expectedSamples} Actual={actualSamples}.");
        }

        if (spec.SizedInternalFormat is ESizedInternalFormat expectedFormat)
        {
            ESizedInternalFormat? actualFormat = texture is IFrameBufferAttachement formatAttachment
                ? ResolveSizedInternalFormat(formatAttachment)
                : null;
            if (actualFormat is ESizedInternalFormat resolvedFormat && resolvedFormat != expectedFormat)
            {
                throw new InvalidOperationException(
                    $"Texture '{spec.Name}' format mismatch. Expected={expectedFormat} Actual={resolvedFormat}.");
            }
        }
    }

    private static void ValidateAttachmentSubresourceContract(
        RenderResourceGeneration generation,
        string frameBufferName,
        FrameBufferAttachmentDescriptor descriptorAttachment,
        IFrameBufferAttachement target)
    {
        if (target is not XRTexture texture ||
            !generation.Layout.TryGet(descriptorAttachment.ResourceName, out RenderPipelineResourceSpec? resourceSpec))
        {
            return;
        }

        uint availableMipLevels = texture is XRTextureViewBase view
            ? Math.Max(view.NumLevels, 1u)
            : ResolveMipLevelCount(texture);
        if (descriptorAttachment.MipLevel < 0 || (uint)descriptorAttachment.MipLevel >= availableMipLevels)
        {
            throw new InvalidOperationException(
                $"Framebuffer '{frameBufferName}' attachment '{descriptorAttachment.ResourceName}' mip {descriptorAttachment.MipLevel} exceeds the {availableMipLevels}-mip view.");
        }

        uint expectedLayers;
        bool stereoCompatible;
        switch (resourceSpec)
        {
            case TextureSpec textureSpec:
                expectedLayers = Math.Max(textureSpec.Layers, 1u);
                stereoCompatible = textureSpec.StereoCompatible;
                break;
            case TextureViewSpec textureViewSpec:
                expectedLayers = Math.Max(textureViewSpec.LayerCount, 1u);
                stereoCompatible = textureViewSpec.LayerCount > 1u;
                break;
            default:
                return;
        }

        uint availableLayers = ResolveInstanceLayerCount(texture);
        if (descriptorAttachment.LayerIndex >= 0 && (uint)descriptorAttachment.LayerIndex >= availableLayers)
        {
            throw new InvalidOperationException(
                $"Framebuffer '{frameBufferName}' attachment '{descriptorAttachment.ResourceName}' layer {descriptorAttachment.LayerIndex} exceeds the {availableLayers}-layer view.");
        }

        if (!stereoCompatible)
            return;

        if (descriptorAttachment.LayerIndex != -1)
        {
            throw new InvalidOperationException(
                $"Framebuffer '{frameBufferName}' stereo attachment '{descriptorAttachment.ResourceName}' selects layer {descriptorAttachment.LayerIndex}; true multiview attachments must bind the complete array with layerIndex=-1.");
        }

        if (availableLayers < expectedLayers || !IsStereoCompatibleInstance(texture))
        {
            throw new InvalidOperationException(
                $"Framebuffer '{frameBufferName}' stereo attachment '{descriptorAttachment.ResourceName}' does not expose the declared multiview range. ExpectedLayers={expectedLayers} ActualLayers={availableLayers} Type={texture.GetType().Name}.");
        }
    }

    private static (uint Width, uint Height) ResolveExpectedExtent(
        RenderResourceSizePolicy sizePolicy,
        ResourceGenerationKey key)
    {
        uint windowWidth = Math.Max(key.DisplayWidth, 1u);
        uint windowHeight = Math.Max(key.DisplayHeight, 1u);
        uint internalWidth = Math.Max(key.InternalWidth, 1u);
        uint internalHeight = Math.Max(key.InternalHeight, 1u);

        return sizePolicy.SizeClass switch
        {
            RenderResourceSizeClass.AbsolutePixels =>
                (Math.Max(sizePolicy.Width, 1u), Math.Max(sizePolicy.Height, 1u)),
            RenderResourceSizeClass.InternalResolution =>
                (ScaleExtent(internalWidth, sizePolicy.ScaleX), ScaleExtent(internalHeight, sizePolicy.ScaleY)),
            RenderResourceSizeClass.WindowResolution or RenderResourceSizeClass.Custom =>
                (ScaleExtent(windowWidth, sizePolicy.ScaleX), ScaleExtent(windowHeight, sizePolicy.ScaleY)),
            _ => (windowWidth, windowHeight),
        };
    }

    private static uint ScaleExtent(uint extent, float scale)
        => (uint)Math.Max(1, (int)MathF.Round(Math.Max(extent, 1u) * scale));

    private static uint ResolveInstanceLayerCount(XRTexture texture)
        => texture switch
        {
            XRTexture2DArray textureArray => Math.Max(textureArray.Depth, 1u),
            XRTextureViewBase viewBase => Math.Max(viewBase.NumLayers, 1u),
            _ => 1u,
        };

    private static bool IsStereoCompatibleInstance(XRTexture texture)
    {
        XRTexture viewedTexture = texture is XRTextureViewBase view
            ? view.GetViewedTexture()
            : texture;
        return viewedTexture is XRTexture2DArray textureArray
            && ResolveInstanceLayerCount(texture) > 1u
            && textureArray.OVRMultiViewParameters is { NumViews: > 1u };
    }

    private static void ValidateFrameBufferBackendCompleteness(string frameBufferName, XRFrameBuffer frameBuffer)
    {
        if (frameBuffer.IsLastCheckComplete)
            return;

        string summary = frameBuffer.Targets is { Length: > 0 } targets
            ? DescribeFrameBufferAttachments(targets)
            : "<none>";
        throw new InvalidOperationException(
            $"Framebuffer '{frameBufferName}' failed backend completeness validation. Attachments={summary}");
    }

    private static void ValidateTextureViewInstance(
        TextureViewSpec spec,
        XRTexture view,
        XRTexture sourceTexture)
    {
        if (view is not XRTextureViewBase viewBase)
            throw new InvalidOperationException($"Texture view '{spec.Name}' factory produced '{view.GetType().Name}', which is not a texture view.");

        if (viewBase.MinLevel != spec.BaseMipLevel || viewBase.NumLevels != spec.MipLevelCount)
        {
            throw new InvalidOperationException(
                $"Texture view '{spec.Name}' mip range mismatch. Expected={spec.BaseMipLevel}+{spec.MipLevelCount} Actual={viewBase.MinLevel}+{viewBase.NumLevels}.");
        }

        if (viewBase.MinLayer != spec.BaseLayer || viewBase.NumLayers != spec.LayerCount)
        {
            throw new InvalidOperationException(
                $"Texture view '{spec.Name}' layer range mismatch. Expected={spec.BaseLayer}+{spec.LayerCount} Actual={viewBase.MinLayer}+{viewBase.NumLayers}.");
        }

        if (spec.SizedInternalFormat is ESizedInternalFormat expectedFormat && viewBase.InternalFormat != expectedFormat)
        {
            throw new InvalidOperationException(
                $"Texture view '{spec.Name}' format mismatch. Expected={expectedFormat} Actual={viewBase.InternalFormat}.");
        }

        if (spec.BaseMipLevel + spec.MipLevelCount > ResolveMipLevelCount(sourceTexture))
        {
            throw new InvalidOperationException(
                $"Texture view '{spec.Name}' mip range exceeds source texture '{spec.SourceTextureName}'.");
        }

        if (spec.BaseLayer + spec.LayerCount > ResolveLayerCount(sourceTexture))
        {
            throw new InvalidOperationException(
                $"Texture view '{spec.Name}' layer range exceeds source texture '{spec.SourceTextureName}'.");
        }

        switch (view)
        {
            case XRTexture2DView view2D:
                if (view2D.Array != spec.ArrayTarget || view2D.Multisample != spec.Multisample)
                {
                    throw new InvalidOperationException(
                        $"Texture view '{spec.Name}' target mismatch. Expected array={spec.ArrayTarget} multisample={spec.Multisample} Actual array={view2D.Array} multisample={view2D.Multisample}.");
                }

                if (view2D.DepthStencilViewFormat != spec.DepthStencilAspect)
                {
                    throw new InvalidOperationException(
                        $"Texture view '{spec.Name}' depth/stencil aspect mismatch. Expected={spec.DepthStencilAspect} Actual={view2D.DepthStencilViewFormat}.");
                }
                break;

            case XRTexture2DArrayView viewArray:
                if (viewArray.Array != spec.ArrayTarget || viewArray.Multisample != spec.Multisample)
                {
                    throw new InvalidOperationException(
                        $"Texture view '{spec.Name}' target mismatch. Expected array={spec.ArrayTarget} multisample={spec.Multisample} Actual array={viewArray.Array} multisample={viewArray.Multisample}.");
                }

                if (viewArray.DepthStencilViewFormat != spec.DepthStencilAspect)
                {
                    throw new InvalidOperationException(
                        $"Texture view '{spec.Name}' depth/stencil aspect mismatch. Expected={spec.DepthStencilAspect} Actual={viewArray.DepthStencilViewFormat}.");
                }
                break;
        }
    }

    private static uint ResolveMipLevelCount(XRTexture texture)
        => texture switch
        {
            XRTextureViewBase viewBase => Math.Max(viewBase.NumLevels, 1u),
            XRTexture2D texture2D when texture2D.SmallestAllowedMipmapLevel < 1000 =>
                (uint)Math.Max(1, texture2D.SmallestMipmapLevel + 1),
            XRTexture2DArray textureArray when textureArray.SmallestAllowedMipmapLevel < 1000 =>
                (uint)Math.Max(1, textureArray.SmallestMipmapLevel + 1),
            XRTexture2D texture2D => (uint)Math.Max(1, texture2D.Mipmaps.Length),
            XRTexture2DArray textureArray => (uint)Math.Max(1, textureArray.Mipmaps?.Length ?? 1),
            _ => 1u,
        };

    private static uint ResolveLayerCount(XRTexture texture)
        => texture switch
        {
            XRTexture2DArray textureArray => Math.Max(1u, textureArray.Depth),
            XRTextureViewBase viewBase => Math.Max(viewBase.NumLayers, 1u),
            _ => 1u,
        };

    private static ESizedInternalFormat? ResolveSizedInternalFormat(IFrameBufferAttachement attachment)
        => attachment switch
        {
            XRTexture2D texture2D => texture2D.SizedInternalFormat,
            XRTexture2DArray textureArray => textureArray.SizedInternalFormat,
            XRTextureViewBase textureView => textureView.InternalFormat,
            _ => null,
        };

    private static string DescribeFrameBufferAttachments(
        (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[] targets)
    {
        StringBuilder builder = new();
        for (int i = 0; i < targets.Length; i++)
        {
            if (i != 0)
                builder.Append("; ");

            var (target, attachment, mipLevel, layerIndex) = targets[i];
            string targetName = target switch
            {
                XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                XRRenderBuffer renderBuffer => renderBuffer.Name ?? renderBuffer.GetDescribingName(),
                _ => target.GetType().Name,
            };

            builder
                .Append(attachment)
                .Append(" -> ")
                .Append(targetName)
                .Append(' ')
                .Append(target.Width)
                .Append('x')
                .Append(target.Height)
                .Append(" samples=")
                .Append(ResolveSampleCount(target));

            if (mipLevel != 0)
                builder.Append(" mip=").Append(mipLevel);
            if (layerIndex >= 0)
                builder.Append(" layer=").Append(layerIndex);
        }

        return builder.ToString();
    }

    private static uint ResolveSampleCount(IFrameBufferAttachement attachment)
        => attachment switch
        {
            XRRenderBuffer renderBuffer => Math.Max(1u, renderBuffer.MultisampleCount),
            XRTexture2D texture2D => Math.Max(1u, texture2D.MultiSampleCount),
            XRTexture2DView textureView when textureView.Multisample => Math.Max(2u, textureView.ViewedTexture.MultiSampleCount),
            XRTexture2DArray textureArray when textureArray.MultiSample && textureArray.Textures.Length > 0 => Math.Max(2u, textureArray.Textures[0].MultiSampleCount),
            XRTexture2DArrayView textureArrayView when textureArrayView.Multisample && textureArrayView.ViewedTexture.Textures.Length > 0
                => Math.Max(2u, textureArrayView.ViewedTexture.Textures[0].MultiSampleCount),
            _ => 1u,
        };
}
