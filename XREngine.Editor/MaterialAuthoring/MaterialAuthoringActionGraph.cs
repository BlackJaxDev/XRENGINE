using System.Globalization;
using System.Text;
using XREngine.Rendering;
using XREngine.Data.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Parsed, typed representation of a Thry action list. The parser accepts the
/// compact object notation emitted by the pinned Poiyomi catalog without
/// treating it as executable code.
/// </summary>
public sealed class MaterialAuthoringActionGraph
{
    public required IReadOnlyList<MaterialAuthoringAction> Actions { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public bool IsValid => Diagnostics.Count == 0;

    public static MaterialAuthoringActionGraph Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new() { Actions = [] };

        List<MaterialAuthoringAction> actions = [];
        List<string> diagnostics = [];
        ActionObjectScanner scanner = new(source);
        foreach (IReadOnlyDictionary<string, string> fields in scanner.Scan())
        {
            if (!fields.TryGetValue("type", out string? type))
                continue;

            string normalized = type.Replace("_", string.Empty, StringComparison.Ordinal);
            if (!Enum.TryParse(normalized, true, out EMaterialAuthoringActionKind kind))
            {
                diagnostics.Add($"Unknown action kind '{type}'.");
                continue;
            }

            fields.TryGetValue("data", out string? data);
            fields.TryGetValue("value", out string? explicitValue);
            SplitPayload(kind, data, explicitValue, out string target, out string? value);
            if (string.IsNullOrWhiteSpace(target))
            {
                diagnostics.Add($"Action '{type}' has no target.");
                continue;
            }

            actions.Add(new(kind, target, value));
        }

        if (actions.Count == 0)
        {
            foreach (MaterialAuthoringAction action in MaterialAuthoringActionParser.Parse(source))
                actions.Add(action);
        }

        return new() { Actions = actions, Diagnostics = diagnostics };
    }

