using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Resources.Diagnostics;

/// <summary>
/// Joins the resource layout, the physical resources, and the pass metadata of one pipeline generation into one
/// table for tools. The snapshot is immutable after it is built. Build a new snapshot only when the generation or
/// the pass metadata changes, because the build allocates.
/// </summary>
public sealed class RenderPipelineResourceSnapshot
{
    private readonly RenderPipelineResourceEntry[] _entries;
    private readonly Dictionary<string, RenderPipelineResourceEntry> _byName;
    private readonly Dictionary<string, RenderPipelineResourceEntry[]> _byUsageName;
    private static readonly Dictionary<EPixelInternalFormat, ESizedInternalFormat?> SizedFormatByInternal = [];
    private static readonly Lock SizedFormatLock = new();

    private RenderPipelineResourceSnapshot(
        RenderPipelineResourceLayout layout,
        RenderResourceGeneration? generation,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        IReadOnlyList<RenderPassMetadata> passOrder,
        RenderPipelineResourceEntry[] entries,
        Dictionary<string, RenderPipelineResourceEntry> byName,
        Dictionary<string, RenderPipelineResourceEntry[]> byUsageName)
    {
        Layout = layout;
        Generation = generation;
        SourcePassMetadata = passMetadata;
        PassOrder = passOrder;
        _entries = entries;
        _byName = byName;
        _byUsageName = byUsageName;

        for (int i = 0; i < entries.Length; i++)
        {
            RenderPipelineResourceEntry entry = entries[i];
            if (entry.IsDeclaredButUnused)
                DeclaredUnusedCount++;
            if (entry.IsUsedButUndeclared)
                UsedUndeclaredCount++;
            if (entry.Has(ERenderPipelineResourceFlags.External))
            {
                ExternalCount++;
                continue;
            }

            if (entry.Has(ERenderPipelineResourceFlags.Disabled))
                continue;

            ulong bytes = entry.EstimatedBytes;
            if (entry.Has(ERenderPipelineResourceFlags.History))
                HistoryBytes += bytes;
            else if (entry.Has(ERenderPipelineResourceFlags.Transient))
                TransientBytes += bytes;
            else
                PersistentBytes += bytes;

            if (entry.Has(ERenderPipelineResourceFlags.AliasCandidate))
                AliasCandidateBytes += bytes;
        }
    }

    /// <summary>Gets an empty snapshot.</summary>
    public static RenderPipelineResourceSnapshot Empty { get; } = new(
        RenderPipelineResourceLayout.Empty,
        null,
        null,
        [],
        [],
        new Dictionary<string, RenderPipelineResourceEntry>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, RenderPipelineResourceEntry[]>(StringComparer.Ordinal));

    /// <summary>Gets the resource layout that the snapshot reads.</summary>
    public RenderPipelineResourceLayout Layout { get; }

    /// <summary>Gets the generation that the snapshot reads, or null when the snapshot reads only a layout.</summary>
    public RenderResourceGeneration? Generation { get; }

    /// <summary>Gets the pass metadata reference that the snapshot reads. Tools compare it to find a stale snapshot.</summary>
    public IReadOnlyCollection<RenderPassMetadata>? SourcePassMetadata { get; }

    /// <summary>
    /// Gets the passes in the order that first and last use refer to: the topological order, or the declaration
    /// order when the passes contain a dependency cycle.
    /// </summary>
    public IReadOnlyList<RenderPassMetadata> PassOrder { get; }

    /// <summary>Gets every resource: declared resources in layout order, then undeclared names in first-use order.</summary>
    public IReadOnlyList<RenderPipelineResourceEntry> Entries => _entries;

    /// <summary>Gets the estimated bytes of persistent resources that have no history policy.</summary>
    public ulong PersistentBytes { get; }

    /// <summary>Gets the estimated bytes of transient resources.</summary>
    public ulong TransientBytes { get; }

    /// <summary>Gets the estimated bytes of resources that keep content across frames.</summary>
    public ulong HistoryBytes { get; }

    /// <summary>Gets the estimated bytes of resources whose descriptors allow memory aliasing. Aliasing can save up to this amount.</summary>
    public ulong AliasCandidateBytes { get; }

    /// <summary>Gets the sum of persistent, transient, and history bytes.</summary>
    public ulong TotalBytes => PersistentBytes + TransientBytes + HistoryBytes;

