using System.Reflection;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Makes an editable <see cref="CustomRenderPipeline"/> from any pipeline. The fork copies the command tree, the scene
/// passes, the resource layout of one profile, and the schema. It never changes the source pipeline.
/// </summary>
/// <remarks>
/// A fork cannot keep code. Each code delegate on a command goes back to the default of the command type, and each
/// code branch loses its condition. The validator reports a branch without a condition as an error, so a fork cannot be
/// applied until the user sets each condition. Resources that only a factory can make are listed as unconverted, and
/// the commands that read them report undeclared reads.
/// </remarks>
public static class RenderPipelineFork
{
    /// <summary>Creates a fork of <paramref name="source"/> for <paramref name="profile"/>.</summary>
    public static RenderPipelineForkResult Create(RenderPipeline source, RenderPipelineResourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<string> unconverted = [];

        ViewportRenderCommandContainer commands = source.CommandChain is { } chain
            ? RenderCommandGraphCloner.CloneContainer(chain)
            : new ViewportRenderCommandContainer();
        RenderPipelineDraftEdits.ForEachCommand(commands, command => StripCode(command, unconverted));

        List<AuthoredRenderResource> resources = [];
        try
        {
            ConvertLayout(source.DescribeResourceLayout(profile), resources, unconverted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            unconverted.Add(string.Concat("Resource layout: ", ex.Message));
        }

        List<AuthoredPostProcessStage> stages = ConvertSchema(source.PostProcessSchema ?? RenderPipelinePostProcessSchema.Empty);
        CustomRenderPipeline fork = new()
        {
            AuthoredResources = resources,
            AuthoredStages = stages,
            RenderPasses = new Dictionary<int, IComparer<RenderCommand>?>(source.PassIndicesAndSorters ?? []),
        };
        fork.Commands = commands;
        return new RenderPipelineForkResult(fork, unconverted);
    }

    /// <summary>
    /// Converts the specs of <paramref name="layout"/> that are enabled for its profile into declarations. A spec that
    /// only a factory can make adds a line to <paramref name="unconverted"/>.
    /// </summary>
    public static void ConvertLayout(RenderPipelineResourceLayout layout, List<AuthoredRenderResource> output, List<string> unconverted)
    {
        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
        {
            RenderPipelineResourceSpec spec = layout.OrderedSpecs[i];
            if (!spec.IsEnabled(layout.Profile))
                continue;

            AuthoredRenderResource? resource = ConvertSpec(spec);
            if (resource is null)
            {
                unconverted.Add(string.Concat("Resource '", spec.Name, "' (", spec.Kind.ToString(), "): only code can create it."));
                continue;
            }

            resource.Name = spec.Name;
            resource.Lifetime = spec.Lifetime;
            resource.History = spec.HistoryPolicy;
            resource.Optional = !spec.Required;
            output.Add(resource);
        }
    }

    private static AuthoredRenderResource? ConvertSpec(RenderPipelineResourceSpec spec)
        => spec switch
        {
            TextureSpec { SizedInternalFormat: { } format } texture => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.Texture,
                Size = AuthoredSizePolicy.FromPolicy(texture.SizePolicy),
                Format = format,
                Samples = Math.Max(1u, texture.Samples),
                Layers = texture.Layers,
                MipLevels = Math.Max(1u, texture.MipPolicy.MipLevelCount),
                StorageImage = texture.RequiresStorageUsage,
            },
            TextureViewSpec view => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.TextureView,
                Size = AuthoredSizePolicy.FromPolicy(view.SizePolicy),
                SourceTexture = view.SourceTextureName,
                BaseMipLevel = view.BaseMipLevel,
                MipLevels = Math.Max(1u, view.MipLevelCount),
                BaseLayer = view.BaseLayer,
                LayerCount = view.LayerCount,
                Format = view.SizedInternalFormat ?? ESizedInternalFormat.Rgba16f,
                DepthStencilAspect = view.DepthStencilAspect,
            },
            FrameBufferSpec { Attachments.Count: > 0 } frameBuffer => ConvertFrameBuffer(frameBuffer),
            RenderBufferSpec renderBuffer => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.RenderBuffer,
                Size = AuthoredSizePolicy.FromPolicy(renderBuffer.SizePolicy),
                RenderBufferStorage = renderBuffer.StorageFormat,
                Samples = Math.Max(1u, renderBuffer.Samples),
            },
            BufferSpec buffer when buffer.ElementCount > 0 => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.Buffer,
                ElementStride = buffer.ElementStride,
                ElementCount = buffer.ElementCount,
                BufferTarget = buffer.Target,
            },
            _ => null,
        };

    private static AuthoredRenderResource ConvertFrameBuffer(FrameBufferSpec spec)
    {
        AuthoredRenderResource resource = new() { Kind = EAuthoredRenderResourceKind.FrameBuffer };
        for (int i = 0; i < spec.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor attachment = spec.Attachments[i];
            resource.Attachments.Add(new AuthoredAttachment
            {
                ResourceName = attachment.ResourceName,
                Attachment = attachment.Attachment,
                MipLevel = attachment.MipLevel,
                LayerIndex = attachment.LayerIndex,
            });
        }

        return resource;
    }

    /// <summary>Converts each schema stage. Library stages stay library stages; other stages become custom stages.</summary>
    public static List<AuthoredPostProcessStage> ConvertSchema(RenderPipelinePostProcessSchema schema)
    {
        List<AuthoredPostProcessStage> stages = [];
        int order = 0;
        foreach (PostProcessStageDescriptor descriptor in schema.StagesByKey.Values)
        {
            AuthoredPostProcessStage stage = new()
            {
                Key = descriptor.Key,
                DisplayName = descriptor.DisplayName,
                IsLibraryStage = AuthoredPostProcessSchema.IsLibraryStageKey(descriptor.Key),
                EditorSection = descriptor.EditorSection,
                Order = order++,
            };

            for (int c = 0; c < schema.Categories.Count; c++)
            {
                PostProcessCategoryDescriptor category = schema.Categories[c];
                if (!category.StageKeys.Contains(descriptor.Key))
                    continue;

                stage.Category = category.Key;
                stage.CategoryDisplayName = category.DisplayName;
                break;
            }

            if (!stage.IsLibraryStage)
            {
                for (int p = 0; p < descriptor.Parameters.Count; p++)
                    stage.Parameters.Add(ConvertParameter(descriptor.Parameters[p]));
            }

            stages.Add(stage);
        }

        return stages;
    }

    private static AuthoredPostProcessParameter ConvertParameter(PostProcessParameterDescriptor descriptor)
    {
        AuthoredPostProcessParameter parameter = new()
        {
            Name = descriptor.Name,
            DisplayName = descriptor.DisplayName,
            Kind = descriptor.Kind,
            Min = descriptor.Min,
            Max = descriptor.Max,
            Step = descriptor.Step,
            IsColor = descriptor.IsColor,
        };

        if (descriptor.DefaultValue is { } value)
        {
            try
            {
                parameter.SetDefaultValue(value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
            {
                // A default of an unexpected type keeps the zero default of the parameter.
            }
        }

        return parameter;
    }

    /// <summary>
    /// Sets each delegate property of <paramref name="command"/> that differs from the type default back to the
    /// default, and records it.
    /// </summary>
    private static void StripCode(ViewportRenderCommand command, List<string> unconverted)
    {
        PropertyInfo[] properties = RenderCommandGraphCloner.GetCopiedProperties(command.GetType());
        ViewportRenderCommand? defaults = null;
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            if (!typeof(Delegate).IsAssignableFrom(property.PropertyType) || property.GetValue(command) is not Delegate value)
                continue;

            defaults ??= RenderCommandGraphCloner.CreateCommand(command.GetType());
            object? defaultValue = property.GetValue(defaults);
            if (Equals(value, defaultValue))
                continue;

            property.SetValue(command, defaultValue);
            unconverted.Add(string.Concat(RenderCommandInfo.Get(command).DisplayName, ".", property.Name, ": code delegate removed."));
        }
    }
}
