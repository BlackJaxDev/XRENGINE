using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static readonly ConditionalWeakTable<XRMaterial, AuthoringInspectorState> AuthoringStates = new();
    private static readonly Vector4 AuthoringGroupColor = new(0.54f, 0.78f, 0.98f, 1.0f);
    private static readonly Vector4 AuthoringMatchColor = new(1.0f, 0.82f, 0.28f, 1.0f);
    private static readonly Vector4 AuthoringUnsupportedColor = new(0.95f, 0.48f, 0.34f, 1.0f);

    private static bool DrawPoiyomiAuthoringTree(
        XRMaterial material,
        XRShader fragmentShader,
        ShaderUiManifest manifest,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        IReadOnlySet<string> unavailableFeatureIds,
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        ref bool variantChanged)
    {
        if (!manifest.PropertyLookup.ContainsKey("_LightingMapMode") &&
            !manifest.PropertyLookup.ContainsKey("_RimStyle"))
            return false;

        Stopwatch drawTimer = Stopwatch.StartNew();
        ShaderAuthoringSchema schema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(manifest);
        AuthoringInspectorState state = AuthoringStates.GetValue(material, static _ => new());
        state.Schema = schema;

        DrawAuthoringToolbar(material, schema, state);
        DrawAuthoringWorkspaces(
            material,
            schema,
            parameterLookup,
            samplerLookup,
            state,
            ref variantChanged);

        MaterialExpressionContext expressionContext = new(material, parameterLookup, samplerLookup);
        Stopwatch buildTimer = Stopwatch.StartNew();
        state.Rows.Clear();
        state.MatchRows.Clear();
        RefreshSearchIndex(schema, state);
        AddVisibleRows(schema.Root, 0, true, state, expressionContext);
        buildTimer.Stop();
        MaterialAuthoringTelemetry.Instance.RecordBuild(buildTimer.Elapsed, state.Rows.Count);

        if (state.Rows.Count == 0)
        {
            ImGui.TextDisabled("No authored controls match the current filter.");
            return true;
        }

        bool constantLiteralChanged = false;
        int submitted = 0;
        float rowHeight = ImGui.GetFrameHeightWithSpacing() + 2.0f;
        float childHeight = Math.Clamp(state.Rows.Count * rowHeight, 140.0f, 620.0f);
        if (ImGui.BeginChild(
            $"PoiyomiAuthoringTree##{schema.Fingerprint}",
            new Vector2(0.0f, childHeight),
            ImGuiChildFlags.Border))
        {
            unsafe
            {
                var clipper = new ImGuiListClipper();
                ImGuiNative.ImGuiListClipper_Begin(&clipper, state.Rows.Count, rowHeight);
                while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
                {
                    for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    {
                        AuthoringRow row = state.Rows[index];
                        submitted++;
                        ImGui.PushID(row.Node.SemanticId);
                        if (index == state.ScrollToRow ||
                            string.Equals(row.Node.SemanticId, state.ScrollToSemanticId, StringComparison.Ordinal))
                        {
                            ImGui.SetScrollHereY(0.5f);
                            state.ScrollToRow = -1;
                            state.ScrollToSemanticId = null;
                        }
                        DrawAuthoringRow(
                            material,
                            fragmentShader,
                            featureLookup,
                            unavailableFeatureIds,
                            parameterLookup,
                            samplerLookup,
                            schema,
                            state,
                            row,
                            ref variantChanged,
                            ref constantLiteralChanged);
                        ImGui.PopID();
                    }
                }
                ImGuiNative.ImGuiListClipper_End(&clipper);
            }
        }
        ImGui.EndChild();

        if (state.ShowDiagnostics)
            DrawAuthoringDiagnostics(schema);

        drawTimer.Stop();
        MaterialAuthoringTelemetry.Instance.RecordDraw(drawTimer.Elapsed, submitted);
        if (variantChanged)
            material.RequestUberVariantRebuild();
        else if (constantLiteralChanged)
            material.RequestUberVariantRebuildDebounced();
        return true;
    }

    private static void DrawAuthoringToolbar(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        AuthoringInspectorState state)
    {
        ImGui.SetNextItemWidth(MathF.Min(360.0f, ImGui.GetContentRegionAvail().X * 0.45f));
        ImGui.InputTextWithHint("##PoiyomiAuthoringSearch", "Search labels, source names, IDs, tooltips...", ref state.Search, 256u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Previous") && state.MatchRows.Count > 0)
        {
            state.ActiveMatch = (state.ActiveMatch - 1 + state.MatchRows.Count) % state.MatchRows.Count;
            state.ScrollToRow = state.MatchRows[state.ActiveMatch];
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Next") && state.MatchRows.Count > 0)
        {
            state.ActiveMatch = (state.ActiveMatch + 1) % state.MatchRows.Count;
            state.ScrollToRow = state.MatchRows[state.ActiveMatch];
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            state.Search = string.Empty;
            state.ExactSemanticId = null;
            state.ActiveMatch = 0;
        }

        ImGui.SetNextItemWidth(MathF.Min(320.0f, ImGui.GetContentRegionAvail().X * 0.40f));
        ImGui.InputTextWithHint("##PoiyomiExactLookup", "Exact source or semantic property...", ref state.ExactLookup, 256u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reveal"))
        {
            ShaderAuthoringNode? exact = null;
            schema.PropertyLookup.TryGetValue(state.ExactLookup, out exact);
            if (exact is null)
                schema.NodeLookup.TryGetValue(state.ExactLookup, out exact);
            state.ExactSemanticId = exact?.SemanticId;
            if (exact is not null)
            {
                state.Search = exact.SourcePropertyName ?? exact.SemanticId;
                state.ScrollToSemanticId = exact.SemanticId;
            }
        }

        ImGui.Checkbox("Simple", ref state.SimpleMode);
        ImGui.SameLine();
        ImGui.Checkbox("Unsupported", ref state.ShowUnsupported);
        ImGui.SameLine();
        ImGui.Checkbox("Annotations", ref state.ShowDeveloperView);
        ImGui.SameLine();
        ImGui.Checkbox("Diagnostics", ref state.ShowDiagnostics);

        if (ImGui.Button("Copy Material"))
            ImGui.SetClipboardText(CaptureClipboard(material, schema).Serialize());
        ImGui.SameLine();
        string? clipboard = GetClipboardTextSafe();
        bool canPaste = MaterialAuthoringClipboardPayload.TryDeserialize(clipboard, out _);
        using (new ImGuiDisabledScope(!canPaste))
        if (ImGui.Button("Paste Material") &&
            MaterialAuthoringClipboardPayload.TryDeserialize(clipboard, out MaterialAuthoringClipboardPayload? payload) &&
            payload is not null)
        {
            state.Status = $"Clipboard contains {payload.Values.Count} semantic value(s). Use a property context menu to paste compatible values.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Prepare Variant"))
            state.Status = material.PrepareUberVariantImmediately()
                ? "Variant prepared."
                : material.UberVariantStatus.FailureReason ?? "Variant preparation failed.";
        ImGui.SameLine();
        if (ImGui.Button("Rebuild"))
        {
            material.RequestUberVariantRebuild();
            MaterialAuthoringTelemetry.Instance.RecordVariantRequest();
            state.Status = "Variant rebuild requested.";
        }

        if (!string.IsNullOrWhiteSpace(state.Status))
            ImGui.TextDisabled(state.Status);
    }

    private static void AddVisibleRows(
        ShaderAuthoringNode node,
        int depth,
        bool parentEnabled,
        AuthoringInspectorState state,
        MaterialExpressionContext context)
    {
        bool searchActive = !string.IsNullOrWhiteSpace(state.Search) ||
            !string.IsNullOrWhiteSpace(state.ExactSemanticId);
        bool selfMatches = state.SearchSelfMatches.Contains(node);
        bool descendantMatches = searchActive &&
            !selfMatches &&
            state.SearchBranches.Contains(node);
        if (searchActive && !selfMatches && !descendantMatches && node.Kind != EShaderAuthoringNodeKind.Root)
            return;

        if (node.IsHiddenBuiltIn &&
            !searchActive &&
            !string.Equals(node.SemanticId, state.ExactSemanticId, StringComparison.Ordinal))
            return;
        if (state.SimpleMode && !node.Options.IsVisibleSimple && !HasAuthoredManifestValue(node, context))
            return;
        if (!state.ShowUnsupported && !node.IsSupported && !descendantMatches)
            return;
        if (node.VisibilityExpression is not null &&
            !state.Conditions.Evaluate(node.VisibilityExpression, context))
            return;

        bool enabled = parentEnabled &&
            (node.EnableExpression is null || state.Conditions.Evaluate(node.EnableExpression, context));
        if (node.Kind != EShaderAuthoringNodeKind.Root)
        {
            int rowIndex = state.Rows.Count;
            state.Rows.Add(new(node, depth, enabled, selfMatches));
            if (selfMatches)
                state.MatchRows.Add(rowIndex);
        }

        if (node.Children.Count == 0)
            return;

        bool expanded = node.Kind == EShaderAuthoringNodeKind.Root ||
            searchActive ||
            IsAuthoringNodeExpanded(
                state,
                node,
                node.Options.DefaultExpand || node.Options.ReferenceFloatTogglesExpand || depth < 1);
        if (!expanded)
            return;

        bool childrenEnabled = enabled &&
            (node.EnableChildrenExpression is null ||
             state.Conditions.Evaluate(node.EnableChildrenExpression, context));
        foreach (ShaderAuthoringNode child in node.Children)
            AddVisibleRows(child, depth + (node.Kind == EShaderAuthoringNodeKind.Root ? 0 : 1), childrenEnabled, state, context);
    }

    private static bool IsAuthoringNodeExpanded(
        AuthoringInspectorState state,
        ShaderAuthoringNode node,
        bool defaultValue)
    {
        if (node.Options.PersistentExpand)
            return MaterialAuthoringPersistence.Instance.IsExpanded(
                state.Schema!.Fingerprint,
                node.SemanticId,
                defaultValue);
        return state.TransientExpansion.TryGetValue(node.SemanticId, out bool expanded)
            ? expanded
            : defaultValue;
    }

    private static void SetAuthoringNodeExpanded(
        AuthoringInspectorState state,
        ShaderAuthoringNode node,
        bool expanded)
    {
        if (node.Options.PersistentExpand)
            MaterialAuthoringPersistence.Instance.SetExpanded(
                state.Schema!.Fingerprint,
                node.SemanticId,
                expanded);
        else
            state.TransientExpansion[node.SemanticId] = expanded;
    }

    private static string ResolveAuthoringLabel(
        ShaderAuthoringNode node,
        AuthoringInspectorState state)
    {
        if (!state.SimpleMode || node.Options.AlternativeLabels.Count == 0)
            return node.DisplayName;
        return node.Options.AlternativeLabels[0];
    }

    private static void DrawAuthoringHelpLinks(
        ShaderAuthoringNode node,
        AuthoringInspectorState state)
    {
        DrawAuthoringLink(node.Options.ButtonHelp, "Help", node, state);
        DrawAuthoringLink(node.Options.ButtonAuthor, "Author", node, state);
    }

    private static void DrawAuthoringLink(
        string? definition,
        string fallbackLabel,
        ShaderAuthoringNode node,
        AuthoringInspectorState state)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return;
        MaterialAuthoringActionGraph graph = MaterialAuthoringActionGraph.Parse(definition);
        MaterialAuthoringAction? link = graph.Actions.FirstOrDefault(static action =>
            action.Kind == EMaterialAuthoringActionKind.Url);
        string label = definition.Contains("text:", StringComparison.OrdinalIgnoreCase)
            ? fallbackLabel
            : fallbackLabel;
        ImGui.SameLine();
        using (new ImGuiDisabledScope(link is null))
        if (ImGui.SmallButton($"{label}##{fallbackLabel}_{node.SemanticId}") && link is not null)
            state.Status = MaterialAuthoringCommandRegistry.RequestSafeLink(link.Target, out string? diagnostic)
                ? "Safe-link confirmation requested."
                : diagnostic;
        if (link is null && ImGui.IsItemHovered())
            ImGui.SetTooltip("The imported target is unavailable or unsafe.");
    }
    private static void RefreshSearchIndex(
        ShaderAuthoringSchema schema,
        AuthoringInspectorState state)
    {
        string key = $"{schema.Fingerprint}\u001f{state.Search}\u001f{state.ExactSemanticId}";
        if (string.Equals(state.SearchIndexKey, key, StringComparison.Ordinal))
            return;

        state.SearchIndexKey = key;
        state.SearchSelfMatches.Clear();
        state.SearchBranches.Clear();
        if (string.IsNullOrWhiteSpace(state.Search) &&
            string.IsNullOrWhiteSpace(state.ExactSemanticId))
            return;

        foreach (ShaderAuthoringNode node in schema.DeclarationOrder)
        {
            if (!MatchesSearch(node, state.Search) &&
                !string.Equals(node.SemanticId, state.ExactSemanticId, StringComparison.Ordinal))
                continue;
            state.SearchSelfMatches.Add(node);
            for (ShaderAuthoringNode? current = node; current is not null; current = current.Parent)
                state.SearchBranches.Add(current);
        }
    }

    private static bool MatchesSearch(ShaderAuthoringNode node, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return false;

        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (node.DisplayName.Contains(search, comparison) ||
            node.SemanticId.Contains(search, comparison) ||
            (node.SourcePropertyName?.Contains(search, comparison) ?? false) ||
            (node.Options.Tooltip?.Contains(search, comparison) ?? false))
            return true;

        foreach (string alternate in node.Options.AlternativeLabels)
            if (alternate.Contains(search, comparison))
                return true;
        return false;
    }

    private static bool HasAuthoredManifestValue(ShaderAuthoringNode node, MaterialExpressionContext context)
        => node.ManifestProperty is not null && context.HasValue(node.ManifestProperty.Name);

    private static void DrawAuthoringRow(
        XRMaterial material,
        XRShader fragmentShader,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        IReadOnlySet<string> unavailableFeatureIds,
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        ShaderAuthoringSchema schema,
        AuthoringInspectorState state,
        AuthoringRow row,
        ref bool variantChanged,
        ref bool constantLiteralChanged)
    {
        ShaderAuthoringNode node = row.Node;
        if (node.Options.MarginTop > 0.0f)
            ImGui.Dummy(new Vector2(0.0f, node.Options.MarginTop));
        if (node.Options.DrawBorder)
            ImGui.Separator();
        ImGui.Indent(row.Depth * 15.0f);
        switch (node.Kind)
        {
            case EShaderAuthoringNodeKind.Section:
            case EShaderAuthoringNodeKind.Subsection:
                DrawAuthoringGroup(schema, state, row);
                break;
            case EShaderAuthoringNodeKind.Decorator:
                DrawAuthoringDecorator(node);
                break;
            case EShaderAuthoringNodeKind.ToolLauncher:
                DrawAuthoringTool(material, node, state);
                break;
            case EShaderAuthoringNodeKind.Action:
                DrawAuthoringAction(material, node, schema, parameterLookup, state);
                break;
            default:
                DrawAuthoringProperty(
                    material,
                    fragmentShader,
                    featureLookup,
                    unavailableFeatureIds,
                    parameterLookup,
                    samplerLookup,
                    node,
                    row.Enabled,
                    state,
                    ref variantChanged,
                    ref constantLiteralChanged);
                break;
        }
        ImGui.Unindent(row.Depth * 15.0f);
    }

    private static void DrawAuthoringGroup(
        ShaderAuthoringSchema schema,
        AuthoringInspectorState state,
        AuthoringRow row)
    {
        bool searchActive = !string.IsNullOrWhiteSpace(state.Search);
        bool expanded = searchActive ||
            IsAuthoringNodeExpanded(
                state,
                row.Node,
                row.Node.Options.DefaultExpand ||
                row.Node.Options.ReferenceFloatTogglesExpand ||
                row.Depth == 0);
        string indicator = expanded ? "v" : ">";
        if (ImGui.SmallButton($"{indicator}##toggle"))
        {
            expanded = !expanded;
            SetAuthoringNodeExpanded(state, row.Node, expanded);
        }
        ImGui.SameLine();
        ImGui.TextColored(
            row.SearchMatch ? AuthoringMatchColor : AuthoringGroupColor,
            ResolveAuthoringLabel(row.Node, state));
        DrawNodeTooltip(row.Node);
        DrawAuthoringHelpLinks(row.Node, state);
    }

    private static void DrawAuthoringDecorator(ShaderAuthoringNode node)
    {
        if (node.WidgetId is "Space" or "ThrySpace")
        {
            ImGui.Spacing();
            return;
        }

        if (node.WidgetId is "Header" or "ThryHeaderLabel")
            ImGui.SeparatorText(node.DisplayName);
        else
        {
            Vector4 color = node.WidgetId is "IMPORTANT" or "sRGBWarning"
                ? AuthoringMatchColor
                : AuthoringGroupColor;
            ImGui.TextWrapped(node.DisplayName);
            if (node.WidgetId is "IMPORTANT" or "sRGBWarning")
                ImGui.TextColored(color, node.WidgetId == "IMPORTANT" ? "Important" : "sRGB / data texture warning");
        }
        DrawNodeTooltip(node);
    }

    private static void DrawAuthoringTool(
        XRMaterial material,
        ShaderAuthoringNode node,
        AuthoringInspectorState state)
    {
        bool supported = ShaderAuthoringWidgetRegistry.IsAllowlistedTool(node.WidgetId);
        using (new ImGuiDisabledScope(!supported))
        if (ImGui.SmallButton($"{node.DisplayName}##tool"))
        {
            OpenAuthoringWorkspace(material, node.WidgetId, node.SourcePropertyName);
            state.Status = node.WidgetId switch
            {
                "ThryShaderOptimizerLockButton" => material.PrepareUberVariantImmediately()
                    ? "Variant prepared."
                    : material.UberVariantStatus.FailureReason ?? "Variant preparation failed.",
                "ThryRGBAPacker" => "RGBA packer ready; open the texture authoring workspace from the texture field.",
                "ThryDecalPositioning" => "Decal positioning is ready for the selected renderer and material slot.",
                _ => $"Tool '{node.WidgetId}' is registered.",
            };
        }
        if (!supported)
        {
            ImGui.SameLine();
            ImGui.TextColored(AuthoringUnsupportedColor, $"Unsupported tool: {node.WidgetId ?? "(none)"}");
        }
        DrawNodeTooltip(node);
        DrawAuthoringHelpLinks(node, state);
    }

    private static void DrawAuthoringAction(
        XRMaterial material,
        ShaderAuthoringNode node,
        ShaderAuthoringSchema schema,
        Dictionary<string, ShaderVar> parameterLookup,
        AuthoringInspectorState state)
    {
        MaterialAuthoringActionGraph graph =
            MaterialAuthoringActionGraph.Parse(node.Options.OnClick ?? node.Options.Actions);
        MaterialAuthoringActionContext context =
            CreateAuthoringActionContext(material, node, schema, parameterLookup);
        MaterialAuthoringActionPreview preview = MaterialAuthoringActionExecutor.Preview(graph, context);
        using (new ImGuiDisabledScope(!preview.CanExecute || graph.Actions.Count == 0))
        if (ImGui.SmallButton($"{node.DisplayName}##action"))
        {
            state.Status = MaterialAuthoringActionExecutor.TryExecute(graph, context, out MaterialAuthoringTransactionReport report)
                ? $"Applied {report.AppliedStepCount} action side effect(s)."
                : string.Join("; ", report.Diagnostics);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(preview.CanExecute
                ? string.Join("\n", preview.SideEffects)
                : string.Join("\n", preview.Diagnostics));
        DrawNodeTooltip(node);
    }

    private static void DrawAuthoringProperty(
        XRMaterial material,
        XRShader fragmentShader,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        IReadOnlySet<string> unavailableFeatureIds,
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        ShaderAuthoringNode node,
        bool enabled,
        AuthoringInspectorState state,
        ref bool variantChanged,
        ref bool constantLiteralChanged)
    {
        ShaderUiProperty? property = node.ManifestProperty;
        if (property is null)
        {
            ImGui.TextColored(AuthoringUnsupportedColor, $"{node.DisplayName}  [not mapped]");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Source: {node.SourcePropertyName}\nAnnotation: {node.WidgetId ?? "generic"}");
            return;
        }

        ShaderVar? parameter = property.IsSampler ? null : FindParameter(parameterLookup, property.Name);
        samplerLookup.TryGetValue(property.Name, out SamplerBindingEntry? samplerBinding);
        bool featureAvailable = property.FeatureId is null || !unavailableFeatureIds.Contains(property.FeatureId);
        bool featureEnabled = featureAvailable &&
            (property.FeatureId is null ||
             !featureLookup.TryGetValue(property.FeatureId, out ShaderUiFeature? feature) ||
             feature.Required ||
             material.IsUberFeatureEnabled(feature.Id, feature.DefaultEnabled));
        EShaderUiPropertyMode mode = material.GetUberPropertyMode(property.Name, property.DefaultMode, property.IsSampler);

        if (!ImGui.BeginTable("##authoringProperty", 4, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 88.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.46f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 92.0f);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        bool nonDefault = IsAuthoringPropertyNonDefault(material, node, parameter, mode);
        string displayedPropertyName = nonDefault ? $"{node.DisplayName} *" : node.DisplayName;
        ImGui.TextColored(state.SearchSelfMatches.Contains(node)
            ? AuthoringMatchColor
            : Vector4.One, displayedPropertyName);
        DrawNodeTooltip(node);
        DrawUberPropertyContextMenu(material, property, parameter, samplerBinding, mode, ref variantChanged, ref constantLiteralChanged);
        DrawAuthoringExtendedPropertyMenu(
            material,
            node,
            parameter,
            samplerBinding,
            state,
            ref variantChanged);

        ImGui.TableSetColumnIndex(1);
        if (property.IsSampler)
            ImGui.TextDisabled("Texture");
        else
        {
            bool animated = mode == EShaderUiPropertyMode.Animated;
            using (new ImGuiDisabledScope(node.Attributes.Any(static value => value.Name == "DoNotAnimate")))
            if (ImGui.Checkbox("Animated", ref animated))
            {
                EShaderUiPropertyMode requested =
                    animated ? EShaderUiPropertyMode.Animated : EShaderUiPropertyMode.Static;
                string? modeDiagnostic = MaterialAnimationAuthoringService.ValidateModeChange(
                    material,
                    node,
                    requested,
                    confirmedBindingRepair: false);
                if (modeDiagnostic is null)
                {
                    mode = requested;
                    variantChanged |= material.SetUberPropertyMode(property.Name, mode);
                }
                else
                    state.Status = modeDiagnostic;
            }
        }

        ImGui.TableSetColumnIndex(2);
        using (new ImGuiDisabledScope(!enabled || !featureEnabled || node.IsNonModifiableTexture))
        if (property.IsSampler)
        {
            XRTexture? previousTexture = samplerBinding?.AssignedTexture;
            if (samplerBinding is not null)
                DrawSamplerTextureField(material, samplerBinding);
            if (node.WidgetId == "TextureKeyword" &&
                !ReferenceEquals(previousTexture, samplerBinding?.AssignedTexture))
                variantChanged = true;
            else
                ImGui.TextDisabled("Unbound");
        }
        else if (parameter is null)
        {
            if (ImGui.SmallButton("Create") &&
                TryCreateMaterialParameter(material, property.GlslType, property.Name))
                parameterLookup[property.Name] = material.Parameters[^1];
        }
        else
        {
            object previous = parameter.GenericValue;
            if (!TryDrawAuthoringWidget(material, node, parameter, property, parameterLookup))
                DrawUberShaderParameterControl(material, parameter, property);
            if (!Equals(previous, parameter.GenericValue))
            {
                if (mode == EShaderUiPropertyMode.Static)
                    constantLiteralChanged |= material.RefreshUberPropertyStaticLiteral(property.Name);
                ExecuteOnValueActions(material, node, schema: state.Schema!, parameterLookup, parameter.GenericValue, state);
                if (TryGetParameterPath(material, parameter, out string animationPath))
                    state.Status ??= MaterialAnimationAuthoringService.AutoMarkAnimated(
                        material,
                        node,
                        animationPath,
                        parameter.GenericValue);
            }
        }

        ImGui.TableSetColumnIndex(3);
        if (!featureAvailable)
            ImGui.TextDisabled("Unavailable");
        else if (!enabled)
            ImGui.TextDisabled("Condition off");
        else if (node.WidgetId is { Length: > 0 } widget &&
                 ShaderAuthoringWidgetRegistry.TryResolve(widget, out ShaderAuthoringWidgetDescriptor descriptor))
            ImGui.TextDisabled(descriptor.Capability.ToString());
        else
            ImGui.TextDisabled(property.GlslType);
        ImGui.EndTable();

        if (state.ShowDeveloperView && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Semantic: {node.SemanticId}");
            ImGui.TextUnformatted($"Source: {node.SourcePropertyName}");
            ImGui.TextUnformatted($"Destination: {property.Name}");
            ImGui.TextUnformatted($"Widget: {node.WidgetId ?? "manifest default"}");
            foreach (ShaderAuthoringAttribute attribute in node.Attributes)
                ImGui.TextDisabled($"{attribute.Name}({attribute.Arguments})");
            ImGui.EndTooltip();
        }
    }

    private static void DrawAuthoringExtendedPropertyMenu(
        XRMaterial material,
        ShaderAuthoringNode node,
        ShaderVar? parameter,
        SamplerBindingEntry? sampler,
        AuthoringInspectorState state,
        ref bool variantChanged)
    {
        ImGui.SameLine();
        if (ImGui.SmallButton($"...##AuthoringContext_{node.SemanticId}"))
            ImGui.OpenPopup($"AuthoringExtended_{node.SemanticId}");
        if (!ImGui.BeginPopup($"AuthoringExtended_{node.SemanticId}"))
            return;

        if (ImGui.MenuItem("Copy raw source name", null, false, node.SourcePropertyName is not null))
            ImGui.SetClipboardText(node.SourcePropertyName!);
        if (ImGui.MenuItem("Copy semantic ID"))
            ImGui.SetClipboardText(node.SemanticId);

        string? runtimePath = null;
        if (parameter is not null)
            TryGetParameterPath(material, parameter, out runtimePath);
        else if (sampler is not null)
        {
            int slot = sampler.MaterialTextureSlot ?? sampler.FallbackTextureSlot;
            runtimePath = $"Textures[{slot}]";
        }
        if (ImGui.MenuItem("Copy animation path", null, false, runtimePath is not null))
            ImGui.SetClipboardText(runtimePath!);
        if (ImGui.MenuItem("Add animation binding", null, false, runtimePath is not null))
            state.Status = MaterialAnimationAuthoringService.RequestBinding(
                material,
                node,
                runtimePath!,
                parameter?.GenericValue ?? sampler?.AssignedTexture);
        if (ImGui.MenuItem("Insert keyframe", null, false, runtimePath is not null))
            state.Status = MaterialAnimationAuthoringService.RequestKeyframe(
                material,
                node,
                runtimePath!,
                parameter?.GenericValue ?? sampler?.AssignedTexture);

        MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
        bool included = !metadata.LocalOverrides.TryGetValue(
            $"presetInclude:{node.SemanticId}",
            out string? includedValue) ||
            !string.Equals(includedValue, "false", StringComparison.OrdinalIgnoreCase);
        if (ImGui.MenuItem("Include in authored preset", null, included))
            metadata.LocalOverrides[$"presetInclude:{node.SemanticId}"] = (!included).ToString();

        if (ImGui.MenuItem("Copy property + references"))
            ImGui.SetClipboardText(CaptureAuthoringNodeClipboard(material, state.Schema!, node).Serialize());
        bool hasAuthoringClipboard = MaterialAuthoringClipboardPayload.TryDeserialize(
            GetClipboardTextSafe(),
            out MaterialAuthoringClipboardPayload? pastePayload);
        if (ImGui.MenuItem("Paste Special preview", null, false, hasAuthoringClipboard))
        {
            AuthoringToolState pasteTools = AuthoringToolStates.GetValue(material, static _ => new());
            pasteTools.Open = true;
            pasteTools.Workspace = EAuthoringWorkspace.Presets;
            pasteTools.PendingPayload = pastePayload;
            pasteTools.Status = "Paste Special preview loaded. Review compatible count before Apply.";
        }
        if (ImGui.MenuItem("Edit local note"))
        {
            AuthoringToolState tools = AuthoringToolStates.GetValue(material, static _ => new());
            tools.Open = true;
            tools.Workspace = EAuthoringWorkspace.LocaleNotes;
            tools.NoteSemanticId = node.SemanticId;
        }

        if (ImGui.BeginMenu("Source / default"))
        {
            ImGui.TextUnformatted($"Source: {node.SourcePropertyName ?? "none"}");
            ImGui.TextUnformatted($"Semantic: {node.SemanticId}");
            ImGui.TextUnformatted($"Type: {node.SourceType ?? node.ManifestProperty?.GlslType ?? "unknown"}");
            ImGui.TextUnformatted($"Default: {node.DefaultValue ?? "unspecified"}");
            ImGui.TextUnformatted($"Classification: {node.Classification ?? "unknown"}");
            ImGui.EndMenu();
        }

        bool canReset = node.ManifestProperty is not null;
        if (ImGui.MenuItem("Reset property + references", null, false, canReset))
        {
            ResetAuthoringNodeRecursive(material, node);
            variantChanged = true;
            state.Status = $"Reset {node.DisplayName} and {node.ReferencedProperties.Count} reference(s).";
        }
        ImGui.EndPopup();
    }

    private static MaterialAuthoringClipboardPayload CaptureAuthoringNodeClipboard(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        ShaderAuthoringNode root)
    {
        List<MaterialAuthoringPresetValue> values = [];
        HashSet<ShaderAuthoringNode> visited = new(ReferenceEqualityComparer.Instance);
        Capture(root);
        return new()
        {
            SchemaId = schema.SchemaId,
            ScopeSemanticId = root.SemanticId,
            Values = values,
        };

        void Capture(ShaderAuthoringNode node)
        {
            if (!visited.Add(node))
                return;
            if (node.ManifestProperty is ShaderUiProperty property && !property.IsSampler)
            {
                ShaderVar? parameter = material.Parameters.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, property.Name, StringComparison.Ordinal));
                if (parameter is not null && TrySerializeShaderParameterValue(parameter, out string serialized))
                    values.Add(new(
                        node.SemanticId,
                        property.GlslType,
                        serialized,
                        null,
                        material.GetUberPropertyMode(property.Name, property.DefaultMode, false)));
            }
            foreach (ShaderAuthoringNode child in node.Children)
                Capture(child);
            foreach (ShaderAuthoringNode reference in node.ReferencedProperties)
                Capture(reference);
        }
    }

    private static bool IsAuthoringPropertyNonDefault(
        XRMaterial material,
        ShaderAuthoringNode node,
        ShaderVar? parameter,
        EShaderUiPropertyMode mode)
    {
        ShaderUiProperty? property = node.ManifestProperty;
        if (property is null)
            return false;
        if (mode != property.DefaultMode)
            return true;
        if (MaterialAuthoringMetadataStore.Instance.Get(material).LocalOverrides.Keys.Any(key =>
                key.Contains(node.SemanticId, StringComparison.Ordinal)))
            return true;
        if (parameter is null || string.IsNullOrWhiteSpace(node.DefaultValue))
            return false;
        string sourceDefault = node.DefaultValue.Trim().Trim('(', ')');
        string actual = Convert.ToString(parameter.GenericValue, CultureInfo.InvariantCulture) ?? string.Empty;
        if (parameter is ShaderFloat or ShaderInt or ShaderUInt or ShaderBool)
            return !double.TryParse(sourceDefault, NumberStyles.Float, CultureInfo.InvariantCulture, out double expected) ||
                   !double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double current) ||
                   Math.Abs(expected - current) > 0.000001;
        return false;
    }
    private static void ResetAuthoringNodeRecursive(XRMaterial material, ShaderAuthoringNode node)
    {
        MaterialAuthoringTransaction transaction = new($"Reset {node.DisplayName}");
        HashSet<ShaderAuthoringNode> visited = new(ReferenceEqualityComparer.Instance);
        AddReset(node);
        transaction.TryExecute(out _);
        return;

        void AddReset(ShaderAuthoringNode current)
        {
            if (!visited.Add(current))
                return;
            if (current.ManifestProperty is ShaderUiProperty property)
            {
                transaction.Add(
                    material,
                    current.DisplayName,
                    () =>
                    {
                        if (!property.IsSampler)
                            RemoveMaterialParameter(material, property.Name);
                        material.ResetUberPropertyOverride(property.Name);
                    },
                    true);
            }
            foreach (ShaderAuthoringNode reference in current.ReferencedProperties)
                AddReset(reference);
        }
    }
    private static MaterialAuthoringActionContext CreateAuthoringActionContext(
        XRMaterial material,
        ShaderAuthoringNode node,
        ShaderAuthoringSchema schema,
        Dictionary<string, ShaderVar> parameterLookup)
        => new()
        {
            Material = material,
            Node = node,
            ValidateProperty = (target, value) =>
            {
                if (MaterialRenderStateActionAdapter.IsSupported(target))
                    return MaterialRenderStateActionAdapter.Validate(target, value);
                if (!schema.PropertyLookup.TryGetValue(target, out ShaderAuthoringNode? targetNode) ||
                    targetNode.ManifestProperty is not ShaderUiProperty targetProperty ||
                    targetProperty.IsSampler ||
                    !parameterLookup.TryGetValue(targetProperty.Name, out ShaderVar? targetParameter))
                    return $"Action target '{target}' is unavailable.";
                string serialized = EnsureClipboardEnvelope(targetParameter, value ?? string.Empty);
                return CanApplyShaderParameterClipboard(targetParameter, serialized)
                    ? null
                    : $"Value '{value}' is incompatible with '{target}'.";
            },
            SetProperty = (target, value) =>
            {
                if (MaterialRenderStateActionAdapter.IsSupported(target))
                {
                    MaterialRenderStateActionAdapter.Apply(material, target, value);
                    return;
                }
                ShaderAuthoringNode targetNode = schema.PropertyLookup[target];
                ShaderUiProperty targetProperty = targetNode.ManifestProperty!;
                ShaderVar targetParameter = parameterLookup[targetProperty.Name];
                TryApplyShaderParameterClipboard(
                    material,
                    targetParameter,
                    EnsureClipboardEnvelope(targetParameter, value ?? string.Empty));
                if (material.GetUberPropertyMode(targetProperty.Name, targetProperty.DefaultMode, false) ==
                    EShaderUiPropertyMode.Static)
                    material.RefreshUberPropertyStaticLiteral(targetProperty.Name);
            },
            ValidateTag = static target => string.IsNullOrWhiteSpace(target) ? "Tag name is empty." : null,
            SetTag = (target, value) => MaterialAuthoringMetadataStore.Instance.SetTag(material, target, value),
            ValidateShader = static target =>
                target.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "The requested shader has no registered semantic converter.",
            SetShader = target =>
                MaterialAuthoringMetadataStore.Instance.Get(material).ImportedShaderIdentity = target,
        };

    private static void ExecuteOnValueActions(
        XRMaterial material,
        ShaderAuthoringNode node,
        ShaderAuthoringSchema schema,
        Dictionary<string, ShaderVar> parameterLookup,
        object value,
        AuthoringInspectorState state)
    {
        if (string.IsNullOrWhiteSpace(node.Options.OnValueActions))
            return;
        string activeValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        MaterialAuthoringActionGraph graph =
            MaterialAuthoringActionGraph.ParseForValue(node.Options.OnValueActions, activeValue);
        if (graph.Actions.Count == 0)
            return;
        MaterialAuthoringActionContext context =
            CreateAuthoringActionContext(material, node, schema, parameterLookup);
        state.Status = MaterialAuthoringActionExecutor.TryExecute(graph, context, out MaterialAuthoringTransactionReport report)
            ? $"Applied {report.AppliedStepCount} coupled preset state change(s)."
            : string.Join("; ", report.Diagnostics);
    }
    private static void DrawNodeTooltip(ShaderAuthoringNode node)
    {
        if (!ImGui.IsItemHovered())
            return;
        string? tooltip = node.Options.Tooltip ?? node.ManifestProperty?.Tooltip;
        if (!string.IsNullOrWhiteSpace(tooltip))
            ImGui.SetTooltip(tooltip);
    }

    private static void DrawAuthoringDiagnostics(ShaderAuthoringSchema schema)
    {
        if (!ImGui.TreeNode($"Schema diagnostics ({schema.Issues.Count})##AuthoringDiagnostics"))
            return;
        int count = Math.Min(schema.Issues.Count, 64);
        for (int i = 0; i < count; i++)
        {
            ShaderAuthoringIssue issue = schema.Issues[i];
            Vector4 color = issue.Severity == EShaderAuthoringIssueSeverity.Error
                ? AuthoringUnsupportedColor
                : issue.Severity == EShaderAuthoringIssueSeverity.Warning
                    ? AuthoringMatchColor
                    : Vector4.One;
            ImGui.TextColored(color, $"L{issue.SourceLine}: {issue.Message}");
        }
        if (schema.Issues.Count > count)
            ImGui.TextDisabled($"{schema.Issues.Count - count} additional diagnostics omitted.");
        ImGui.TreePop();
    }

    private static MaterialAuthoringClipboardPayload CaptureClipboard(
        XRMaterial material,
        ShaderAuthoringSchema schema)
    {
        List<MaterialAuthoringPresetValue> values = [];
        foreach (ShaderAuthoringNode node in schema.DeclarationOrder)
        {
            ShaderUiProperty? property = node.ManifestProperty;
            if (property is null || property.IsSampler)
                continue;
            ShaderVar? parameter = material.Parameters.FirstOrDefault(value =>
                string.Equals(value.Name, property.Name, StringComparison.Ordinal));
            if (parameter is null)
                continue;
            values.Add(new(
                node.SemanticId,
                property.GlslType,
                Convert.ToString(parameter.GenericValue, CultureInfo.InvariantCulture) ?? string.Empty,
                null,
                material.GetUberPropertyMode(property.Name, property.DefaultMode, false)));
        }

        return new()
        {
            SchemaId = schema.SchemaId,
            ScopeSemanticId = schema.Root.SemanticId,
            Values = values,
        };
    }

    private sealed class MaterialExpressionContext(
        XRMaterial material,
        Dictionary<string, ShaderVar> parameters,
        Dictionary<string, SamplerBindingEntry> samplers) : IShaderAuthoringExpressionContext
    {
        public bool TryResolve(string operand, out ShaderAuthoringValue value)
        {
            if (operand.StartsWith("texture:", StringComparison.OrdinalIgnoreCase))
                operand = operand["texture:".Length..];
            if (parameters.TryGetValue(operand, out ShaderVar? parameter))
            {
                value = new(parameter.GenericValue);
                return true;
            }
            if (operand.StartsWith("texture_name:", StringComparison.OrdinalIgnoreCase))
            {
                string textureProperty = operand["texture_name:".Length..];
                value = new(
                    samplers.TryGetValue(textureProperty, out SamplerBindingEntry? namedSampler)
                        ? namedSampler.AssignedTexture?.Name ?? string.Empty
                        : string.Empty);
                return true;
            }
            if (samplers.TryGetValue(operand, out SamplerBindingEntry? sampler))
            {
                value = new(sampler.AssignedTexture is not null);
                return true;
            }
            if (operand.Equals("render_queue", StringComparison.OrdinalIgnoreCase) ||
                operand.Equals("renderQueue", StringComparison.OrdinalIgnoreCase))
            {
                value = new(
                    MaterialAuthoringMetadataStore.Instance.Get(material).ImportedRenderQueue ??
                    material.RenderPass);
                return true;
            }
            if (operand.StartsWith("animated:", StringComparison.OrdinalIgnoreCase))
            {
                string name = operand["animated:".Length..];
                value = new(material.GetUberPropertyMode(name, EShaderUiPropertyMode.Animated, false) ==
                            EShaderUiPropertyMode.Animated);
                return true;
            }
            if (operand.StartsWith("static:", StringComparison.OrdinalIgnoreCase))
            {
                string name = operand["static:".Length..];
                value = new(material.GetUberPropertyMode(name, EShaderUiPropertyMode.Static, false) ==
                            EShaderUiPropertyMode.Static);
                return true;
            }
            if (operand.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
            {
                string version = operand["version:".Length..];
                value = new(version is "9.3" or "9.3.64" or "poiyomi-9.3.64");
                return true;
            }
            if (operand.StartsWith("cap:", StringComparison.OrdinalIgnoreCase))
            {
                string capability = operand["cap:".Length..];
                value = new(capability is
                    "xrengine" or "poiyomi-9.3" or "opengl-4.6" or "vulkan" or
                    "texture-array" or "semantic-material-authoring");
                return true;
            }

            value = default;
            return false;
        }

        public bool HasValue(string propertyName)
            => parameters.ContainsKey(propertyName) ||
               (samplers.TryGetValue(propertyName, out SamplerBindingEntry? sampler) &&
                sampler.AssignedTexture is not null);
    }

    private sealed class AuthoringInspectorState
    {
        public ShaderAuthoringSchema? Schema;
        public string Search = string.Empty;
        public string ExactLookup = string.Empty;
        public string? ExactSemanticId;
        public string? Status;
        public bool SimpleMode = true;
        public bool ShowUnsupported;
        public bool ShowDeveloperView;
        public bool ShowDiagnostics;
        public int ActiveMatch;
        public int ScrollToRow = -1;
        public string? ScrollToSemanticId;
        public readonly Dictionary<string, bool> TransientExpansion = new(StringComparer.Ordinal);
        public readonly AuthoringConditionCache Conditions = new();
        public string? SearchIndexKey;
        public readonly HashSet<ShaderAuthoringNode> SearchSelfMatches =
            new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<ShaderAuthoringNode> SearchBranches =
            new(ReferenceEqualityComparer.Instance);
        public readonly List<AuthoringRow> Rows = [];
        public readonly List<int> MatchRows = [];
    }

    private sealed class AuthoringConditionCache
    {
        private readonly Dictionary<ShaderAuthoringExpression, ConditionEntry> _entries =
            new(ReferenceEqualityComparer.Instance);

        public bool Evaluate(
            ShaderAuthoringExpression expression,
            IShaderAuthoringExpressionContext context)
        {
            HashCode hash = new();
            foreach (string dependency in expression.Dependencies)
            {
                hash.Add(dependency, StringComparer.Ordinal);
                if (context.TryResolve(dependency, out ShaderAuthoringValue value))
                    hash.Add(value.Value);
            }
            int fingerprint = hash.ToHashCode();
            if (_entries.TryGetValue(expression, out ConditionEntry cached) &&
                cached.Fingerprint == fingerprint)
                return cached.Result;
            bool result = expression.EvaluateBoolean(context);
            _entries[expression] = new(fingerprint, result);
            MaterialAuthoringTelemetry.Instance.RecordConditionInvalidation();
            return result;
        }

        private readonly record struct ConditionEntry(int Fingerprint, bool Result);
    }
    private readonly record struct AuthoringRow(
        ShaderAuthoringNode Node,
        int Depth,
        bool Enabled,
        bool SearchMatch);
}
