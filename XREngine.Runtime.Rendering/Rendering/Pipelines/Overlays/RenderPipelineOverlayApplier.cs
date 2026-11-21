using System.Globalization;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>
/// Applies an overlay chain to a base command chain, a resource layout builder, or a schema builder. Each part checks
/// every target first and changes nothing when one target is missing, so a part never applies in part.
/// </summary>
internal static class RenderPipelineOverlayApplier
{
    /// <summary>
    /// Applies the command patches of <paramref name="overlays"/> to <paramref name="chain"/>, root overlay first. A
    /// failed patch stops the apply. The caller then uses a fresh base chain, because the failed apply can leave
    /// <paramref name="chain"/> changed.
    /// </summary>
    /// <returns>True when every patch applied.</returns>
    public static bool ApplyCommands(
        RenderPipeline pipeline,
        ViewportRenderCommandContainer chain,
        IReadOnlyList<RenderPipelineOverlay> overlays,
        IReadOnlyDictionary<string, string> aliases,
        List<RenderPipelinePatchResult> results,
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers,
        List<string> removedItems)
    {
        bool failed = false;
        for (int o = 0; o < overlays.Count; o++)
        {
            List<RenderCommandPatch> patches = overlays[o].CommandPatches;
            for (int p = 0; p < patches.Count; p++)
            {
                RenderCommandPatch patch = patches[p];
                if (failed)
                {
                    results.Add(new RenderPipelinePatchResult(o, patch, ERenderPipelinePatchStatus.NotApplied, "An earlier patch failed."));
                    continue;
                }

                RenderPipelinePatchResult result = ApplyCommandPatch(pipeline, chain, patch, o, p, aliases, markers, removedItems);
                results.Add(result);
                failed = result.IsError;
            }
        }

        return !failed;
    }

    /// <summary>Applies the resource patches of <paramref name="overlays"/> to <paramref name="builder"/>, root overlay first.</summary>
    /// <returns>True when every patch applied. On false, the builder is unchanged.</returns>
    public static bool ApplyResources(
        RenderPipelineResourceLayoutBuilder builder,
        IReadOnlyList<RenderPipelineOverlay> overlays,
        AuthoredRenderResourceFactory.AttachmentResolver resolver,
        List<RenderPipelinePatchResult> results)
    {
        // Check each target against the names that the base and the earlier patches declare.
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        bool valid = true;
        for (int o = 0; o < overlays.Count; o++)
        {
            List<RenderResourcePatch> patches = overlays[o].ResourcePatches;
            for (int p = 0; p < patches.Count; p++)
            {
                RenderResourcePatch patch = patches[p];
                bool exists = builder.Contains(patch.Name) ? !names.Contains(string.Concat("-", patch.Name)) : names.Contains(patch.Name);
                string? error = patch.Kind switch
                {
                    ERenderResourcePatchKind.Add when exists => string.Concat("The base already declares '", patch.Name, "'. Use override."),
                    ERenderResourcePatchKind.Add or ERenderResourcePatchKind.Override when patch.Resource is null => "The patch has no declaration.",
                    ERenderResourcePatchKind.Override or ERenderResourcePatchKind.Remove when !exists => string.Concat("The base has no resource '", patch.Name, "'."),
                    _ => null,
                };

                if (error is not null)
                {
                    bool missing = patch.Kind != ERenderResourcePatchKind.Add && !exists;
                    results.Add(new RenderPipelinePatchResult(o, patch, missing ? ERenderPipelinePatchStatus.AnchorMissing : ERenderPipelinePatchStatus.Failed, error));
                    valid = false;
                    continue;
                }

                results.Add(new RenderPipelinePatchResult(o, patch, ERenderPipelinePatchStatus.Applied, string.Empty));
                if (patch.Kind == ERenderResourcePatchKind.Remove)
                {
                    names.Remove(patch.Name);
                    names.Add(string.Concat("-", patch.Name));
                }
                else
                {
                    names.Add(patch.Name);
                    names.Remove(string.Concat("-", patch.Name));
                }
            }
        }

        if (!valid)
            return false;

        for (int o = 0; o < overlays.Count; o++)
        {
            List<RenderResourcePatch> patches = overlays[o].ResourcePatches;
            for (int p = 0; p < patches.Count; p++)
            {
                RenderResourcePatch patch = patches[p];
                if (patch.Kind != ERenderResourcePatchKind.Add)
                    builder.Remove(patch.Name);
                if (patch.Kind != ERenderResourcePatchKind.Remove)
                {
                    AuthoredRenderResource resource = patch.Resource!.Clone();
                    resource.Name = patch.Name;
                    AuthoredRenderResourceFactory.Declare(builder, resource, resolver);
                }
            }
        }

        return true;
    }

