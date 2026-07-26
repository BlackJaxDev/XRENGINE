using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public static class ShaderAuthoringSchemaValidation
{
    private const int MaximumAnnotationLength = 32 * 1024;
    private const int MaximumReferences = 256;
    private const int MaximumEnumEntries = 1024;

    public static void ValidateNode(
        ShaderAuthoringNode node,
        IReadOnlyDictionary<string, ShaderAuthoringNode> properties,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (node.Options.ReferenceProperties.Count > MaximumReferences)
            AddError(node, issues, $"Reference count exceeds {MaximumReferences}.");
        foreach (ShaderAuthoringAttribute attribute in node.Attributes)
        {
            if ((attribute.Arguments?.Length ?? 0) > MaximumAnnotationLength)
            {
                AddError(node, issues, $"Annotation '{attribute.Name}' exceeds the size limit.");
                continue;
            }
            if (attribute.Name is "Enum" or "KeywordEnum" or "ThryWideEnum")
                ValidateEnum(node, attribute, issues);
            if (attribute.Name == "ButtonVector")
                ValidateButtonVector(node, attribute, issues);
        }

        if (node.WidgetId is { Length: > 0 } widget &&
            !ShaderAuthoringWidgetRegistry.TryResolve(widget, out _) &&
            widget is not ("ThryCustomGUI" or "ThryExternalTextureTool"))
            AddWarning(node, issues, $"Widget '{widget}' has no typed registration.");

        if (node.WidgetId is "ThryCustomGUI" or "ThryExternalTextureTool" &&
            !ShaderAuthoringWidgetRegistry.IsAllowlistedTool(node.WidgetId))
            AddWarning(node, issues, $"External widget/tool '{node.WidgetId}' is preserved inactive.");

        ValidatePath(node, node.Options.FileName, issues);
        ValidateUrl(node, node.Options.RemoteVersionUrl, "remote version URL", issues);
        ValidateActionDefinition(node, node.Options.ButtonHelp, properties, issues);
        ValidateActionDefinition(node, node.Options.ButtonAuthor, properties, issues);
        ValidateActionDefinition(node, node.Options.OnClick, properties, issues);
        ValidateActionDefinition(node, node.Options.AltClick, properties, issues);
        ValidateActionDefinition(node, node.Options.Actions, properties, issues);
        ValidateActionDefinition(node, node.Options.OnValueActions, properties, issues);

        if (node.Options.TextureWidth is <= 0 or > 16384)
            AddError(node, issues, "Texture option width must be between 1 and 16384.");
        if (node.Options.TextureHeight is <= 0 or > 16384)
            AddError(node, issues, "Texture option height must be between 1 and 16384.");
    }

    private static void ValidateEnum(
        ShaderAuthoringNode node,
        ShaderAuthoringAttribute attribute,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(attribute.Arguments))
        {
            AddError(node, issues, $"Widget '{attribute.Name}' requires label/value arguments.");
            return;
        }
        string[] tokens = attribute.Arguments.Split(',', StringSplitOptions.TrimEntries);
        int stride = attribute.Name == "KeywordEnum" ? 1 : 2;
        if (tokens.Length % stride != 0 || tokens.Length / stride > MaximumEnumEntries)
            AddError(node, issues, $"Widget '{attribute.Name}' has an invalid label/value count.");
        if (stride == 2)
        {
            for (int index = 1; index < tokens.Length; index += 2)
                if (!double.TryParse(
                        tokens[index],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _))
                    AddError(node, issues, $"Enum value '{tokens[index]}' is not numeric.");
        }
    }

    private static void ValidateButtonVector(
        ShaderAuthoringNode node,
        ShaderAuthoringAttribute attribute,
        ICollection<ShaderAuthoringIssue> issues)
    {
        int count = attribute.Arguments?.Split(',', StringSplitOptions.TrimEntries).Length ?? 0;
        if (count is < 1 or > 4)
            AddError(node, issues, "ButtonVector requires one to four component labels.");
    }

    private static void ValidatePath(
        ShaderAuthoringNode node,
        string? path,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (Path.IsPathRooted(path) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static part => part == ".."))
            AddError(node, issues, "Imported output file name must be a relative path without traversal.");
    }

    private static void ValidateUrl(
        ShaderAuthoringNode node,
        string? rawUrl,
        string purpose,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            AddError(node, issues, $"The {purpose} is not an absolute HTTPS URL.");
    }

    private static void ValidateActionDefinition(
        ShaderAuthoringNode node,
        string? definition,
        IReadOnlyDictionary<string, ShaderAuthoringNode> properties,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return;
        MaterialAuthoringActionGraph graph = MaterialAuthoringActionGraph.Parse(definition);
        foreach (string diagnostic in graph.Diagnostics)
            AddError(node, issues, diagnostic);
        foreach (MaterialAuthoringAction action in graph.Actions)
        {
            switch (action.Kind)
            {
                case EMaterialAuthoringActionKind.Url:
                    ValidateUrl(node, action.Target, "action URL", issues);
                    break;
                case EMaterialAuthoringActionKind.OpenEditor:
                    if (!MaterialAuthoringCommandRegistry.RegisteredCommandIds.Contains(
                            action.Target,
                            StringComparer.Ordinal))
                        AddWarning(node, issues, $"Editor command '{action.Target}' is not allowlisted.");
                    break;
                case EMaterialAuthoringActionKind.SetProperty:
                    if (!IsRenderState(action.Target) && !properties.ContainsKey(action.Target))
                        AddError(node, issues, $"Action target '{action.Target}' does not exist.");
                    break;
            }
        }
    }

    private static bool IsRenderState(string target)
        => MaterialRenderStateActionAdapter.IsSupported(target) ||
           target.Equals("render_queue", StringComparison.OrdinalIgnoreCase) ||
           target.Equals("renderQueue", StringComparison.OrdinalIgnoreCase) ||
           target.Equals("render_type", StringComparison.OrdinalIgnoreCase);

    private static void AddError(
        ShaderAuthoringNode node,
        ICollection<ShaderAuthoringIssue> issues,
        string message)
        => issues.Add(new(
            EShaderAuthoringIssueSeverity.Error,
            message,
            node.SemanticId,
            node.SourceLine));

    private static void AddWarning(
        ShaderAuthoringNode node,
        ICollection<ShaderAuthoringIssue> issues,
        string message)
        => issues.Add(new(
            EShaderAuthoringIssueSeverity.Warning,
            message,
            node.SemanticId,
            node.SourceLine));
}
