using System.Collections.ObjectModel;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Builder for constructing a <see cref="RenderPipelineResourceLayout"/> from a set of <see cref="RenderPipelineResourceSpec"/>s.
/// </summary>
/// <param name="profile">The resource profile to use for the layout.</param>
public sealed partial class RenderPipelineResourceLayoutBuilder(RenderPipelineResourceProfile profile)
{
    private readonly List<RenderPipelineResourceSpec> _specs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderPipelineResourceLayoutBuilder"/> class with an empty resource profile.
    /// </summary>
    public RenderPipelineResourceLayoutBuilder()
        : this(RenderPipelineResourceProfile.Empty) { }

    /// <summary>
    /// Gets the resource profile associated with this builder.
    /// </summary>
    public RenderPipelineResourceProfile Profile { get; } = profile;

    /// <summary>
    /// Creates a new <see cref="TextureSpecBuilder"/> for defining a texture resource with the specified name.
    /// </summary>
    /// <param name="name">The name of the texture resource.</param>
    /// <returns>A <see cref="TextureSpecBuilder"/> for configuring the texture resource.</returns>
    public TextureSpecBuilder Texture(string name)
        => new(this, name);

    /// <summary>
    /// Creates a new <see cref="TextureViewSpecBuilder"/> for defining a texture view resource with the specified name and source texture.
    /// </summary>
    /// <param name="name">The name of the texture view resource.</param>
    /// <param name="sourceTextureName">The name of the source texture resource.</param>
    /// <returns>A <see cref="TextureViewSpecBuilder"/> for configuring the texture view resource.</returns>
    public TextureViewSpecBuilder TextureView(string name, string sourceTextureName)
        => new(this, name, sourceTextureName);

    /// <summary>
    /// Creates a new <see cref="RenderBufferSpecBuilder"/> for defining a render buffer resource with the specified name.
    /// </summary>
    /// <param name="name">The name of the render buffer resource.</param>
    /// <returns>A <see cref="RenderBufferSpecBuilder"/> for configuring the render buffer resource.</returns>
    public RenderBufferSpecBuilder RenderBuffer(string name)
        => new(this, name);

    /// <summary>
    /// Creates a new <see cref="BufferSpecBuilder"/> for defining a buffer resource with the specified name.
    /// </summary>
    /// <param name="name">The name of the buffer resource.</param>
    /// <returns>A <see cref="BufferSpecBuilder"/> for configuring the buffer resource.</returns>
    public BufferSpecBuilder Buffer(string name)
        => new(this, name);

    /// <summary>
    /// Creates a new <see cref="FrameBufferSpecBuilder"/> for defining a frame buffer resource with the specified name.
    /// </summary>
    /// <param name="name">The name of the frame buffer resource.</param>
    /// <returns>A <see cref="FrameBufferSpecBuilder"/> for configuring the frame buffer resource.</returns>
    public FrameBufferSpecBuilder FrameBuffer(string name)
        => new(this, name);

    /// <summary>
    /// Declares an externally owned resource that is imported at execution.
    /// </summary>
    public ExternalResourceSpecBuilder External(string name)
        => new(this, name);

    /// <summary>
    /// Declares a generation-owned fullscreen-quad material/FBO helper that has
    /// no render-target attachments of its own.
    /// </summary>
    public QuadMaterialSpecBuilder QuadMaterial(string name)
        => new(this, name);

    /// <summary>
    /// Adds a <see cref="RenderPipelineResourceSpec"/> to the builder's collection of resource specifications.
    /// </summary>
    /// <param name="spec">The resource specification to add.</param>
    /// <returns>The current <see cref="RenderPipelineResourceLayoutBuilder"/> instance.</returns>
    public RenderPipelineResourceLayoutBuilder Add(RenderPipelineResourceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _specs.Add(spec);
        return this;
    }

    /// <summary>
    /// Builds a <see cref="RenderPipelineResourceLayout"/> from the added resource specifications, validating them against the specified resource profile.
    /// </summary>
    /// <param name="profile">The resource profile to validate against.</param>
    /// <returns>A <see cref="RenderPipelineResourceLayout"/> instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the resource layout is invalid.</exception>
    public RenderPipelineResourceLayout Build(RenderPipelineResourceProfile profile)
    {
        List<string> diagnostics = [];
        List<RenderPipelineResourceSpec> enabled = [];
        Dictionary<string, RenderPipelineResourceSpec> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (RenderPipelineResourceSpec spec in _specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Name))
            {
                diagnostics.Add("Resource name must not be empty.");
                continue;
            }

            if (!spec.IsEnabled(profile))
                continue;

            if (!byName.TryAdd(spec.Name, spec))
            {
                diagnostics.Add($"Duplicate resource '{spec.Name}'.");
                continue;
            }

