using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering
{
    public enum EShaderUiPropertyMode
    {
        Unspecified,
        Static,
        Animated,
    }

    public enum EShaderUiFeatureCost
    {
        Unspecified,
        None,
        Low,
        Medium,
        High,
    }

    public enum EShaderUiValidationSeverity
    {
        Info,
        Warning,
        Error,
    }

    public sealed class ShaderUiManifest
    {
        public static ShaderUiManifest Empty { get; } = new([], [], []);

        public ShaderUiManifest(
            IReadOnlyList<ShaderUiFeature> features,
            IReadOnlyList<ShaderUiProperty> properties,
            IReadOnlyList<ShaderUiValidationIssue> validationIssues)
        {
            Features = features;
            Properties = properties;
            ValidationIssues = validationIssues;

            var featuresById = new Dictionary<string, ShaderUiFeature>(StringComparer.Ordinal);
            foreach (ShaderUiFeature feature in features)
                featuresById[feature.Id] = feature;
            FeatureLookup = new ReadOnlyDictionary<string, ShaderUiFeature>(featuresById);

            var propertiesByName = new Dictionary<string, ShaderUiProperty>(StringComparer.Ordinal);
            foreach (ShaderUiProperty property in properties)
                propertiesByName[property.Name] = property;
            PropertyLookup = new ReadOnlyDictionary<string, ShaderUiProperty>(propertiesByName);
        }

        public IReadOnlyList<ShaderUiFeature> Features { get; }
        public IReadOnlyList<ShaderUiProperty> Properties { get; }
        public IReadOnlyList<ShaderUiValidationIssue> ValidationIssues { get; }
        public IReadOnlyDictionary<string, ShaderUiFeature> FeatureLookup { get; }
        public IReadOnlyDictionary<string, ShaderUiProperty> PropertyLookup { get; }
    }

    public sealed record ShaderUiFeature(
        string Id,
        string DisplayName,
        string? Category,
        string? Subcategory,
        string? Tooltip,
        string? GuardMacro,
        bool GuardDefinedEnablesFeature,
        bool DefaultEnabled,
        bool Required,
        EShaderUiFeatureCost Cost,
        bool HasExplicitMetadata,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<string> Conflicts);

    public sealed record ShaderUiProperty(
        string Name,
        string GlslType,
        int ArraySize,
        bool IsSampler,
        string DisplayName,
        string? Tooltip,
        string? Category,
        string? Subcategory,
        string? FeatureId,
        string? Slot,
        string? Range,
        string? EnumOptions,
        bool IsToggle,
        EShaderUiPropertyMode DefaultMode,
        bool HasExplicitMetadata,
        int SourceLine,
        string? DefaultLiteral = null);

    public sealed record ShaderUiValidationIssue(
        EShaderUiValidationSeverity Severity,
        string Message,
        int LineNumber);

    public static partial class ShaderUiManifestParser
    {
        public static ShaderUiManifest Parse(string? source, string? sourcePath = null)
        {
            if (string.IsNullOrWhiteSpace(source))
                return ShaderUiManifest.Empty;

            HashSet<string> definedMacros = CollectDefinedMacros(source);
            Dictionary<string, FeatureBuilder> featuresById = new(StringComparer.Ordinal);
            List<ShaderUiProperty> properties = [];
            List<ShaderUiValidationIssue> validation = [];

            ParseState state = new();
            Stack<GuardScope> guardStack = [];

            string[] lines = NormalizeNewlines(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i];
                string trimmed = line.Trim();

                if (TryParseDirective(trimmed, state, validation, lineNumber))
                    continue;

                if (TryEnterGuard(trimmed, definedMacros, state, guardStack, featuresById, validation, lineNumber))
                    continue;

                if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
                {
                    if (guardStack.Count > 0)
                        guardStack.Pop();
                    continue;
                }

                Match uniformMatch = UniformRegex().Match(line);
                if (!uniformMatch.Success)
                    continue;

                string glslType = uniformMatch.Groups["type"].Value;
                string uniformName = uniformMatch.Groups["name"].Value;
                int arraySize = ParseArraySize(uniformMatch.Groups["array"].Value);
                bool isSampler = glslType.StartsWith("sampler", StringComparison.OrdinalIgnoreCase);
                string? inlineCommentTooltip = ExtractInlineComment(line);

                PendingPropertyAnnotation? propertyAnnotation = state.PendingProperty;
                bool hasExplicitPropertyMetadata = propertyAnnotation is not null;

                if (propertyAnnotation?.Name is { Length: > 0 } annotatedName &&
                    !string.Equals(annotatedName, uniformName, StringComparison.Ordinal))
                {
                    validation.Add(new(
                        EShaderUiValidationSeverity.Error,
                        $"Annotated property '{annotatedName}' does not match following uniform '{uniformName}'.",
                        lineNumber));
                }

                string? featureId = propertyAnnotation?.FeatureId ?? ResolveFeatureId(guardStack);
                FeatureBuilder? feature = featureId is not null && featuresById.TryGetValue(featureId, out FeatureBuilder? existingFeature)
                    ? existingFeature
                    : null;

                string? category = propertyAnnotation?.Category ?? feature?.Category ?? state.CurrentCategory;
                string? subcategory = propertyAnnotation?.Subcategory ?? feature?.Subcategory ?? state.CurrentSubcategory;
                string displayName = propertyAnnotation?.DisplayName ?? FormatDisplayName(uniformName);
                string? tooltip = BuildTooltip(propertyAnnotation?.Tooltip, inlineCommentTooltip);
                string? slot = propertyAnnotation?.Slot ?? (isSampler ? "texture" : null);
                string? range = propertyAnnotation?.Range;
                string? enumOptions = propertyAnnotation?.EnumOptions;
                EShaderUiPropertyMode mode = propertyAnnotation?.Mode
                    ?? (isSampler ? EShaderUiPropertyMode.Unspecified : EShaderUiPropertyMode.Static);

                properties.Add(new ShaderUiProperty(
                    uniformName,
                    glslType,
                    arraySize,
                    isSampler,
                    displayName,
                    tooltip,
                    category,
                    subcategory,
                    featureId,
                    slot,
                    range,
                    enumOptions,
                    propertyAnnotation?.IsToggle ?? false,
                    mode,
                    hasExplicitPropertyMetadata,
                    lineNumber,
                    propertyAnnotation?.DefaultLiteral));

                state.PendingProperty = null;
                state.PendingTooltip = null;
            }

            FinalizePendingAnnotations(state, validation, lines.Length);
            ValidateFeatureGraph(featuresById, validation);
            ValidateUberCoverage(sourcePath, featuresById, properties, validation);

            ShaderUiFeature[] features = featuresById.Values
                .OrderBy(static x => x.SortOrder)
                .ThenBy(static x => x.DisplayName, StringComparer.Ordinal)
                .Select(static x => x.Build())
                .ToArray();

            return new ShaderUiManifest(features, properties, validation);
        }

        private static void FinalizePendingAnnotations(ParseState state, List<ShaderUiValidationIssue> validation, int lineCount)
        {
            if (state.PendingFeature is not null)
            {
                validation.Add(new(
                    EShaderUiValidationSeverity.Error,
                    $"Feature annotation '{state.PendingFeature.Id ?? state.PendingFeature.DisplayName ?? "<unnamed>"}' was not followed by a guard block.",
                    lineCount));
            }

            if (state.PendingProperty is not null)
            {
                validation.Add(new(
                    EShaderUiValidationSeverity.Error,
                    $"Property annotation '{state.PendingProperty.Name ?? state.PendingProperty.DisplayName ?? "<unnamed>"}' was not followed by a uniform declaration.",
                    lineCount));
            }
        }

        private static void ValidateFeatureGraph(Dictionary<string, FeatureBuilder> featuresById, List<ShaderUiValidationIssue> validation)
        {
            foreach (FeatureBuilder feature in featuresById.Values)
            {
                foreach (string dependency in feature.Dependencies)
                {
                    if (!featuresById.ContainsKey(dependency))
                    {
                        validation.Add(new(
                            EShaderUiValidationSeverity.Error,
                            $"Feature '{feature.Id}' depends on unknown feature '{dependency}'.",
                            feature.SourceLine));
                    }
                }

                foreach (string conflict in feature.Conflicts)
                {
                    if (!featuresById.ContainsKey(conflict))
                    {
                        validation.Add(new(
                            EShaderUiValidationSeverity.Error,
                            $"Feature '{feature.Id}' conflicts with unknown feature '{conflict}'.",
                            feature.SourceLine));
                    }
                }
            }

            HashSet<string> visiting = new(StringComparer.Ordinal);
            HashSet<string> visited = new(StringComparer.Ordinal);

            foreach (FeatureBuilder feature in featuresById.Values)
                DetectCycles(feature, featuresById, validation, visiting, visited);
        }

        private static void ValidateUberCoverage(
            string? sourcePath,
            Dictionary<string, FeatureBuilder> featuresById,
            IReadOnlyList<ShaderUiProperty> properties,
            List<ShaderUiValidationIssue> validation)
        {
            if (!IsUberAnnotationSource(sourcePath))
                return;

            foreach (FeatureBuilder feature in featuresById.Values)
            {
                if (!feature.HasExplicitMetadata &&
                    feature.GuardMacro?.StartsWith("XRENGINE_UBER_DISABLE_", StringComparison.Ordinal) == true)
                {
                    validation.Add(new(
                        EShaderUiValidationSeverity.Warning,
                        $"Uber feature '{feature.Id}' relies on inferred guard metadata. Add an explicit //@feature annotation.",
                        feature.SourceLine));
                }
            }

            foreach (ShaderUiProperty property in properties)
            {
                if (!property.HasExplicitMetadata && ShouldRequireExplicitUberPropertyMetadata(property))
                {
                    validation.Add(new(
                        EShaderUiValidationSeverity.Warning,
                        $"Uber property '{property.Name}' is missing explicit //@property metadata for the custom inspector.",
                        property.SourceLine));
                }
            }
        }

        private static void DetectCycles(
            FeatureBuilder feature,
            Dictionary<string, FeatureBuilder> featuresById,
            List<ShaderUiValidationIssue> validation,
            HashSet<string> visiting,
            HashSet<string> visited)
        {
            if (visited.Contains(feature.Id))
                return;

            if (!visiting.Add(feature.Id))
            {
                validation.Add(new(
                    EShaderUiValidationSeverity.Error,
                    $"Feature dependency cycle detected at '{feature.Id}'.",
                    feature.SourceLine));
                return;
            }

            foreach (string dependency in feature.Dependencies)
            {
                if (featuresById.TryGetValue(dependency, out FeatureBuilder? dependencyFeature))
                    DetectCycles(dependencyFeature, featuresById, validation, visiting, visited);
            }

            visiting.Remove(feature.Id);
            visited.Add(feature.Id);
        }

        private static bool TryParseDirective(
            string trimmedLine,
            ParseState state,
            List<ShaderUiValidationIssue> validation,
            int lineNumber)
        {
            Match match = DirectiveRegex().Match(trimmedLine);
            if (!match.Success)
                return false;

            string directiveName = match.Groups["name"].Value;
            string rawArgs = match.Groups["args"].Value;
            List<string> arguments = SplitArguments(rawArgs);
            Dictionary<string, string> namedArgs = ParseNamedArguments(arguments);

            switch (directiveName)
            {
                case "category":
                    state.CurrentCategory = FirstStringArgument(arguments, namedArgs, "name");
                    state.CurrentSubcategory = null;
                    break;

                case "subcategory":
                    state.CurrentSubcategory = FirstStringArgument(arguments, namedArgs, "name");
                    break;

                case "tooltip":
                    state.PendingTooltip = AppendTooltip(state.PendingTooltip, FirstStringArgument(arguments, namedArgs, "text"));
                    if (state.PendingProperty is not null)
                        state.PendingProperty = state.PendingProperty with { Tooltip = state.PendingTooltip };
                    if (state.PendingFeature is not null)
                        state.PendingFeature = state.PendingFeature with { Tooltip = state.PendingTooltip };
                    break;

                case "tooltip.key":
                    string? tooltipKey = FirstStringArgument(arguments, namedArgs, "key");
                    if (!string.IsNullOrWhiteSpace(tooltipKey))
                    {
                        string text = $"Localization Key: {tooltipKey}";
                        state.PendingTooltip = AppendTooltip(state.PendingTooltip, text);
                        if (state.PendingProperty is not null)
                            state.PendingProperty = state.PendingProperty with { Tooltip = state.PendingTooltip };
                        if (state.PendingFeature is not null)
                            state.PendingFeature = state.PendingFeature with { Tooltip = state.PendingTooltip };
                    }
                    break;

                case "feature":
                    string? featureTooltip = namedArgs.TryGetValue("tooltip", out string? featureTooltipArg)
                        ? BuildTooltip(state.PendingTooltip, Unquote(featureTooltipArg))
                        : state.PendingTooltip;
                    state.PendingFeature = new PendingFeatureAnnotation(
                        namedArgs.TryGetValue("id", out string? id) ? Unquote(id) : FirstStringArgument(arguments, namedArgs, "id"),
                        namedArgs.TryGetValue("name", out string? name) ? Unquote(name) : null,
                        ParseBool(namedArgs, "required"),
                        ParseFeatureCost(namedArgs),
                        ParseDefaultEnabled(namedArgs),
                        state.CurrentCategory,
                        state.CurrentSubcategory,
                        featureTooltip,
                        ParseStringList(arguments, namedArgs, "depends"),
                        ParseStringList(arguments, namedArgs, "conflicts"),
                        ParseSortOrder(namedArgs),
                        lineNumber);
                    break;

                case "depends":
                    if (state.PendingFeature is null)
                    {
                        validation.Add(new(EShaderUiValidationSeverity.Warning, "@depends should follow a feature annotation.", lineNumber));
                    }
                    else
                    {
                        state.PendingFeature = state.PendingFeature with
                        {
                            Dependencies = MergeLists(state.PendingFeature.Dependencies, ParseStringList(arguments, namedArgs, null)),
                        };
                    }
                    break;

                case "conflicts":
                    if (state.PendingFeature is null)
                    {
                        validation.Add(new(EShaderUiValidationSeverity.Warning, "@conflicts should follow a feature annotation.", lineNumber));
                    }
                    else
                    {
                        state.PendingFeature = state.PendingFeature with
                        {
                            Conflicts = MergeLists(state.PendingFeature.Conflicts, ParseStringList(arguments, namedArgs, null)),
                        };
                    }
                    break;

                case "property":
                    string? propertyTooltip = namedArgs.TryGetValue("tooltip", out string? propertyTooltipArg)
                        ? BuildTooltip(state.PendingTooltip, Unquote(propertyTooltipArg))
                        : state.PendingTooltip;
                    state.PendingProperty = new PendingPropertyAnnotation(
                        namedArgs.TryGetValue("name", out string? propertyName) ? Unquote(propertyName) : null,
                        namedArgs.TryGetValue("display", out string? display) ? Unquote(display) : null,
                        ParsePropertyMode(namedArgs),
                        namedArgs.TryGetValue("range", out string? range) ? Unquote(range) : null,
                        namedArgs.TryGetValue("slot", out string? slot) ? Unquote(slot) : null,
                        namedArgs.TryGetValue("enum", out string? enumOptions) ? Unquote(enumOptions) : null,
                        ParseBool(namedArgs, "toggle"),
                        namedArgs.TryGetValue("feature", out string? propertyFeature) ? Unquote(propertyFeature) : null,
                        state.CurrentCategory,
                        state.CurrentSubcategory,
                        propertyTooltip,
                        namedArgs.TryGetValue("default", out string? defaultLiteral) ? Unquote(defaultLiteral) : null);
                    break;
            }

            return true;
        }

        private static bool TryEnterGuard(
            string trimmedLine,
            HashSet<string> definedMacros,
            ParseState state,
            Stack<GuardScope> guardStack,
            Dictionary<string, FeatureBuilder> featuresById,
            List<ShaderUiValidationIssue> validation,
            int lineNumber)
        {
            Match match = GuardRegex().Match(trimmedLine);
            if (!match.Success)
                return false;

            bool isIfndef = string.Equals(match.Groups["kind"].Value, "ifndef", StringComparison.Ordinal);
            string macro = match.Groups["macro"].Value;

            PendingFeatureAnnotation? pendingFeature = state.PendingFeature;
            string? featureId = pendingFeature?.Id;

            if (string.IsNullOrWhiteSpace(featureId) && TryCreateImplicitFeatureFromGuard(macro, out string implicitFeatureId))
                featureId = implicitFeatureId;

            if (featureId is not null)
            {
                string displayName = pendingFeature?.DisplayName ?? FormatDisplayName(featureId);
                bool guardDefinedEnablesFeature = !isIfndef;
                bool defaultEnabled = pendingFeature?.DefaultEnabled
                    ?? (guardDefinedEnablesFeature ? definedMacros.Contains(macro) : !definedMacros.Contains(macro));

                if (!featuresById.TryGetValue(featureId, out FeatureBuilder? feature))
                {
                    feature = new FeatureBuilder(featureId, lineNumber);
                    featuresById.Add(featureId, feature);
                }

                feature.Apply(
                    displayName,
                    pendingFeature?.Category ?? state.CurrentCategory,
                    pendingFeature?.Subcategory ?? state.CurrentSubcategory,
                    pendingFeature?.Tooltip,
                    macro,
                    guardDefinedEnablesFeature,
                    defaultEnabled,
                    pendingFeature?.Required ?? false,
                    pendingFeature?.Cost ?? EShaderUiFeatureCost.Unspecified,
                    pendingFeature is not null,
                    pendingFeature?.Dependencies,
                    pendingFeature?.Conflicts,
                    pendingFeature?.SortOrder ?? feature.SortOrder);

                guardStack.Push(new GuardScope(macro, isIfndef, featureId));
            }
            else
            {
                guardStack.Push(new GuardScope(macro, isIfndef, null));
                if (pendingFeature is not null)
                {
                    validation.Add(new(
                        EShaderUiValidationSeverity.Error,
                        $"Feature annotation '{pendingFeature.Id ?? pendingFeature.DisplayName ?? "<unnamed>"}' could not bind to guard '{macro}'.",
                        lineNumber));
                }
            }

            state.PendingFeature = null;
            state.PendingTooltip = null;
            return true;
        }

        private static string? ResolveFeatureId(Stack<GuardScope> guardStack)
        {
            foreach (GuardScope guard in guardStack)
            {
                if (!string.IsNullOrWhiteSpace(guard.FeatureId))
                    return guard.FeatureId;
            }

            return null;
        }

        private static bool TryCreateImplicitFeatureFromGuard(string macro, out string featureId)
        {
            const string uberDisablePrefix = "XRENGINE_UBER_DISABLE_";
            if (macro.StartsWith(uberDisablePrefix, StringComparison.Ordinal))
            {
                featureId = ToFeatureId(macro[uberDisablePrefix.Length..]);
                return true;
            }

            featureId = string.Empty;
            return false;
        }

        private static HashSet<string> CollectDefinedMacros(string source)
        {
            HashSet<string> macros = new(StringComparer.Ordinal);
            foreach (Match match in DefineRegex().Matches(source))
            {
                if (match.Success)
                    macros.Add(match.Groups["macro"].Value);
            }

            return macros;
        }

        private static string NormalizeNewlines(string source)
            => source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        private static bool IsUberAnnotationSource(string? sourcePath)
            => !string.IsNullOrWhiteSpace(sourcePath) &&
               sourcePath.Replace('\\', '/').Contains("/Shaders/Uber/", StringComparison.OrdinalIgnoreCase);

        private static bool ShouldRequireExplicitUberPropertyMetadata(ShaderUiProperty property)
        {
            if (!(property.Name.StartsWith("_", StringComparison.Ordinal) || string.Equals(property.Name, "AlphaCutoff", StringComparison.Ordinal)))
                return false;

            if (property.Name is "_LightingColorMode" or "_LightingDirectionMode" or "_StylizedSpecular" or "_MatcapNormal" or "_FlipbookFrame")
                return false;

            if (property.Name.EndsWith("_ST", StringComparison.Ordinal) ||
                property.Name.EndsWith("Pan", StringComparison.Ordinal) ||
                property.Name.EndsWith("UV", StringComparison.Ordinal) ||
                property.Name.EndsWith("ThemeIndex", StringComparison.Ordinal) ||
                property.Name.StartsWith("_Enable", StringComparison.Ordinal) ||
                property.Name.EndsWith("Enabled", StringComparison.Ordinal) ||
                property.Name.EndsWith("Toggle", StringComparison.Ordinal) ||
                string.Equals(property.Name, "_Mode", StringComparison.Ordinal) ||
                string.Equals(property.Name, "_AlphaForceOpaque", StringComparison.Ordinal) ||
                string.Equals(property.Name, "_AlphaMod", StringComparison.Ordinal))
                return false;

            return true;
        }

        private static int ParseArraySize(string value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

        private static string? BuildTooltip(string? explicitTooltip, string? inlineCommentTooltip)
        {
            if (!string.IsNullOrWhiteSpace(explicitTooltip))
                return explicitTooltip;

            return string.IsNullOrWhiteSpace(inlineCommentTooltip) ? null : inlineCommentTooltip;
        }

        private static string? ExtractInlineComment(string line)
        {
            int commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIndex < 0)
                return null;

            string comment = line[(commentIndex + 2)..].Trim();
            return string.IsNullOrWhiteSpace(comment) ? null : comment;
        }

        private static string FormatDisplayName(string value)
        {
            string trimmed = value.TrimStart('_');
            if (trimmed.EndsWith("_ST", StringComparison.Ordinal))
                return FormatDisplayName(trimmed[..^3]) + " Tiling / Offset";

            StringBuilder builder = new(trimmed.Length + 8);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char current = trimmed[i];
                if (current == '_')
                {
                    if (builder.Length > 0 && builder[^1] != ' ')
                        builder.Append(' ');
                    continue;
                }

                bool addSpace = i > 0 &&
                    ((char.IsUpper(current) && (char.IsLower(trimmed[i - 1]) || char.IsDigit(trimmed[i - 1]))) ||
                     (char.IsDigit(current) && char.IsLetter(trimmed[i - 1])));

                if (addSpace && builder.Length > 0 && builder[^1] != ' ')
                    builder.Append(' ');

                builder.Append(current);
            }

            return builder.ToString().Trim();
        }

        private static string ToFeatureId(string macroSuffix)
            => macroSuffix.Replace('_', '-').ToLowerInvariant();

        private static List<string> SplitArguments(string raw)
        {
            List<string> parts = [];
            if (string.IsNullOrWhiteSpace(raw))
                return parts;

            StringBuilder current = new();
            bool inQuotes = false;
            int bracketDepth = 0;

            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch == '"')
                    inQuotes = !inQuotes;
                else if (!inQuotes)
                {
                    if (ch == '[')
                        bracketDepth++;
                    else if (ch == ']')
                        bracketDepth = Math.Max(0, bracketDepth - 1);
                    else if (ch == ',' && bracketDepth == 0)
                    {
                        AppendArgument(parts, current);
                        continue;
                    }
                }

                current.Append(ch);
            }

            AppendArgument(parts, current);
            return parts;
        }

        private static void AppendArgument(List<string> parts, StringBuilder builder)
        {
            string value = builder.ToString().Trim();
            if (value.Length > 0)
                parts.Add(value);
            builder.Clear();
        }

        private static Dictionary<string, string> ParseNamedArguments(IEnumerable<string> arguments)
        {
            Dictionary<string, string> named = new(StringComparer.OrdinalIgnoreCase);
            foreach (string argument in arguments)
            {
                int equalsIndex = argument.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                string key = argument[..equalsIndex].Trim();
                string value = argument[(equalsIndex + 1)..].Trim();
                if (key.Length > 0)
                    named[key] = value;
            }

            return named;
        }

        private static string? FirstStringArgument(List<string> arguments, Dictionary<string, string> namedArgs, string? fallbackKey)
        {
            if (fallbackKey is not null && namedArgs.TryGetValue(fallbackKey, out string? namedValue))
                return Unquote(namedValue);

            foreach (string argument in arguments)
            {
                if (argument.Contains('=', StringComparison.Ordinal))
                    continue;
                return Unquote(argument);
            }

            return null;
        }

        private static string Unquote(string value)
        {
            string trimmed = value.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
                ? trimmed[1..^1]
                : trimmed;
        }

        private static bool ParseBool(Dictionary<string, string> namedArgs, string key)
            => namedArgs.TryGetValue(key, out string? value) &&
               bool.TryParse(Unquote(value), out bool parsed) &&
               parsed;

        private static bool? ParseDefaultEnabled(Dictionary<string, string> namedArgs)
        {
            if (!namedArgs.TryGetValue("default", out string? value))
                return null;

            string parsed = Unquote(value);
            return parsed.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                   parsed.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseSortOrder(Dictionary<string, string> namedArgs)
            => namedArgs.TryGetValue("order", out string? value) &&
               int.TryParse(Unquote(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : int.MaxValue;

        private static EShaderUiFeatureCost ParseFeatureCost(Dictionary<string, string> namedArgs)
        {
            if (!namedArgs.TryGetValue("cost", out string? value))
                return EShaderUiFeatureCost.Unspecified;

            return Unquote(value).ToLowerInvariant() switch
            {
                "none" => EShaderUiFeatureCost.None,
                "low" => EShaderUiFeatureCost.Low,
                "medium" => EShaderUiFeatureCost.Medium,
                "high" => EShaderUiFeatureCost.High,
                _ => EShaderUiFeatureCost.Unspecified,
            };
        }

        private static EShaderUiPropertyMode ParsePropertyMode(Dictionary<string, string> namedArgs)
        {
            if (!namedArgs.TryGetValue("mode", out string? value))
                return EShaderUiPropertyMode.Unspecified;

            return Unquote(value).ToLowerInvariant() switch
            {
                "constant" => EShaderUiPropertyMode.Static,
                "static" => EShaderUiPropertyMode.Static,
                "animated" => EShaderUiPropertyMode.Animated,
                _ => EShaderUiPropertyMode.Unspecified,
            };
        }

        private static List<string> ParseStringList(List<string> arguments, Dictionary<string, string> namedArgs, string? namedKey)
        {
            if (namedKey is not null && namedArgs.TryGetValue(namedKey, out string? namedValue))
                return ParseStringList(Unquote(namedValue));

            List<string> results = [];
            foreach (string argument in arguments)
            {
                if (argument.Contains('=', StringComparison.Ordinal))
                    continue;

                string value = Unquote(argument);
                if (!string.IsNullOrWhiteSpace(value))
                    results.Add(value);
            }

            return results;
        }

        private static List<string> ParseStringList(string value)
        {
            List<string> results = [];
            foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                    results.Add(part);
            }

            return results;
        }

        private static List<string> MergeLists(IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
        {
            HashSet<string> merged = new(existing, StringComparer.Ordinal);
            foreach (string value in incoming)
                merged.Add(value);

            return merged.ToList();
        }

        private static string? AppendTooltip(string? existing, string? next)
        {
            if (string.IsNullOrWhiteSpace(next))
                return existing;

            if (string.IsNullOrWhiteSpace(existing))
                return next;

            return existing + Environment.NewLine + next;
        }

        [GeneratedRegex("^\\s*//@(?<name>[A-Za-z0-9_.]+)\\((?<args>.*)\\)\\s*$", RegexOptions.Compiled)]
        private static partial Regex DirectiveRegex();

        [GeneratedRegex("^\\s*#(?<kind>ifdef|ifndef)\\s+(?<macro>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
        private static partial Regex GuardRegex();

        [GeneratedRegex("^\\s*#define\\s+(?<macro>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.Multiline)]
        private static partial Regex DefineRegex();

        [GeneratedRegex("(?:layout\\s*\\([^\\)]*\\)\\s*)?uniform\\s+(?:lowp|mediump|highp\\s+)?(?<type>\\w+)\\s+(?<name>\\w+)\\s*(?:\\[(?<array>\\d+)\\])?\\s*[;=]", RegexOptions.Compiled)]
        private static partial Regex UniformRegex();

        private sealed class FeatureBuilder(string id, int sourceLine)
        {
            public string Id { get; } = id;
            public int SourceLine { get; } = sourceLine;

            public string DisplayName { get; private set; } = FormatDisplayName(id);
            public string? Category { get; private set; }
            public string? Subcategory { get; private set; }
            public string? Tooltip { get; private set; }
            public string? GuardMacro { get; private set; }
            public bool GuardDefinedEnablesFeature { get; private set; }
            public bool DefaultEnabled { get; private set; } = true;
            public bool Required { get; private set; }
            public EShaderUiFeatureCost Cost { get; private set; } = EShaderUiFeatureCost.Unspecified;
            public bool HasExplicitMetadata { get; private set; }
            public int SortOrder { get; private set; } = int.MaxValue;
            public List<string> Dependencies { get; } = [];
            public List<string> Conflicts { get; } = [];

            public void Apply(
                string displayName,
                string? category,
                string? subcategory,
                string? tooltip,
                string? guardMacro,
                bool guardDefinedEnablesFeature,
                bool defaultEnabled,
                bool required,
                EShaderUiFeatureCost cost,
                bool hasExplicitMetadata,
                IReadOnlyList<string>? dependencies,
                IReadOnlyList<string>? conflicts,
                int sortOrder)
            {
                DisplayName = displayName;
                Category ??= category;
                Subcategory ??= subcategory;
                Tooltip = AppendTooltip(Tooltip, tooltip);
                GuardMacro ??= guardMacro;
                GuardDefinedEnablesFeature = guardDefinedEnablesFeature;
                DefaultEnabled = defaultEnabled;
                Required |= required;
                if (cost != EShaderUiFeatureCost.Unspecified)
                    Cost = cost;
                HasExplicitMetadata |= hasExplicitMetadata;
                if (sortOrder != int.MaxValue)
                    SortOrder = Math.Min(SortOrder, sortOrder);

                if (dependencies is not null)
                    AddUnique(Dependencies, dependencies);
                if (conflicts is not null)
                    AddUnique(Conflicts, conflicts);
            }

            public ShaderUiFeature Build()
                => new(
                    Id,
                    DisplayName,
                    Category,
                    Subcategory,
                    Tooltip,
                    GuardMacro,
                    GuardDefinedEnablesFeature,
                    DefaultEnabled,
                    Required,
                    Cost,
                    HasExplicitMetadata,
                    Dependencies.ToArray(),
                    Conflicts.ToArray());

            private static void AddUnique(List<string> target, IEnumerable<string> incoming)
            {
                HashSet<string> seen = new(target, StringComparer.Ordinal);
                foreach (string value in incoming)
                {
                    if (seen.Add(value))
                        target.Add(value);
                }
            }
        }

        private sealed record ParseState
        {
            public string? CurrentCategory { get; set; }
            public string? CurrentSubcategory { get; set; }
            public string? PendingTooltip { get; set; }
            public PendingFeatureAnnotation? PendingFeature { get; set; }
            public PendingPropertyAnnotation? PendingProperty { get; set; }
        }

        private sealed record GuardScope(string Macro, bool IsIfndef, string? FeatureId);

        private sealed record PendingFeatureAnnotation(
            string? Id,
            string? DisplayName,
            bool Required,
            EShaderUiFeatureCost Cost,
            bool? DefaultEnabled,
            string? Category,
            string? Subcategory,
            string? Tooltip,
            IReadOnlyList<string> Dependencies,
            IReadOnlyList<string> Conflicts,
            int SortOrder,
            int SourceLine);

        private sealed record PendingPropertyAnnotation(
            string? Name,
            string? DisplayName,
            EShaderUiPropertyMode Mode,
            string? Range,
            string? Slot,
            string? EnumOptions,
            bool IsToggle,
            string? FeatureId,
            string? Category,
            string? Subcategory,
            string? Tooltip,
            string? DefaultLiteral);
    }
}