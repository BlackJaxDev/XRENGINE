using System.Globalization;
using XREngine.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Builds the cached lines of the Resources view detail tabs for one resource. The lines rebuild only when the
/// selected resource, the resource snapshot, or the generations of the instance change.
/// </summary>
public sealed class RenderPipelineWorkbenchResourceDetail
{
    private string? _resourceName;
    private RenderPipelineResourceSnapshot? _snapshot;
    private RenderResourceGeneration? _active;
    private RenderResourceGeneration? _pending;
    private int _retiredCount = -1;

    /// <summary>Gets the snapshot entry of the selected resource, or null when the snapshot has no such resource.</summary>
    public RenderPipelineResourceEntry? Entry { get; private set; }

    /// <summary>Gets the Spec tab lines.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> Spec { get; } = new(32);

    /// <summary>Gets the Generations tab lines.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> Generations { get; } = new(32);

    /// <summary>Gets the Usage tab lines.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> Usage { get; } = new(32);

    /// <summary>Gets the Physical tab lines.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> Physical { get; } = new(16);

    /// <summary>Gets how many times the lines were rebuilt.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Returns the lines of <paramref name="tab"/>.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> GetTab(ERenderPipelineResourceDetailTab tab)
        => tab switch
        {
            ERenderPipelineResourceDetailTab.Generations => Generations,
            ERenderPipelineResourceDetailTab.Usage => Usage,
            ERenderPipelineResourceDetailTab.Physical => Physical,
            _ => Spec,
        };

    /// <summary>Rebuilds the lines when an input changed.</summary>
    public void Update(RenderPipelineWorkbenchModel model, string? resourceName)
    {
        XRRenderPipelineInstance? instance = model.Instance;
        RenderResourceGeneration? active = instance?.ActiveGeneration;
        RenderResourceGeneration? pending = instance?.PendingGeneration;
        int retiredCount = instance?.RetiredGenerations.Count ?? 0;
        if (string.Equals(_resourceName, resourceName, StringComparison.Ordinal) &&
            ReferenceEquals(_snapshot, model.Resources) &&
            ReferenceEquals(_active, active) &&
            ReferenceEquals(_pending, pending) &&
            _retiredCount == retiredCount)
        {
            return;
        }

        _resourceName = resourceName;
        _snapshot = model.Resources;
        _active = active;
        _pending = pending;
        _retiredCount = retiredCount;
        BuildCount++;
        Spec.Clear();
        Generations.Clear();
        Usage.Clear();
        Physical.Clear();
        Entry = null;

        if (resourceName is null)
            return;

        Entry = model.Resources.TryGet(resourceName, out RenderPipelineResourceEntry? entry) ? entry : null;
        BuildSpec(model.Resources, Entry);
        BuildGenerations(instance, resourceName);
        BuildUsage(model, Entry);
        BuildPhysical(instance, resourceName);
    }