    /// <summary>Gets the number of external or imported resources.</summary>
    public int ExternalCount { get; }

    /// <summary>Gets the number of enabled declarations that no pass uses.</summary>
    public int DeclaredUnusedCount { get; }

    /// <summary>Gets the number of names that passes use but the layout does not declare.</summary>
    public int UsedUndeclaredCount { get; }

    /// <summary>Finds a resource by its logical name.</summary>
    public bool TryGet(string name, out RenderPipelineResourceEntry? entry)
        => _byName.TryGetValue(name, out entry);

    /// <summary>
    /// Finds the resources that a render graph usage name maps to. A framebuffer attachment name maps to the
    /// framebuffer and to the texture attached at that point.
    /// </summary>
    public IReadOnlyList<RenderPipelineResourceEntry> GetForUsageName(string usageName)
        => _byUsageName.TryGetValue(usageName, out RenderPipelineResourceEntry[]? entries) ? entries : [];

    /// <summary>Builds a snapshot from <paramref name="generation"/>, its layout, and its pass metadata.</summary>
    public static RenderPipelineResourceSnapshot Build(RenderResourceGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return Build(generation.Layout, generation.PassMetadata, generation);
    }

    /// <summary>
    /// Builds a snapshot from a layout and pass metadata. Pass <paramref name="generation"/> to read physical sizes and
    /// materialization state; pass null to describe a layout that no generation has materialized.
    /// </summary>
    public static RenderPipelineResourceSnapshot Build(
        RenderPipelineResourceLayout layout,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        RenderResourceGeneration? generation)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Dictionary<string, RenderPipelineResourceEntry> byName = new(StringComparer.OrdinalIgnoreCase);
        List<RenderPipelineResourceEntry> ordered = new(layout.OrderedSpecs.Count + 8);
        RenderPipelineResourceProfile profile = layout.Profile;

        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
        {
            RenderPipelineResourceSpec spec = layout.OrderedSpecs[i];
            if (byName.ContainsKey(spec.Name))
                continue;

            RenderPipelineResourceEntry entry = new(spec.Name, spec);
            Describe(entry, spec, profile, generation?.Registry);
            byName.Add(spec.Name, entry);
            ordered.Add(entry);
        }

        IReadOnlyList<RenderPassMetadata> passOrder = OrderPasses(passMetadata);
        Dictionary<string, RenderPipelineResourceEntry[]> byUsageName = new(StringComparer.Ordinal);
        List<RenderPipelineResourceEntry> targets = new(4);
        for (int order = 0; order < passOrder.Count; order++)
        {
            RenderPassMetadata pass = passOrder[order];
            for (int u = 0; u < pass.ResourceUsages.Count; u++)
            {
                RenderPassResourceUsage usage = pass.ResourceUsages[u];
                if (!byUsageName.TryGetValue(usage.ResourceName, out RenderPipelineResourceEntry[]? mapped))
                {
                    targets.Clear();
                    ResolveUsageTargets(usage.ResourceName, layout, byName, ordered, targets);
                    mapped = [.. targets];
                    byUsageName.Add(usage.ResourceName, mapped);
                }

                for (int t = 0; t < mapped.Length; t++)
                {
                    mapped[t].AddUse(pass.PassIndex, order, usage.Access, usage.ResourceName);
                    if (usage.IsImported)
                        mapped[t].Flags |= ERenderPipelineResourceFlags.External;
                }
            }
        }

