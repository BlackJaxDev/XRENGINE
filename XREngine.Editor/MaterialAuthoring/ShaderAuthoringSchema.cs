using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public enum EShaderAuthoringNodeKind
{
    Root,
    Section,
    Subsection,
    Property,
    Decorator,
    Action,
    ToolLauncher,
    Diagnostic,
}

public enum EShaderAuthoringIssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ShaderAuthoringIssue(
    EShaderAuthoringIssueSeverity Severity,
    string Message,
    string? SemanticId,
    int SourceLine);

public sealed record ShaderAuthoringAttribute(string Name, string? Arguments);

/// <summary>
/// Safe, declarative subset of Thry <c>PropertyOptions</c>. Unknown fields are
/// retained in <see cref="Unclassified"/> and never executed.
/// </summary>
public sealed class ShaderAuthoringOptions
{
    public float? Offset { get; init; }
    public string? Tooltip { get; init; }
    public string? AltClick { get; init; }
    public string? OnClick { get; init; }
    public string? ConditionShow { get; init; }
    public string? ConditionEnable { get; init; }
    public string? ConditionEnableChildren { get; init; }
    public string? OnValue { get; init; }
    public string? Actions { get; init; }
    public string? OnValueActions { get; init; }
    public string? ButtonHelp { get; init; }
    public string? ButtonAuthor { get; init; }
    public string? Texture { get; init; }
    public string? ReferenceProperty { get; init; }
    public IReadOnlyList<string> ReferenceProperties { get; init; } = [];
    public string? FpsProperty { get; init; }
    public bool ForceTextureOptions { get; init; }
    public bool IsVisibleSimple { get; init; } = true;
    public string? FileName { get; init; }
    public string? RemoteVersionUrl { get; init; }
    public string? GenericString { get; init; }
    public bool NeverLock { get; init; }
    public float MarginTop { get; init; }
    public IReadOnlyList<string> AlternativeLabels { get; init; } = [];
    public bool PersistentExpand { get; init; }
    public bool DefaultExpand { get; init; }
    public bool ReferenceFloatTogglesExpand { get; init; }
    public bool DrawBorder { get; init; }
    public string? TextureFilterMode { get; init; }
    public string? TextureWrapMode { get; init; }
    public int? TextureWidth { get; init; }
    public int? TextureHeight { get; init; }
    public IReadOnlyDictionary<string, string> Unclassified { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class ShaderAuthoringNode
{
    public required string SemanticId { get; init; }
    public required EShaderAuthoringNodeKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? SourcePropertyName { get; init; }
    public string? LocalizationKey { get; init; }
    public string? SourceType { get; init; }
    public string? DefaultValue { get; init; }
    public string? WidgetId { get; init; }
    public string? Classification { get; init; }
    public int SourceLine { get; init; }
    public int DeclarationOrder { get; init; }
    public ShaderUiProperty? ManifestProperty { get; init; }
    public ShaderAuthoringOptions Options { get; init; } = new();
    public IReadOnlyList<ShaderAuthoringAttribute> Attributes { get; init; } = [];
    public ShaderAuthoringExpression? VisibilityExpression { get; init; }
    public ShaderAuthoringExpression? EnableExpression { get; init; }
    public ShaderAuthoringExpression? EnableChildrenExpression { get; init; }
    public ShaderAuthoringNode? Parent { get; internal set; }
    public List<ShaderAuthoringNode> Children { get; } = [];
    public List<ShaderAuthoringNode> ReferencedProperties { get; } = [];
    public List<MaterialAuthoringAction> Actions { get; } = [];

    public IEnumerable<string> ConditionDependencies
    {
        get
        {
            if (VisibilityExpression is not null)
                foreach (string dependency in VisibilityExpression.Dependencies)
                    yield return dependency;
            if (EnableExpression is not null)
                foreach (string dependency in EnableExpression.Dependencies)
                    yield return dependency;
            if (EnableChildrenExpression is not null)
                foreach (string dependency in EnableChildrenExpression.Dependencies)
                    yield return dependency;
        }
    }

    public bool IsHiddenBuiltIn => Attributes.Any(static attribute =>
        attribute.Name == "HideInInspector");
    public bool IsNonModifiableTexture => Attributes.Any(static attribute =>
        attribute.Name == "NonModifiableTextureData");
    public bool IsSupported => ManifestProperty is not null ||
        Kind is not EShaderAuthoringNodeKind.Property;

    public IEnumerable<ShaderAuthoringNode> Ancestors()
    {
        for (ShaderAuthoringNode? current = Parent; current is not null; current = current.Parent)
            yield return current;
    }
}

public sealed class ShaderAuthoringSchema
{
    public ShaderAuthoringSchema(
        string schemaId,
        int version,
        string sourceIdentity,
        string fingerprint,
        ShaderAuthoringNode root,
        IReadOnlyList<ShaderAuthoringIssue> issues)
    {
        SchemaId = schemaId;
        Version = version;
        SourceIdentity = sourceIdentity;
        Fingerprint = fingerprint;
        Root = root;
        Dictionary<string, ShaderAuthoringNode> nodes = new(StringComparer.Ordinal);
        Dictionary<string, ShaderAuthoringNode> properties = new(StringComparer.Ordinal);
        List<ShaderAuthoringNode> declarationOrder = [];
        Index(root, nodes, properties, declarationOrder);
        NodeLookup = nodes;
        PropertyLookup = properties;
        DeclarationOrder = declarationOrder;
        List<ShaderAuthoringIssue> validatedIssues = [.. issues];
        ResolveGraphEdges(properties, declarationOrder, validatedIssues);
        Issues = validatedIssues;
        DependencyIndex = BuildDependencyIndex(declarationOrder);
        ValidateConditionCycles(declarationOrder, properties, validatedIssues);
    }

    public string SchemaId { get; }
    public int Version { get; }
    public string SourceIdentity { get; }
    public string Fingerprint { get; }
    public ShaderAuthoringNode Root { get; }
    public IReadOnlyList<ShaderAuthoringIssue> Issues { get; }
    public IReadOnlyDictionary<string, ShaderAuthoringNode> NodeLookup { get; }
    public IReadOnlyDictionary<string, ShaderAuthoringNode> PropertyLookup { get; }
    public IReadOnlyList<ShaderAuthoringNode> DeclarationOrder { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ShaderAuthoringNode>> DependencyIndex { get; }

    public IReadOnlyList<ShaderAuthoringNode> GetAffectedNodes(string sourcePropertyName)
        => DependencyIndex.TryGetValue(sourcePropertyName, out IReadOnlyList<ShaderAuthoringNode>? nodes)
            ? nodes
            : [];

    private static void ResolveGraphEdges(
        IReadOnlyDictionary<string, ShaderAuthoringNode> properties,
        IReadOnlyList<ShaderAuthoringNode> nodes,
        ICollection<ShaderAuthoringIssue> issues)
    {
        foreach (ShaderAuthoringNode node in nodes)
        {
            if (node.Options.ReferenceProperty is { Length: > 0 } single)
                ResolveReference(node, single, properties, issues);
            foreach (string reference in node.Options.ReferenceProperties)
                ResolveReference(node, reference, properties, issues);

            MaterialAuthoringActionGraph graph = MaterialAuthoringActionGraph.Parse(
                node.Options.OnValueActions ?? node.Options.OnClick ?? node.Options.Actions);
            node.Actions.AddRange(graph.Actions);
            foreach (string diagnostic in graph.Diagnostics)
                issues.Add(new(
                    EShaderAuthoringIssueSeverity.Error,
                    diagnostic,
                    node.SemanticId,
                    node.SourceLine));

            ShaderAuthoringSchemaValidation.ValidateNode(node, properties, issues);
            foreach (string dependency in node.ConditionDependencies.Distinct(StringComparer.Ordinal))
            {
                if (properties.ContainsKey(dependency) || IsEngineOperand(dependency))
                    continue;
                issues.Add(new(
                    EShaderAuthoringIssueSeverity.Warning,
                    $"Condition operand '{dependency}' is unknown and evaluates false.",
                    node.SemanticId,
                    node.SourceLine));
            }
        }
    }

    private static void ResolveReference(
        ShaderAuthoringNode node,
        string reference,
        IReadOnlyDictionary<string, ShaderAuthoringNode> properties,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (properties.TryGetValue(reference, out ShaderAuthoringNode? target))
        {
            if (!node.ReferencedProperties.Contains(target, ReferenceEqualityComparer.Instance))
                node.ReferencedProperties.Add(target);
            return;
        }
        issues.Add(new(
            EShaderAuthoringIssueSeverity.Error,
            $"Referenced property '{reference}' was not found.",
            node.SemanticId,
            node.SourceLine));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ShaderAuthoringNode>> BuildDependencyIndex(
        IReadOnlyList<ShaderAuthoringNode> nodes)
    {
        Dictionary<string, List<ShaderAuthoringNode>> mutable = new(StringComparer.Ordinal);
        foreach (ShaderAuthoringNode node in nodes)
        {
            foreach (string dependency in node.ConditionDependencies.Distinct(StringComparer.Ordinal))
            {
                if (!mutable.TryGetValue(dependency, out List<ShaderAuthoringNode>? dependents))
                    mutable[dependency] = dependents = [];
                dependents.Add(node);
            }
        }
        return mutable.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<ShaderAuthoringNode>)pair.Value,
            StringComparer.Ordinal);
    }

    private static void ValidateConditionCycles(
        IReadOnlyList<ShaderAuthoringNode> nodes,
        IReadOnlyDictionary<string, ShaderAuthoringNode> properties,
        ICollection<ShaderAuthoringIssue> issues)
    {
        HashSet<ShaderAuthoringNode> visiting = new(ReferenceEqualityComparer.Instance);
        HashSet<ShaderAuthoringNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (ShaderAuthoringNode node in nodes)
            Visit(node, properties, visiting, visited, issues);
    }

    private static void Visit(
        ShaderAuthoringNode node,
        IReadOnlyDictionary<string, ShaderAuthoringNode> properties,
        ISet<ShaderAuthoringNode> visiting,
        ISet<ShaderAuthoringNode> visited,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (visited.Contains(node))
            return;
        if (!visiting.Add(node))
        {
            issues.Add(new(
                EShaderAuthoringIssueSeverity.Error,
                "Condition dependency cycle detected.",
                node.SemanticId,
                node.SourceLine));
            return;
        }
        foreach (string dependency in node.ConditionDependencies)
            if (properties.TryGetValue(dependency, out ShaderAuthoringNode? target))
                Visit(target, properties, visiting, visited, issues);
        visiting.Remove(node);
        visited.Add(node);
    }

    private static bool IsEngineOperand(string operand)
        => operand.Equals("render_queue", StringComparison.OrdinalIgnoreCase) ||
           operand.Equals("renderQueue", StringComparison.OrdinalIgnoreCase) ||
           operand.StartsWith("animated:", StringComparison.OrdinalIgnoreCase) ||
           operand.StartsWith("static:", StringComparison.OrdinalIgnoreCase) ||
           operand.StartsWith("texture:", StringComparison.OrdinalIgnoreCase) ||
           operand.StartsWith("texture_name:", StringComparison.OrdinalIgnoreCase) ||
           operand.StartsWith("cap:", StringComparison.OrdinalIgnoreCase) ||
           operand.StartsWith("version:", StringComparison.OrdinalIgnoreCase);

    private static void Index(
        ShaderAuthoringNode node,
        IDictionary<string, ShaderAuthoringNode> nodes,
        IDictionary<string, ShaderAuthoringNode> properties,
        ICollection<ShaderAuthoringNode> declarationOrder)
    {
        nodes[node.SemanticId] = node;
        if (node.SourcePropertyName is { Length: > 0 } sourceName)
            properties[sourceName] = node;
        if (node.Kind != EShaderAuthoringNodeKind.Root)
            declarationOrder.Add(node);

        foreach (ShaderAuthoringNode child in node.Children)
            Index(child, nodes, properties, declarationOrder);
    }
}