    public static MaterialAuthoringActionGraph ParseForValue(string? source, string? activeValue)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new() { Actions = [] };
        string normalized = activeValue?.Trim() ?? string.Empty;
        foreach (IReadOnlyDictionary<string, string> fields in new ActionObjectScanner(source).Scan())
        {
            if (!fields.TryGetValue("value", out string? candidate) ||
                !fields.TryGetValue("actions", out string? nested) ||
                !string.Equals(Unquote(candidate.Trim()), normalized, StringComparison.OrdinalIgnoreCase))
                continue;
            return Parse(nested);
        }
        return new() { Actions = [] };
    }

    private static void SplitPayload(
        EMaterialAuthoringActionKind kind,
        string? data,
        string? explicitValue,
        out string target,
        out string? value)
    {
        string payload = Unquote(data?.Trim() ?? string.Empty);
        value = explicitValue is null ? null : Unquote(explicitValue.Trim());
        if (kind is EMaterialAuthoringActionKind.Url or EMaterialAuthoringActionKind.OpenEditor or
            EMaterialAuthoringActionKind.SetShader)
        {
            target = payload;
            return;
        }

        int separator = FindAssignment(payload);
        target = separator < 0 ? payload : payload[..separator].Trim();
        value ??= separator < 0 ? null : Unquote(payload[(separator + 1)..].Trim());
    }

    private static int FindAssignment(string payload)
    {
        int equals = payload.IndexOf('=');
        if (equals >= 0)
            return equals;
        int comma = payload.IndexOf(',');
        return comma;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private sealed class ActionObjectScanner(string source)
    {
        private const int MaxDepth = 32;
        private const int MaxObjects = 4096;
        private const int MaxFieldLength = 16 * 1024;

        public IEnumerable<IReadOnlyDictionary<string, string>> Scan()
        {
            int objects = 0;
            for (int index = 0; index < source.Length;)
            {
                if (source[index] != '{')
                {
                    index++;
                    continue;
                }

                int end = FindObjectEnd(index);
                if (end < 0)
                    yield break;
                if (++objects > MaxObjects)
                    yield break;

                Dictionary<string, string> fields = ParseFields(source.AsSpan(index + 1, end - index - 1));
                if (fields.ContainsKey("type") ||
                    (fields.ContainsKey("value") && fields.ContainsKey("actions")))
                    yield return fields;
                index++;
            }
        }

        private int FindObjectEnd(int start)
        {
            int depth = 0;
            char quote = '\0';
            bool escaped = false;
            for (int index = start; index < source.Length; index++)
            {
                char current = source[index];
                if (quote != '\0')
                {
                    if (escaped)
                        escaped = false;
                    else if (current == '\\')
                        escaped = true;
                    else if (current == quote)
                        quote = '\0';
                    continue;
                }

                if (current is '"' or '\'')
                {
                    quote = current;
                    continue;
                }
                if (current == '{' && ++depth > MaxDepth)
                    return -1;
                if (current == '}' && --depth == 0)
                    return index;
            }
            return -1;
        }

        private static Dictionary<string, string> ParseFields(ReadOnlySpan<char> objectBody)
        {
            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            while (index < objectBody.Length)
            {
                SkipDelimiters(objectBody, ref index);
                int colon = FindTopLevel(objectBody, index, ':');
                if (colon < 0)
                    break;
                string key = objectBody[index..colon].Trim().ToString().Trim('"', '\'');
                index = colon + 1;
                int comma = FindTopLevel(objectBody, index, ',');
                int end = comma < 0 ? objectBody.Length : comma;
                if (key.Length > 0 && end - index <= MaxFieldLength)
                    fields[key] = objectBody[index..end].Trim().ToString();
                index = comma < 0 ? objectBody.Length : comma + 1;
            }
            return fields;
        }

        private static void SkipDelimiters(ReadOnlySpan<char> value, ref int index)
        {
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || value[index] == ','))
                index++;
        }

        private static int FindTopLevel(ReadOnlySpan<char> value, int start, char target)
        {
            int objectDepth = 0;
            int arrayDepth = 0;
            char quote = '\0';
            bool escaped = false;
            for (int index = start; index < value.Length; index++)
            {
                char current = value[index];
                if (quote != '\0')
                {
                    if (escaped)
                        escaped = false;
                    else if (current == '\\')
                        escaped = true;
                    else if (current == quote)
                        quote = '\0';
                    continue;
                }
                if (current is '"' or '\'')
                {
                    quote = current;
                    continue;
                }
                switch (current)
                {
                    case '{':
                        objectDepth++;
                        break;
                    case '}':
                        objectDepth--;
                        break;
                    case '[':
                        arrayDepth++;
                        break;
                    case ']':
                        arrayDepth--;
                        break;
                    default:
                        if (current == target && objectDepth == 0 && arrayDepth == 0)
                            return index;
                        break;
                }
            }
            return -1;
        }
    }
}

/// <summary>
/// Allowlisted mutation surface used by the action executor. Property and
/// shader conversion remain owned by the inspector/converter, not metadata.
/// </summary>
public sealed class MaterialAuthoringActionContext
{
    public required XRMaterial Material { get; init; }
    public required ShaderAuthoringNode Node { get; init; }
    public required Func<string, string?, string?> ValidateProperty { get; init; }
    public required Action<string, string?> SetProperty { get; init; }
    public Func<string, string?>? ValidateShader { get; init; }
    public Action<string>? SetShader { get; init; }
    public Func<string, string?>? ValidateTag { get; init; }
    public Action<string, string?>? SetTag { get; init; }
}

public sealed record MaterialAuthoringActionPreview(
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<string> Diagnostics)
{
    public bool CanExecute => Diagnostics.Count == 0;
}

/// <summary>
/// Preflights and executes imported action graphs as one material transaction.
/// </summary>
public static class MaterialAuthoringActionExecutor
{
    public static MaterialAuthoringActionPreview Preview(
        MaterialAuthoringActionGraph graph,
        MaterialAuthoringActionContext context)
    {
        List<string> effects = [];
        List<string> diagnostics = [.. graph.Diagnostics];
        foreach (MaterialAuthoringAction action in graph.Actions)
        {
            string? diagnostic = Validate(action, context);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
            else
                effects.Add(Describe(action));
        }
        return new(effects, diagnostics);
    }