    /// <summary>Applies the schema patches of <paramref name="overlays"/> to <paramref name="builder"/>, root overlay first.</summary>
    /// <returns>True when every patch applied. On false, the builder is unchanged.</returns>
    public static bool ApplySchema(
        RenderPipelinePostProcessSchemaBuilder builder,
        IReadOnlyList<RenderPipelineOverlay> overlays,
        List<RenderPipelinePatchResult> results)
    {
        HashSet<string> added = new(StringComparer.Ordinal);
        HashSet<string> hidden = new(StringComparer.Ordinal);
        bool valid = true;
        for (int o = 0; o < overlays.Count; o++)
        {
            List<PostProcessSchemaPatch> patches = overlays[o].SchemaPatches;
            for (int p = 0; p < patches.Count; p++)
            {
                PostProcessSchemaPatch patch = patches[p];
                bool exists = (builder.HasStage(patch.StageKey) || added.Contains(patch.StageKey)) && !hidden.Contains(patch.StageKey);
                string? error = patch.Kind switch
                {
                    EPostProcessSchemaPatchKind.AddStage when patch.Stage is null => "The patch has no stage declaration.",
                    EPostProcessSchemaPatchKind.AddStage when exists => string.Concat("The base already declares stage '", patch.StageKey, "'."),
                    EPostProcessSchemaPatchKind.AddStage => null,
                    _ when !exists => string.Concat("The base has no stage '", patch.StageKey, "'."),
                    EPostProcessSchemaPatchKind.OverrideDefault when string.IsNullOrWhiteSpace(patch.ParameterName) || patch.ValueText is null => "The patch has no parameter or value.",
                    EPostProcessSchemaPatchKind.MoveToCategory when string.IsNullOrWhiteSpace(patch.CategoryKey) => "The patch has no category.",
                    _ => null,
                };

                if (error is not null)
                {
                    bool missing = patch.Kind != EPostProcessSchemaPatchKind.AddStage && !exists;
                    results.Add(new RenderPipelinePatchResult(o, patch, missing ? ERenderPipelinePatchStatus.AnchorMissing : ERenderPipelinePatchStatus.Failed, error));
                    valid = false;
                    continue;
                }

                results.Add(new RenderPipelinePatchResult(o, patch, ERenderPipelinePatchStatus.Applied, string.Empty));
                if (patch.Kind == EPostProcessSchemaPatchKind.AddStage)
                {
                    added.Add(patch.StageKey);
                    hidden.Remove(patch.StageKey);
                }
                else if (patch.Kind == EPostProcessSchemaPatchKind.HideStage)
                {
                    hidden.Add(patch.StageKey);
                }
            }
        }

        if (!valid)
            return false;

        for (int o = 0; o < overlays.Count; o++)
        {
            List<PostProcessSchemaPatch> patches = overlays[o].SchemaPatches;
            for (int p = 0; p < patches.Count; p++)
            {
                PostProcessSchemaPatch patch = patches[p];
                switch (patch.Kind)
                {
                    case EPostProcessSchemaPatchKind.AddStage:
                        AuthoredPostProcessStage stage = patch.Stage!.Clone();
                        stage.Key = patch.StageKey;
                        AuthoredPostProcessSchema.Describe(builder, [stage]);
                        break;
                    case EPostProcessSchemaPatchKind.HideStage:
                        builder.RemoveStage(patch.StageKey);
                        break;
                    case EPostProcessSchemaPatchKind.OverrideDefault:
                        string valueText = patch.ValueText!;
                        builder.OverrideParameterDefault(patch.StageKey, patch.ParameterName!, type => RenderPipelineScript.ConvertValueText(valueText, type));
                        break;
                    case EPostProcessSchemaPatchKind.MoveToCategory:
                        builder.MoveStageToCategory(patch.StageKey, patch.CategoryKey!);
                        break;
                }
            }
        }

        return true;
    }