    private void BuildSpec(RenderPipelineResourceSnapshot snapshot, RenderPipelineResourceEntry? entry)
    {
        if (entry?.Spec is not { } spec)
        {
            Add(Spec, entry is null
                ? "The selected generation has no resource with this name."
                : "The layout does not declare this resource. A pass uses it, so the pipeline creates or imports it outside the layout.");
            return;
        }

        Header(Spec, "Declaration");
        Add(Spec, string.Concat("Kind: ", spec.Kind.ToString()));
        Add(Spec, string.Concat("Lifetime: ", spec.Lifetime.ToString()));
        Add(Spec, string.Concat("Size policy: ", spec.SizePolicy.ToString()));
        Add(Spec, string.Concat("Usage: ", spec.Usage.ToString()));
        Add(Spec, string.Concat("History: ", spec.HistoryPolicy.ToString()));
        Add(Spec, string.Concat("Required: ", spec.Required ? "yes" : "no"));
        if (!string.IsNullOrEmpty(spec.DebugLabel))
            Add(Spec, string.Concat("Debug label: ", spec.DebugLabel));
        Add(Spec, spec.Predicate is null
            ? "Predicate: none"
            : string.Concat("Predicate: ", entry.Has(ERenderPipelineResourceFlags.Disabled) ? "false for this profile (disabled)" : "true for this profile"));
        if (spec.Dependencies.Count > 0)
        {
            Header(Spec, "Dependencies");
            for (int i = 0; i < spec.Dependencies.Count; i++)
                Spec.Add(new RenderPipelineWorkbenchInspectorItem(spec.Dependencies[i], RenderPipelineWorkbenchSelectionEntry.ForResource(spec.Dependencies[i]), 1));
        }

        Header(Spec, "Type fields");
        switch (spec)
        {
            case TextureSpec texture:
                Add(Spec, string.Concat("Format: ", entry.FormatText.Length > 0 ? entry.FormatText : "-"));
                Add(Spec, string.Concat("Samples: ", texture.Samples.ToString(CultureInfo.InvariantCulture), "  Layers: ", texture.Layers.ToString(CultureInfo.InvariantCulture)));
                Add(Spec, string.Concat("Mips: ", texture.MipPolicy.ToString()));
                Add(Spec, string.Concat("Stereo compatible: ", texture.StereoCompatible ? "yes" : "no", "  Storage: ", texture.RequiresStorageUsage ? "yes" : "no"));
                Add(Spec, string.Concat("Array target: ", texture.ArrayTarget ? "yes" : "no", "  Multisample: ", texture.Multisample ? "yes" : "no"));
                Add(Spec, texture.Factory is null ? "Factory: generic" : "Factory: code delegate");
                break;
            case FrameBufferSpec fbo:
                for (int i = 0; i < fbo.Attachments.Count; i++)
                {
                    var attachment = fbo.Attachments[i];
                    Spec.Add(new RenderPipelineWorkbenchInspectorItem(
                        string.Concat(attachment.Attachment.ToString(), ": ", attachment.ResourceName),
                        RenderPipelineWorkbenchSelectionEntry.ForResource(attachment.ResourceName),
                        1));
                }

                Add(Spec, fbo.Factory is null && fbo.IncrementalFactory is null ? "Factory: generic" : "Factory: code delegate");
                break;
            case BufferSpec buffer:
                Add(Spec, string.Concat("Target: ", buffer.Target.ToString(), "  Usage: ", buffer.BufferUsage.ToString()));
                Add(Spec, string.Concat("Size: ", RenderPipelineWorkbenchText.FormatBytes((long)buffer.SizeInBytes),
                    "  Stride: ", buffer.ElementStride.ToString(CultureInfo.InvariantCulture),
                    "  Count: ", buffer.ElementCount.ToString(CultureInfo.InvariantCulture)));
                Add(Spec, string.Concat("Access: ", buffer.AccessPattern.ToString()));
                break;
            case TextureViewSpec view:
                Spec.Add(new RenderPipelineWorkbenchInspectorItem(
                    string.Concat("Source: ", view.SourceTextureName),
                    RenderPipelineWorkbenchSelectionEntry.ForResource(view.SourceTextureName)));
                Add(Spec, string.Concat("Mips: ", view.BaseMipLevel.ToString(CultureInfo.InvariantCulture), " +", view.MipLevelCount.ToString(CultureInfo.InvariantCulture),
                    "  Layers: ", view.BaseLayer.ToString(CultureInfo.InvariantCulture), " +", view.LayerCount.ToString(CultureInfo.InvariantCulture)));
                break;
            case RenderBufferSpec renderBuffer:
                Add(Spec, string.Concat("Storage: ", renderBuffer.StorageFormat.ToString(), "  Samples: ", renderBuffer.Samples.ToString(CultureInfo.InvariantCulture)));
                break;
            case ExternalResourceSpec external:
                Add(Spec, string.Concat("External kind: ", external.ExternalKind.ToString()));
                Add(Spec, string.Concat("Ownership: ", external.Ownership.ToString(), "  Synchronization: ", external.Synchronization.ToString()));
                break;
        }

        Header(Spec, "Profile");
        Add(Spec, snapshot.Layout.Profile.ToString());
    }

    private void BuildGenerations(XRRenderPipelineInstance? instance, string resourceName)
    {
        if (instance is null)
        {
            Add(Generations, "Select a pipeline instance to see its generations.");
            return;
        }

        RenderResourceGeneration? active = instance.ActiveGeneration;
        Header(Generations, "Active generation");
        if (active is null)
        {
            Add(Generations, "The instance has no active generation.");
        }
        else
        {
            AddGeneration(active);
        }

        if (instance.PendingGeneration is { } pending)
        {
            Header(Generations, "Pending generation");
            AddGeneration(pending);
            if (active is not null)
                Add(Generations, string.Concat("Key change: ", XRRenderPipelineInstance.DescribeResourceGenerationKeyDelta(active.Key, pending.Key)));
        }

        if (!string.IsNullOrEmpty(instance.LastResourceGenerationFailure))
        {
            Header(Generations, "Last failure");
            Add(Generations, instance.LastResourceGenerationFailure!);
        }

        if (active is null)
            return;

        // The newest retired generation is the one that the active generation replaced.
        RenderResourceGeneration? previous = null;
        foreach (RenderResourceGeneration retired in instance.RetiredGenerations)
            previous = retired;

        Header(Generations, "Last rebuild");
        if (previous is null)
        {
            Add(Generations, "No retired generation. The active generation is the first build or the instance drained its history.");
            return;
        }

        Add(Generations, string.Concat("Key change: ", XRRenderPipelineInstance.DescribeResourceGenerationKeyDelta(previous.Key, active.Key)));
        bool hadSpec = previous.Layout.TryGet(resourceName, out RenderPipelineResourceSpec? oldSpec);
        bool hasSpec = active.Layout.TryGet(resourceName, out RenderPipelineResourceSpec? newSpec);
        Add(Generations, (hadSpec, hasSpec) switch
        {
            (false, true) => "This resource is new in the active generation.",
            (true, false) => "The active generation removed this resource.",
            (true, true) when !RenderPipelineResourceLayout.AreSpecsStructurallyEquivalent(oldSpec!, newSpec!) => "The declaration of this resource changed in the active generation.",
            (true, true) => "The declaration of this resource did not change.",
            _ => "Neither generation declares this resource.",
        });
    }

