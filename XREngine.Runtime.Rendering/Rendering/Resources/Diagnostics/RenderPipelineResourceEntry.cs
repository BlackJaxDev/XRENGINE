using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Resources.Diagnostics;

/// <summary>
/// One logical resource of a pipeline generation: its declaration, its resolved size and format, its estimated
/// memory, and the render passes that write and read it. Entries are immutable after the snapshot is built.
/// </summary>
public sealed class RenderPipelineResourceEntry
{
    internal readonly List<int> ProducerList = [];
    internal readonly List<int> ConsumerList = [];
    internal readonly List<string> UsageNameList = [];

    internal RenderPipelineResourceEntry(string name, RenderPipelineResourceSpec? spec)
    {
        Name = name;
        Spec = spec;
        Kind = spec?.Kind;
    }

    /// <summary>Gets the logical resource name.</summary>
    public string Name { get; }

    /// <summary>Gets the declaration, or null when a pass uses a name that the layout does not declare.</summary>
    public RenderPipelineResourceSpec? Spec { get; }

    /// <summary>Gets the declared kind, or null for an undeclared resource.</summary>
    public RenderPipelineResourceKind? Kind { get; }

    /// <summary>Gets the state flags of the resource.</summary>
    public ERenderPipelineResourceFlags Flags { get; internal set; }

    /// <summary>Gets the format text, such as <c>Rgba16f</c>, or an empty string when the format is not known.</summary>
    public string FormatText { get; internal set; } = string.Empty;

    /// <summary>Gets the resolved width in pixels, or zero when the size depends on code or is not known.</summary>
    public uint Width { get; internal set; }

    /// <summary>Gets the resolved height in pixels, or zero when the size depends on code or is not known.</summary>
    public uint Height { get; internal set; }

    /// <summary>Gets the number of array layers.</summary>
    public uint Layers { get; internal set; } = 1;

    /// <summary>Gets the number of samples for each pixel.</summary>
    public uint Samples { get; internal set; } = 1;

    /// <summary>Gets the number of mip levels.</summary>
    public uint MipLevels { get; internal set; } = 1;

    /// <summary>
    /// Gets the estimated memory in bytes. Views, framebuffers, and external resources own no memory and report zero.
    /// </summary>
    public ulong EstimatedBytes { get; internal set; }

    /// <summary>Gets the indices of the passes that write the resource, in execution order.</summary>
    public IReadOnlyList<int> Producers => ProducerList;

    /// <summary>Gets the indices of the passes that read the resource, in execution order.</summary>
    public IReadOnlyList<int> Consumers => ConsumerList;

    /// <summary>Gets the logical render graph names that map to this resource, such as <c>fbo::Name::color[0]</c>.</summary>
    public IReadOnlyList<string> UsageNames => UsageNameList;

    /// <summary>Gets the position of the first pass that uses the resource in the execution order, or -1 when no pass uses it.</summary>
    public int FirstUseOrder { get; internal set; } = -1;

    /// <summary>Gets the position of the last pass that uses the resource in the execution order, or -1 when no pass uses it.</summary>
    public int LastUseOrder { get; internal set; } = -1;

    /// <summary>Gets whether the layout declares the resource but no pass uses it.</summary>
    public bool IsDeclaredButUnused
        => (Flags & (ERenderPipelineResourceFlags.Declared | ERenderPipelineResourceFlags.Used | ERenderPipelineResourceFlags.Disabled)) == ERenderPipelineResourceFlags.Declared;

    /// <summary>Gets whether a pass uses the resource but the layout does not declare it.</summary>
    public bool IsUsedButUndeclared
        => (Flags & (ERenderPipelineResourceFlags.Declared | ERenderPipelineResourceFlags.Used | ERenderPipelineResourceFlags.External)) == ERenderPipelineResourceFlags.Used;

    /// <summary>Returns true when <paramref name="flag"/> is set.</summary>
    public bool Has(ERenderPipelineResourceFlags flag)
        => (Flags & flag) == flag;

    internal void AddUse(int passIndex, int order, ERenderGraphAccess access, string usageName)
    {
        Flags |= ERenderPipelineResourceFlags.Used;
        if (access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite)
            AddUnique(ProducerList, passIndex);
        if (access is ERenderGraphAccess.Read or ERenderGraphAccess.ReadWrite)
            AddUnique(ConsumerList, passIndex);
        if (!UsageNameList.Contains(usageName))
            UsageNameList.Add(usageName);

        FirstUseOrder = FirstUseOrder < 0 ? order : Math.Min(FirstUseOrder, order);
        LastUseOrder = Math.Max(LastUseOrder, order);
    }

    private static void AddUnique(List<int> list, int value)
    {
        if (!list.Contains(value))
            list.Add(value);
    }
}