    private static RenderPipelinePatchResult ApplyCommandPatch(
        RenderPipeline pipeline,
        ViewportRenderCommandContainer chain,
        RenderCommandPatch patch,
        int overlayIndex,
        int patchIndex,
        IReadOnlyDictionary<string, string> aliases,
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers,
        List<string> removedItems)
    {
        ViewportRenderCommandContainer container;
        int index;
        bool viaAlias = false;
        if (patch.Kind == ERenderCommandPatchKind.AppendTo && string.Equals(patch.Target, RenderCommandPaths.RootTarget, StringComparison.Ordinal))
        {
            container = chain;
            index = -1;
        }
        else if (!RenderCommandPaths.TryResolve(chain, patch.Target, aliases, out container, out index, out viaAlias))
        {
            return new RenderPipelinePatchResult(overlayIndex, patch, ERenderPipelinePatchStatus.AnchorMissing,
                string.Concat("The base has no command '", patch.Target, "'."));
        }

        try
        {
            string prefix = string.Concat("overlay", overlayIndex.ToString(CultureInfo.InvariantCulture), ".patch", patchIndex.ToString(CultureInfo.InvariantCulture));
            switch (patch.Kind)
            {
                case ERenderCommandPatchKind.InsertBefore:
                    InsertCompiled(pipeline, container, index, patch, prefix, markers);
                    break;
                case ERenderCommandPatchKind.InsertAfter:
                    InsertCompiled(pipeline, container, GetRangeEnd(container, index) + 1, patch, prefix, markers);
                    break;
                case ERenderCommandPatchKind.Replace:
                    RemoveRange(container, index, removedItems, patch);
                    InsertCompiled(pipeline, container, index, patch, prefix, markers);
                    break;
                case ERenderCommandPatchKind.Remove:
                    RemoveRange(container, index, removedItems, patch);
                    break;
                case ERenderCommandPatchKind.Wrap:
                    Wrap(pipeline, container, index, patch, prefix, markers);
                    break;
                case ERenderCommandPatchKind.SetProperty:
                    if (string.IsNullOrWhiteSpace(patch.PropertyName) || patch.ValueText is null)
                        throw new InvalidOperationException("The patch has no property or value.");
                    RenderPipelineScript.AssignPropertyText(container[index], patch.PropertyName, patch.ValueText);
                    markers.TryAdd(container[index], ERenderCommandPatchKind.SetProperty);
                    break;
                case ERenderCommandPatchKind.SetCondition:
                    SetCondition(container[index], patch);
                    markers.TryAdd(container[index], ERenderCommandPatchKind.SetCondition);
                    break;
                case ERenderCommandPatchKind.AppendTo:
                    ViewportRenderCommandContainer target = index < 0 ? container : GetFirstChild(container[index]);
                    InsertCompiled(pipeline, target, target.Count, patch, prefix, markers);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or InvalidCastException)
        {
            return new RenderPipelinePatchResult(overlayIndex, patch, ERenderPipelinePatchStatus.Failed, ex.Message);
        }

        return viaAlias
            ? new RenderPipelinePatchResult(overlayIndex, patch, ERenderPipelinePatchStatus.AnchorMoved, "The base renamed the anchor. Accept the rebase to keep the new id.")
            : new RenderPipelinePatchResult(overlayIndex, patch, ERenderPipelinePatchStatus.Applied, patch.UsesStructuralPath ? "The patch uses a structural path." : string.Empty);
    }

    /// <summary>Compiles the commands of <paramref name="patch"/> and inserts them at <paramref name="index"/>. A command with no anchor gets one.</summary>
    private static void InsertCompiled(
        RenderPipeline pipeline,
        ViewportRenderCommandContainer container,
        int index,
        RenderCommandPatch patch,
        string anchorPrefix,
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers)
    {
        ViewportRenderCommandContainer compiled = RenderPipelineScript.Compile(pipeline, patch.CommandText);
        int count = 0;
        while (compiled.Count > 0)
        {
            ViewportRenderCommand command = compiled.RemoveAt(0);
            command.AnchorId ??= string.Concat(anchorPrefix, ".cmd", count.ToString(CultureInfo.InvariantCulture));
            container.Insert(index + count, command);
            markers[command] = patch.Kind;
            count++;
        }
    }

    private static void Wrap(
        RenderPipeline pipeline,
        ViewportRenderCommandContainer container,
        int index,
        RenderCommandPatch patch,
        string anchorPrefix,
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers)
    {
        int end = GetRangeEnd(container, index);
        if (!string.IsNullOrWhiteSpace(patch.WrapScopeText))
        {
            ViewportRenderCommandContainer compiled = RenderPipelineScript.Compile(pipeline, patch.WrapScopeText);
            if (compiled.Count != 1 || compiled[0] is not ViewportStateRenderCommandBase push)
                throw new InvalidOperationException("A wrap scope is one push command, such as bindFBOByName(FrameBufferName=\"HDRSceneFBO\").");

            compiled.RemoveAt(0);
            push.AnchorId ??= string.Concat(anchorPrefix, ".cmd0");
            container.Insert(end + 1, push.PairedPopCommand);
            container.Insert(index, push);
            markers[push] = ERenderCommandPatchKind.Wrap;
            markers[push.PairedPopCommand] = ERenderCommandPatchKind.Wrap;
            return;
        }

        if (patch.Condition is null)
            throw new InvalidOperationException("A wrap needs a scope command or a branch condition.");

        ViewportRenderCommandContainer body = new(pipeline);
        for (int i = index; i <= end; i++)
            body.Add(container.RemoveAt(index));

        VPRC_IfElse branch = new()
        {
            Condition = patch.Condition.Clone(),
            Label = patch.WrapLabel,
            TrueCommands = body,
            AnchorId = string.Concat(anchorPrefix, ".cmd0"),
        };
        container.Insert(index, branch);
        markers[branch] = ERenderCommandPatchKind.Wrap;
    }

    private static void SetCondition(ViewportRenderCommand command, RenderCommandPatch patch)
    {
        if (patch.Condition is null)
            throw new InvalidOperationException("The patch has no condition.");

        switch (command)
        {
            case VPRC_IfElse branch:
                branch.Condition = patch.Condition.Clone();
                break;
            case VPRC_Switch selector:
                selector.Condition = patch.Condition.Clone();
                break;
            default:
                throw new InvalidOperationException(string.Concat("'", patch.Target, "' is a ", command.GetType().Name, ", not a branch."));
        }
    }

    private static ViewportRenderCommandContainer GetFirstChild(ViewportRenderCommand command)
    {
        List<RenderCommandChildContainer> children = [];
        command.CollectChildContainers(children);
        if (children.Count == 0)
            throw new InvalidOperationException(string.Concat(command.GetType().Name, " has no child container."));
        return children[0].Container;
    }

    private static void RemoveRange(ViewportRenderCommandContainer container, int index, List<string> removedItems, RenderCommandPatch patch)
    {
        int end = GetRangeEnd(container, index);
        for (int i = index; i <= end; i++)
        {
            ViewportRenderCommand removed = container.RemoveAt(index);
            removedItems.Add(string.Concat(removed.AnchorId ?? removed.GetType().Name, " (", patch.Describe(), ")"));
        }
    }

    /// <summary>Returns the index of the last command of the range at <paramref name="index"/>: the paired pop of a push, or the command itself.</summary>
    private static int GetRangeEnd(ViewportRenderCommandContainer container, int index)
    {
        if (container[index] is not ViewportStateRenderCommandBase push)
            return index;

        for (int i = index + 1; i < container.Count; i++)
        {
            if (ReferenceEquals(container[i], push.PairedPopCommand))
                return i;
        }

        return index;
    }
}