    public static bool TryExecute(
        MaterialAuthoringActionGraph graph,
        MaterialAuthoringActionContext context,
        out MaterialAuthoringTransactionReport report)
    {
        MaterialAuthoringActionPreview preview = Preview(graph, context);
        if (!preview.CanExecute)
        {
            report = new(false, 0, preview.Diagnostics);
            return false;
        }

        MaterialAuthoringTransaction transaction = new($"Run {context.Node.DisplayName}");
        foreach (MaterialAuthoringAction action in graph.Actions)
        {
            MaterialAuthoringAction captured = action;
            bool invalidatesVariant = captured.Kind is
                EMaterialAuthoringActionKind.SetProperty or
                EMaterialAuthoringActionKind.SetShader or
                EMaterialAuthoringActionKind.SetRenderState;
            Action? undo = CaptureStructuralUndo(captured, context);
            if (undo is null)
            {
                transaction.Add(
                    context.Material,
                    Describe(captured),
                    () => Validate(captured, context),
                    () => Apply(captured, context),
                    invalidatesVariant);
            }
            else
            {
                transaction.AddStructural(
                    context.Material,
                    Describe(captured),
                    () => Validate(captured, context),
                    () => Apply(captured, context),
                    undo,
                    invalidatesVariant);
            }
        }
        return transaction.TryExecute(out report);
    }