        return new RenderPipelineResourceSnapshot(layout, generation, passMetadata, passOrder, [.. ordered], byName, byUsageName);
    }

    private static IReadOnlyList<RenderPassMetadata> OrderPasses(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return [];

        try
        {
            return RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata);
        }
        catch (InvalidOperationException)
        {
            // A dependency cycle has no topological order. The declaration order still gives useful first and last use.
            List<RenderPassMetadata> passes = [.. passMetadata];
            passes.Sort(static (a, b) => a.DeclarationOrder.CompareTo(b.DeclarationOrder));
            return passes;
        }
    }

    /// <summary>
    /// Maps a render graph usage name to snapshot entries. Unknown names become undeclared entries so that tools can
    /// show them.
    /// </summary>
    private static void ResolveUsageTargets(
        string usageName,
        RenderPipelineResourceLayout layout,
        Dictionary<string, RenderPipelineResourceEntry> byName,
        List<RenderPipelineResourceEntry> ordered,
        List<RenderPipelineResourceEntry> targets)
    {
        if (string.Equals(usageName, RenderGraphResourceNames.OutputRenderTarget, StringComparison.Ordinal))
        {
            RenderPipelineResourceEntry output = GetOrAddUndeclared(usageName, byName, ordered);
            output.Flags |= ERenderPipelineResourceFlags.External;
            targets.Add(output);
            return;
        }

        if (usageName.StartsWith("tex::", StringComparison.Ordinal))
        {
            targets.Add(GetOrAddUndeclared(usageName[5..], byName, ordered));
            return;
        }

        if (usageName.StartsWith("fbo::", StringComparison.Ordinal))
        {
            int split = usageName.LastIndexOf("::", StringComparison.Ordinal);
            if (split > 5)
            {
                string fboName = usageName[5..split];
                string attachment = usageName[(split + 2)..];
                RenderPipelineResourceEntry fbo = GetOrAddUndeclared(fboName, byName, ordered);
                targets.Add(fbo);
                if (fbo.Spec is FrameBufferSpec fboSpec)
                    AddAttachmentTargets(fboSpec, attachment, byName, targets);
                return;
            }
        }

        targets.Add(GetOrAddUndeclared(usageName, byName, ordered));
    }

    private static void AddAttachmentTargets(
        FrameBufferSpec spec,
        string attachment,
        Dictionary<string, RenderPipelineResourceEntry> byName,
        List<RenderPipelineResourceEntry> targets)
    {
        for (int i = 0; i < spec.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor descriptor = spec.Attachments[i];
            if (!MatchesAttachment(descriptor.Attachment, attachment) ||
                !byName.TryGetValue(descriptor.ResourceName, out RenderPipelineResourceEntry? texture) ||
                targets.Contains(texture))
            {
                continue;
            }

            targets.Add(texture);
        }
    }

    private static bool MatchesAttachment(EFrameBufferAttachment attachment, string usageAttachment)
    {
        if (usageAttachment.StartsWith("color", StringComparison.Ordinal))
        {
            int index = 0;
            if (usageAttachment.Length > 5 && !int.TryParse(usageAttachment.AsSpan(5), out index))
                return false;
            return attachment == EFrameBufferAttachment.ColorAttachment0 + index;
        }

        return usageAttachment switch
        {
            "depth" => attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment,
            "stencil" => attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment,
            _ => false,
        };
    }

    private static RenderPipelineResourceEntry GetOrAddUndeclared(
        string name,
        Dictionary<string, RenderPipelineResourceEntry> byName,
        List<RenderPipelineResourceEntry> ordered)
    {
        if (byName.TryGetValue(name, out RenderPipelineResourceEntry? entry))
            return entry;

        entry = new RenderPipelineResourceEntry(name, null);
        byName.Add(name, entry);
        ordered.Add(entry);
        return entry;
    }

    private static void Describe(
        RenderPipelineResourceEntry entry,
        RenderPipelineResourceSpec spec,
        RenderPipelineResourceProfile profile,
        RenderResourceRegistry? registry)
    {
        ERenderPipelineResourceFlags flags = ERenderPipelineResourceFlags.Declared;
        if (spec.Required)
            flags |= ERenderPipelineResourceFlags.Required;
        if (spec.HistoryPolicy != RenderResourceHistoryPolicy.None)
            flags |= ERenderPipelineResourceFlags.History;
        if (spec.Lifetime == RenderResourceLifetime.Transient)
            flags |= ERenderPipelineResourceFlags.Transient;
        if (spec.Lifetime == RenderResourceLifetime.External || spec.Kind == RenderPipelineResourceKind.External)
            flags |= ERenderPipelineResourceFlags.External;
        if (!spec.IsEnabled(profile))
            flags |= ERenderPipelineResourceFlags.Disabled;

        (entry.Width, entry.Height) = ResolveSize(spec.SizePolicy, profile);
        switch (spec)
        {
            case TextureSpec texture:
                entry.Samples = Math.Max(1u, texture.Samples);
                entry.Layers = Math.Max(1u, texture.Layers);
                entry.MipLevels = Math.Max(1u, texture.MipPolicy.MipLevelCount);
                ESizedInternalFormat? sized = texture.SizedInternalFormat ?? ToSized(texture.InternalFormat);
                entry.FormatText = sized?.ToString() ?? texture.InternalFormat?.ToString() ?? string.Empty;
                if (registry is not null && registry.TextureRecords.TryGetValue(spec.Name, out RenderTextureResource? textureRecord))
                {
                    if (textureRecord.Descriptor.SupportsAliasing)
                        flags |= ERenderPipelineResourceFlags.AliasCandidate;
                    if (textureRecord.Instance is { } instance)
                    {
                        flags |= ERenderPipelineResourceFlags.Materialized;
                        entry.Width = (uint)Math.Max(0f, instance.WidthHeightDepth.X);
                        entry.Height = (uint)Math.Max(0f, instance.WidthHeightDepth.Y);
                    }
                }

                entry.EstimatedBytes = EstimateTextureBytes(entry, sized);
                break;
            case RenderBufferSpec renderBuffer:
                entry.Samples = Math.Max(1u, renderBuffer.Samples);
                entry.FormatText = renderBuffer.StorageFormat.ToString();
                if (registry is not null && registry.RenderBufferRecords.TryGetValue(spec.Name, out RenderRenderBufferResource? renderBufferRecord) && renderBufferRecord.Instance is not null)
                    flags |= ERenderPipelineResourceFlags.Materialized;
                entry.EstimatedBytes = (ulong)RuntimeEngine.Rendering.Stats.PixelFormats.GetBytesPerPixel(renderBuffer.StorageFormat) *
                    entry.Width * entry.Height * entry.Samples;
                break;
            case BufferSpec buffer:
                entry.Width = 0;
                entry.Height = 0;
                entry.FormatText = buffer.Target.ToString();
                entry.EstimatedBytes = buffer.SizeInBytes > 0 ? buffer.SizeInBytes : (ulong)buffer.ElementStride * buffer.ElementCount;
                if (registry is not null && registry.BufferRecords.TryGetValue(spec.Name, out RenderBufferResource? bufferRecord) && bufferRecord.Instance is not null)
                    flags |= ERenderPipelineResourceFlags.Materialized;
                break;
            case TextureViewSpec view:
                entry.FormatText = view.SizedInternalFormat?.ToString() ?? string.Empty;
                entry.Layers = Math.Max(1u, view.LayerCount);
                entry.MipLevels = Math.Max(1u, view.MipLevelCount);
                if (registry is not null && registry.TextureRecords.TryGetValue(spec.Name, out RenderTextureResource? viewRecord) && viewRecord.Instance is not null)
                    flags |= ERenderPipelineResourceFlags.Materialized;
                break;
            case FrameBufferSpec:
                if (registry is not null && registry.FrameBufferRecords.TryGetValue(spec.Name, out RenderFrameBufferResource? fboRecord) && fboRecord.Instance is not null)
                    flags |= ERenderPipelineResourceFlags.Materialized;
                break;
        }

        entry.Flags = flags;
    }

    private static (uint Width, uint Height) ResolveSize(RenderResourceSizePolicy size, RenderPipelineResourceProfile profile)
        => size.Resolve(profile);

    private static ulong EstimateTextureBytes(RenderPipelineResourceEntry entry, ESizedInternalFormat? sized)
    {
        if (entry.Width == 0 || entry.Height == 0)
            return 0;

        ulong bytesPerPixel = (ulong)(sized is { } format ? RuntimeEngine.Rendering.Stats.PixelFormats.GetBytesPerPixel(format) : 4);
        ulong baseLevel = bytesPerPixel * entry.Width * entry.Height * entry.Layers * entry.Samples;

        // Each mip level holds a quarter of the texels of the level above it.
        ulong total = 0;
        ulong level = baseLevel;
        for (uint i = 0; i < entry.MipLevels && level > 0; i++, level /= 4)
            total += level;
        return total;
    }

    /// <summary>
    /// Maps a GL internal format to the engine sized format with the same name. The two enums use different values,
    /// so the map compares names. Results are cached.
    /// </summary>
    private static ESizedInternalFormat? ToSized(EPixelInternalFormat? internalFormat)
    {
        if (internalFormat is not { } format)
            return null;

        lock (SizedFormatLock)
        {
            if (!SizedFormatByInternal.TryGetValue(format, out ESizedInternalFormat? sized))
            {
                sized = Enum.TryParse(format.ToString(), ignoreCase: true, out ESizedInternalFormat parsed) ? parsed : null;
                SizedFormatByInternal.Add(format, sized);
            }

            return sized;
        }
    }
}