    private void AddGeneration(RenderResourceGeneration generation)
    {
        Add(Generations, string.Concat("Status: ", generation.Status.ToString()), 1);
        Add(Generations, string.Concat("Key: ", generation.Key.ToString()), 1);
        if (!string.IsNullOrEmpty(generation.CommitReason))
            Add(Generations, string.Concat("Commit reason: ", generation.CommitReason), 1);
        Add(Generations, string.Concat(
            "Build: ", RenderPipelineWorkbenchText.FormatMilliseconds(generation.BuildDuration.TotalMilliseconds),
            "  Materialized specs: ", generation.MaterializedSpecCount.ToString(CultureInfo.InvariantCulture)), 1);
        for (int i = 0; i < generation.Diagnostics.Count; i++)
            Add(Generations, generation.Diagnostics[i], 2);
    }

    private void BuildUsage(RenderPipelineWorkbenchModel model, RenderPipelineResourceEntry? entry)
    {
        if (entry is null || (entry.Producers.Count == 0 && entry.Consumers.Count == 0))
        {
            Add(Usage, "No pass uses this resource.");
            return;
        }

        Header(Usage, "Written by");
        AddPasses(model, entry.Producers);
        Header(Usage, "Read by");
        AddPasses(model, entry.Consumers);
        Header(Usage, "Render graph names");
        for (int i = 0; i < entry.UsageNames.Count; i++)
            Usage.Add(new RenderPipelineWorkbenchInspectorItem(entry.UsageNames[i], RenderPipelineWorkbenchSelectionEntry.ForResource(entry.UsageNames[i]), 1));
    }

    private void AddPasses(RenderPipelineWorkbenchModel model, IReadOnlyList<int> passIndices)
    {
        if (passIndices.Count == 0)
        {
            Add(Usage, "none", 1);
            return;
        }

        for (int i = 0; i < passIndices.Count; i++)
        {
            int passIndex = passIndices[i];
            string title = model.PassGraph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex)
                ? model.PassGraph.Nodes[nodeIndex].Title
                : string.Concat("#", passIndex.ToString(CultureInfo.InvariantCulture));
            Usage.Add(new RenderPipelineWorkbenchInspectorItem(title, RenderPipelineWorkbenchSelectionEntry.ForPass(passIndex), 1));
        }
    }

    private void BuildPhysical(XRRenderPipelineInstance? instance, string resourceName)
    {
        RenderResourceRegistry? registry = instance?.ActiveGeneration?.Registry;
        if (registry is null)
        {
            Add(Physical, "No active generation.");
            return;
        }

        if (registry.TextureRecords.TryGetValue(resourceName, out RenderTextureResource? texture))
        {
            Header(Physical, "Texture");
            Add(Physical, texture.Instance is { } instanceTexture
                ? string.Concat(instanceTexture.GetType().Name, "  ", instanceTexture.WidthHeightDepth.ToString())
                : "Not materialized.");
            Add(Physical, string.Concat("Owns instance: ", texture.OwnsInstance ? "yes" : "no"));
            Add(Physical, string.Concat("Descriptor: ", texture.Descriptor.ToString()));
        }

        if (registry.FrameBufferRecords.TryGetValue(resourceName, out RenderFrameBufferResource? fbo))
        {
            Header(Physical, "Framebuffer");
            Add(Physical, fbo.Instance is null ? "Not materialized." : fbo.Instance.GetType().Name);
            for (int i = 0; i < fbo.Descriptor.Attachments.Count; i++)
                Add(Physical, string.Concat(fbo.Descriptor.Attachments[i].Attachment.ToString(), ": ", fbo.Descriptor.Attachments[i].ResourceName), 1);
        }

        if (registry.BufferRecords.TryGetValue(resourceName, out RenderBufferResource? buffer))
        {
            Header(Physical, "Buffer");
            Add(Physical, buffer.Instance is null ? "Not materialized." : buffer.Instance.GetType().Name);
            Add(Physical, string.Concat("Owns instance: ", buffer.OwnsInstance ? "yes" : "no"));
        }

        if (registry.RenderBufferRecords.TryGetValue(resourceName, out RenderRenderBufferResource? renderBuffer))
        {
            Header(Physical, "Render buffer");
            Add(Physical, renderBuffer.Instance is null ? "Not materialized." : renderBuffer.Instance.GetType().Name);
        }

        if (Physical.Count == 0)
            Add(Physical, "The active generation holds no physical record with this name.");
    }

    private static void Header(List<RenderPipelineWorkbenchInspectorItem> items, string text)
        => items.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, 0, IsHeader: true));

    private static void Add(List<RenderPipelineWorkbenchInspectorItem> items, string text, int indent = 0)
        => items.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, indent));
}