    private static Action? CaptureStructuralUndo(
        MaterialAuthoringAction action,
        MaterialAuthoringActionContext context)
    {
        if (action.Kind is EMaterialAuthoringActionKind.SetProperty or EMaterialAuthoringActionKind.SetRenderState &&
            MaterialRenderStateActionAdapter.IsSupported(action.Target))
            return MaterialRenderStateActionAdapter.CaptureUndo(context.Material, action.Target);

        MaterialAuthoringMetadata metadata =
            MaterialAuthoringMetadataStore.Instance.Get(context.Material);
        if (action.Kind == EMaterialAuthoringActionKind.SetTag ||
            (action.Kind is EMaterialAuthoringActionKind.SetProperty or EMaterialAuthoringActionKind.SetRenderState &&
             action.Target.Equals("render_type", StringComparison.OrdinalIgnoreCase)))
        {
            string tagName = action.Target.Equals("render_type", StringComparison.OrdinalIgnoreCase)
                ? "RenderType"
                : action.Target;
            bool hadValue = metadata.Tags.TryGetValue(tagName, out string? previous);
            return () =>
            {
                if (hadValue)
                    metadata.Tags[tagName] = previous!;
                else
                    metadata.Tags.Remove(tagName);
            };
        }
        if (action.Kind is EMaterialAuthoringActionKind.SetProperty or EMaterialAuthoringActionKind.SetRenderState &&
            (action.Target.Equals("render_queue", StringComparison.OrdinalIgnoreCase) ||
             action.Target.Equals("renderQueue", StringComparison.OrdinalIgnoreCase)))
        {
            int? previous = metadata.ImportedRenderQueue;
            return () => metadata.ImportedRenderQueue = previous;
        }
        if (action.Kind == EMaterialAuthoringActionKind.SetShader)
        {
            string? previous = metadata.ImportedShaderIdentity;
            return () => metadata.ImportedShaderIdentity = previous;
        }
        return null;
    }
    private static string? Validate(
        MaterialAuthoringAction action,
        MaterialAuthoringActionContext context)
        => action.Kind switch
        {
            EMaterialAuthoringActionKind.SetProperty =>
                ValidatePropertyOrRenderState(action, context),
            EMaterialAuthoringActionKind.SetRenderState =>
                ValidateRenderState(action.Target, action.Value),
            EMaterialAuthoringActionKind.SetTag =>
                context.ValidateTag?.Invoke(action.Target) ??
                (context.SetTag is null ? "No native tag mapping is registered." : null),
            EMaterialAuthoringActionKind.SetShader =>
                context.ValidateShader?.Invoke(action.Target) ??
                (context.SetShader is null ? "No semantic shader converter is registered." : null),
            EMaterialAuthoringActionKind.Url =>
                Uri.TryCreate(action.Target, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "Only absolute HTTPS action links are allowed.",
            EMaterialAuthoringActionKind.OpenEditor =>
                MaterialAuthoringCommandRegistry.RegisteredCommandIds.Contains(action.Target, StringComparer.Ordinal)
                    ? null
                    : $"Editor command '{action.Target}' is not allowlisted.",
            _ => $"Action kind '{action.Kind}' is not supported.",
        };

    private static string? ValidatePropertyOrRenderState(
        MaterialAuthoringAction action,
        MaterialAuthoringActionContext context)
    {
        if (IsRenderState(action.Target))
            return ValidateRenderState(action.Target, action.Value);
        return context.ValidateProperty(action.Target, action.Value);
    }

    private static string? ValidateRenderState(string target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"Render state '{target}' has no value.";
        if (MaterialRenderStateActionAdapter.IsSupported(target))
            return MaterialRenderStateActionAdapter.Validate(target, value);
        if (target.Equals("render_type", StringComparison.OrdinalIgnoreCase))
            return value is "Opaque" or "Transparent" or "TransparentCutout"
                ? null
                : $"Render type '{value}' is not recognized.";
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? null
            : $"Render state '{target}' requires an integer value.";
    }

    private static void Apply(
        MaterialAuthoringAction action,
        MaterialAuthoringActionContext context)
    {
        switch (action.Kind)
        {
            case EMaterialAuthoringActionKind.SetProperty:
                if (IsRenderState(action.Target))
                    ApplyRenderState(context.Material, action.Target, action.Value);
                else
                    context.SetProperty(action.Target, action.Value);
                break;
            case EMaterialAuthoringActionKind.SetRenderState:
                ApplyRenderState(context.Material, action.Target, action.Value);
                break;
            case EMaterialAuthoringActionKind.SetTag:
                context.SetTag!(action.Target, action.Value);
                break;
            case EMaterialAuthoringActionKind.SetShader:
                context.SetShader!(action.Target);
                break;
            case EMaterialAuthoringActionKind.Url:
                if (!MaterialAuthoringCommandRegistry.RequestSafeLink(action.Target, out string? linkDiagnostic))
                    throw new InvalidOperationException(linkDiagnostic);
                break;
            case EMaterialAuthoringActionKind.OpenEditor:
                if (!MaterialAuthoringCommandRegistry.TryExecute(
                        action.Target,
                        context.Material,
                        context.Node,
                        out string? commandDiagnostic))
                    throw new InvalidOperationException(commandDiagnostic);
                break;
        }
    }

    private static bool IsRenderState(string target)
        => MaterialRenderStateActionAdapter.IsSupported(target) ||
           target.Equals("render_queue", StringComparison.OrdinalIgnoreCase) ||
           target.Equals("renderQueue", StringComparison.OrdinalIgnoreCase) ||
           target.Equals("render_type", StringComparison.OrdinalIgnoreCase);

    private static void ApplyRenderState(XRMaterial material, string target, string? value)
    {
        if (MaterialRenderStateActionAdapter.IsSupported(target))
        {
            MaterialRenderStateActionAdapter.Apply(material, target, value);
            return;
        }
        if (target.Equals("render_type", StringComparison.OrdinalIgnoreCase))
        {
            MaterialAuthoringMetadataStore.Instance.SetTag(material, "RenderType", value);
            return;
        }

        material.RenderPass = TranslateUnityQueue(int.Parse(value!, CultureInfo.InvariantCulture));
        MaterialAuthoringMetadataStore.Instance.SetImportedRenderQueue(material, int.Parse(value!, CultureInfo.InvariantCulture));
    }

    public static int TranslateUnityQueue(int queue)
        => queue switch
        {
            < 2450 => (int)EDefaultRenderPass.OpaqueForward,
            < 2501 => (int)EDefaultRenderPass.MaskedForward,
            _ => (int)EDefaultRenderPass.TransparentForward,
        };

    private static string Describe(MaterialAuthoringAction action)
        => action.Kind switch
        {
            EMaterialAuthoringActionKind.Url => $"Open HTTPS link {action.Target}",
            EMaterialAuthoringActionKind.OpenEditor => $"Open editor tool {action.Target}",
            EMaterialAuthoringActionKind.SetShader => $"Convert shader to {action.Target}",
            _ => $"{action.Kind} {action.Target} = {action.Value}",
        };
}