            enabled.Add(spec);
        }

        foreach (RenderPipelineResourceSpec spec in enabled)
            ValidateSpec(spec, byName, diagnostics);

        if (diagnostics.Count != 0)
            throw new InvalidOperationException("Render pipeline resource layout is invalid: " + string.Join(" ", diagnostics));

        List<RenderPipelineResourceSpec> ordered = TopologicallySort(enabled, byName);
        return new RenderPipelineResourceLayout(
            profile,
            ordered.AsReadOnly(),
            new ReadOnlyDictionary<string, RenderPipelineResourceSpec>(byName));
    }

    /// <summary>
    /// Validates a render pipeline resource specification, adding any diagnostics to the provided list.
    /// </summary>
    /// <param name="spec">The render pipeline resource specification to validate.</param>
    /// <param name="byName">A dictionary of resource specifications keyed by their names.</param>
    /// <param name="diagnostics">A list to which any validation diagnostics will be added.</param>
    private static void ValidateSpec(
        RenderPipelineResourceSpec spec,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName,
        List<string> diagnostics)
    {
        if (spec.SizePolicy.SizeClass == RenderResourceSizeClass.AbsolutePixels
            && (spec.SizePolicy.Width == 0 || spec.SizePolicy.Height == 0)
            && spec.Kind is not RenderPipelineResourceKind.Buffer
                and not RenderPipelineResourceKind.External
                and not RenderPipelineResourceKind.QuadMaterial)
            diagnostics.Add($"Resource '{spec.Name}' has an absolute size policy with zero width or height.");

        foreach (string dependency in spec.Dependencies)
        {
            if (!byName.ContainsKey(dependency))
                diagnostics.Add($"Resource '{spec.Name}' depends on missing resource '{dependency}'.");
        }

        if (spec is TextureViewSpec viewSpec && !byName.ContainsKey(viewSpec.SourceTextureName))
            diagnostics.Add($"Texture view '{viewSpec.Name}' references missing source texture '{viewSpec.SourceTextureName}'.");

        if (spec is FrameBufferSpec frameBufferSpec)
        {
            if (frameBufferSpec.Attachments.Count == 0)
                diagnostics.Add($"Framebuffer '{frameBufferSpec.Name}' has no attachments.");

            foreach (FrameBufferAttachmentDescriptor attachment in frameBufferSpec.Attachments)
            {
                if (!byName.ContainsKey(attachment.ResourceName))
                {
                    diagnostics.Add($"Framebuffer '{frameBufferSpec.Name}' references missing attachment resource '{attachment.ResourceName}'.");
                    continue;
                }
            }
        }
    }

    /// <summary>
    /// Topologically sorts the given list of render pipeline resource specifications based on their dependencies.
    /// </summary>
    /// <param name="specs">The list of render pipeline resource specifications to sort.</param>
    /// <param name="byName">A dictionary of resource specifications keyed by their names.</param>
    /// <returns>A list of render pipeline resource specifications sorted topologically based on their dependencies.</returns>
    private static List<RenderPipelineResourceSpec> TopologicallySort(
        IReadOnlyList<RenderPipelineResourceSpec> specs,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName)
    {
        Dictionary<string, int> visitState = new(StringComparer.OrdinalIgnoreCase);
        List<RenderPipelineResourceSpec> ordered = new(specs.Count);

        foreach (RenderPipelineResourceSpec spec in specs)
            Visit(spec, byName, visitState, ordered);

        return ordered;
    }

    /// <summary>
    /// Visits the given render pipeline resource specification and its dependencies in a depth-first manner,
    /// adding them to the ordered list in topological order. Throws an exception if a dependency cycle is detected.
    /// </summary>
    /// <param name="spec">The render pipeline resource specification to visit.</param>
    /// <param name="byName">A dictionary of resource specifications keyed by their names.</param>
    /// <param name="visitState">A dictionary tracking the visit state of each resource specification.</param>
    /// <param name="ordered">The list to which the visited resource specifications will be added in topological order.</param>
    /// <exception cref="InvalidOperationException">Thrown if a dependency cycle is detected.</exception>
    private static void Visit(
        RenderPipelineResourceSpec spec,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName,
        Dictionary<string, int> visitState,
        List<RenderPipelineResourceSpec> ordered)
    {
        if (visitState.TryGetValue(spec.Name, out int state))
        {
            if (state == 1)
                throw new InvalidOperationException($"Render pipeline resource layout has a dependency cycle at '{spec.Name}'.");
            return;
        }

        visitState[spec.Name] = 1;
        foreach (string dependency in spec.Dependencies)
            if (byName.TryGetValue(dependency, out RenderPipelineResourceSpec? dependencySpec))
                Visit(dependencySpec, byName, visitState, ordered);

        if (spec is TextureViewSpec viewSpec
            && byName.TryGetValue(viewSpec.SourceTextureName, out RenderPipelineResourceSpec? sourceSpec))
            Visit(sourceSpec, byName, visitState, ordered);

        if (spec is FrameBufferSpec frameBufferSpec)
            foreach (FrameBufferAttachmentDescriptor attachment in frameBufferSpec.Attachments)
                if (byName.TryGetValue(attachment.ResourceName, out RenderPipelineResourceSpec? attachmentSpec))
                    Visit(attachmentSpec, byName, visitState, ordered);

        visitState[spec.Name] = 2;
        ordered.Add(spec);
    }
}
