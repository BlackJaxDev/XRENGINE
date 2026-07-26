using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ImGuiNET;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static readonly ConditionalWeakTable<XRMaterial, AuthoringToolState> AuthoringToolStates = new();
    private static readonly MaterialAuthoringLocaleService AuthoringLocales = new();
    private static readonly MaterialAuthoringPresetLibrary AuthoringPresets = new();

    private static void OpenAuthoringWorkspace(XRMaterial material, string? widgetId, string? sourceProperty)
    {
        AuthoringToolState tools = AuthoringToolStates.GetValue(material, static _ => new());
        tools.SourceProperty = sourceProperty;
        tools.Workspace = widgetId switch
        {
            "ThryRGBAPacker" => EAuthoringWorkspace.TexturePacker,
            "ThryDecalPositioning" => EAuthoringWorkspace.Decal,
            "ThryShaderOptimizerLockButton" => EAuthoringWorkspace.Optimizer,
            _ => EAuthoringWorkspace.Utilities,
        };
        tools.Open = true;
    }

    private static void DrawAuthoringWorkspaces(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        AuthoringInspectorState inspectorState,
        ref bool variantChanged)
    {
        AuthoringToolState tools = AuthoringToolStates.GetValue(material, static _ => new());
        if (!tools.LocaleInitialized)
        {
            AuthoringLocales.ImportSourceLabels(schema);
            tools.LocaleInitialized = true;
        }

        if (ImGui.SmallButton("Tools"))
        {
            tools.Open = !tools.Open;
            tools.Workspace = EAuthoringWorkspace.Utilities;
        }
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"Locale: {tools.Locale} | Variant: {material.UberVariantStatus.Stage} | " +
            $"0x{material.UberVariantStatus.RequestedVariantHash:x16}");

        if (!tools.Open)
            return;

        ImGui.PushID($"AuthoringWorkspaces_{RuntimeHelpers.GetHashCode(material)}");
        if (ImGui.BeginTabBar("##AuthoringWorkspaceTabs"))
        {
            DrawWorkspaceTab("Rendering", EAuthoringWorkspace.Rendering, tools, () =>
                DrawRenderingWorkspace(material));
            DrawWorkspaceTab("Presets / Clipboard", EAuthoringWorkspace.Presets, tools, () =>
                DrawPresetWorkspace(material, schema, parameterLookup, inspectorState));
            DrawWorkspaceTab("Texture Packer", EAuthoringWorkspace.TexturePacker, tools, () =>
                DrawTexturePackerWorkspace(tools));
            DrawWorkspaceTab("Gradient / Curve", EAuthoringWorkspace.GradientCurve, tools, () =>
                DrawGradientCurveWorkspace(tools));
            DrawWorkspaceTab("Texture Array", EAuthoringWorkspace.TextureArray, tools, () =>
                DrawTextureArrayWorkspace(tools));
            DrawWorkspaceTab("Decal", EAuthoringWorkspace.Decal, tools, () =>
                DrawDecalWorkspace(material, tools));
            DrawWorkspaceTab("Links / Cleanup", EAuthoringWorkspace.Utilities, tools, () =>
                DrawUtilityWorkspace(material, schema, tools));
            DrawWorkspaceTab("Locale / Notes", EAuthoringWorkspace.LocaleNotes, tools, () =>
                DrawLocaleNotesWorkspace(material, schema, inspectorState, tools));
            DrawWorkspaceTab("Optimizer", EAuthoringWorkspace.Optimizer, tools, () =>
                DrawOptimizerWorkspace(material, tools));
            ImGui.EndTabBar();
        }
        variantChanged |= tools.VariantChanged;
        tools.VariantChanged = false;
        if (ImGui.SmallButton("Close tools"))
            tools.Open = false;
        ImGui.PopID();
    }

    private static void DrawWorkspaceTab(
        string label,
        EAuthoringWorkspace workspace,
        AuthoringToolState tools,
        Action draw)
    {
        if (!ImGui.BeginTabItem(label))
            return;
        tools.Workspace = workspace;
        draw();
        ImGui.EndTabItem();
    }

    private static void DrawRenderingWorkspace(XRMaterial material)
    {
        MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
        ImGui.TextUnformatted($"Native render pass: {material.RenderPass}");
        ImGui.TextUnformatted($"Imported Unity queue: {metadata.ImportedRenderQueue?.ToString() ?? "not recorded"}");
        ImGui.TextUnformatted($"Pass set: {string.Join(", ", material.PassSet.Passes.Select(static pass => pass.Identity))}");
        if (metadata.Tags.Count == 0)
            ImGui.TextDisabled("No authored tags.");
        foreach ((string name, string value) in metadata.Tags)
            ImGui.BulletText($"{name} = {value}");
        if (ImGui.SmallButton("Return to opaque preset"))
        {
            MaterialAuthoringTransaction transaction = new("Set opaque render preset");
            transaction.Add(
                material,
                "Opaque render pass",
                () => material.RenderPass = (int)XREngine.Data.Rendering.EDefaultRenderPass.OpaqueForward,
                true);
            transaction.TryExecute(out _);
        }

        DrawConversionReportWorkspace(material);
    }

    private static void DrawPresetWorkspace(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        Dictionary<string, ShaderVar> parameterLookup,
        AuthoringInspectorState inspectorState)
    {
        AuthoringToolState tools = AuthoringToolStates.GetValue(material, static _ => new());
        ImGui.InputTextWithHint("##PresetRoot", "Preset folder under project Assets", ref tools.PresetRoot, 512u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Index"))
        {
            IReadOnlyList<string> diagnostics = AuthoringPresets.Rebuild(tools.PresetRoot);
            tools.Status = diagnostics.Count == 0
                ? $"Indexed {AuthoringPresets.Entries.Count} preset(s)."
                : string.Join("; ", diagnostics.Take(3));
        }
        ImGui.InputTextWithHint("##PresetSearch", "Search presets...", ref tools.PresetSearch, 128u);

        foreach (MaterialAuthoringPresetEntry entry in AuthoringPresets.Search(tools.PresetSearch, null).Take(64))
        {
            if (!ImGui.Selectable($"{entry.Preset.Collection ?? "General"} / {entry.Preset.Name}##{entry.Path}"))
                continue;
            MaterialAuthoringClipboardPayload payload = new()
            {
                SchemaId = entry.Preset.SchemaId,
                ScopeSemanticId = schema.Root.SemanticId,
                Values = [.. entry.Preset.Values.Where(static value => value.Included)],
            };
            ApplyAuthoringPayload(material, schema, parameterLookup, payload, out string status);
            tools.Status = status;
            AuthoringPresets.MarkUsed(entry);
            tools.VariantChanged = true;
        }

        if (ImGui.SmallButton("Copy full material"))
            ImGui.SetClipboardText(CaptureClipboard(material, schema).Serialize());
        ImGui.SameLine();
        if (ImGui.SmallButton("Paste preview"))
        {
            if (MaterialAuthoringClipboardPayload.TryDeserialize(
                    GetClipboardTextSafe(),
                    out MaterialAuthoringClipboardPayload? payload) &&
                payload is not null)
            {
                int compatible = payload.Values.Count(value =>
                    schema.NodeLookup.TryGetValue(value.SemanticId, out ShaderAuthoringNode? node) &&
                    node.ManifestProperty is { IsSampler: false });
                tools.PendingPayload = payload;
                tools.Status = $"{compatible}/{payload.Values.Count} values are compatible. Apply or dismiss.";
            }
            else
                tools.Status = "Clipboard does not contain a supported material payload.";
        }
        if (tools.PendingPayload is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply preview"))
            {
                ApplyAuthoringPayload(
                    material,
                    schema,
                    parameterLookup,
                    tools.PendingPayload,
                    out tools.Status);
                tools.PendingPayload = null;
                tools.VariantChanged = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss"))
                tools.PendingPayload = null;
        }
        if (!string.IsNullOrWhiteSpace(tools.Status))
            ImGui.TextWrapped(tools.Status);
        inspectorState.Status = tools.Status;
    }

    private static bool ApplyAuthoringPayload(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        Dictionary<string, ShaderVar> parameterLookup,
        MaterialAuthoringClipboardPayload payload,
        out string status)
    {
        MaterialAuthoringTransaction transaction = new("Paste material authoring values");
        int compatible = 0;
        List<string> skipped = [];
        foreach (MaterialAuthoringPresetValue value in payload.Values)
        {
            if (!value.Included ||
                !schema.NodeLookup.TryGetValue(value.SemanticId, out ShaderAuthoringNode? node) ||
                node.ManifestProperty is not ShaderUiProperty property ||
                property.IsSampler ||
                !parameterLookup.TryGetValue(property.Name, out ShaderVar? parameter))
            {
                skipped.Add(value.SemanticId);
                continue;
            }

            string serialized = EnsureClipboardEnvelope(parameter, value.SerializedValue);
            if (!CanApplyShaderParameterClipboard(parameter, serialized))
            {
                skipped.Add(value.SemanticId);
                continue;
            }

            compatible++;
            ShaderVar captured = parameter;
            transaction.Add(
                material,
                node.DisplayName,
                () => CanApplyShaderParameterClipboard(captured, serialized)
                    ? null
                    : "Value type changed during paste.",
                () =>
                {
                    TryApplyShaderParameterClipboard(material, captured, serialized);
                    if (value.Mode.HasValue)
                        material.SetUberPropertyMode(property.Name, value.Mode.Value);
                },
                true);
        }

        MaterialAuthoringTransactionReport report = new(false, 0, []);
        bool succeeded = compatible > 0 && transaction.TryExecute(out report);
        status = succeeded
            ? $"Applied {compatible} semantic value(s); skipped {skipped.Count}."
            : compatible == 0
                ? $"No compatible values; skipped {skipped.Count}."
                : string.Join("; ", report.Diagnostics);
        return succeeded;
    }

    private static string EnsureClipboardEnvelope(ShaderVar parameter, string value)
    {
        if (value.StartsWith("xreparam:", StringComparison.Ordinal))
            return value;
        string type = parameter switch
        {
            ShaderFloat => "float",
            ShaderInt => "int",
            ShaderUInt => "uint",
            ShaderBool => "bool",
            ShaderVector2 => "vec2",
            ShaderVector3 => "vec3",
            ShaderVector4 => "vec4",
            _ => "unsupported",
        };
        return $"xreparam:{type}|{value}";
    }

    private static void DrawTexturePackerWorkspace(AuthoringToolState tools)
    {
        ImGui.TextWrapped($"Target: {tools.SourceProperty ?? "standalone output"}");
        ImGui.DragInt("Width", ref tools.PackWidth, 1.0f, 1, 16384);
        ImGui.DragInt("Height", ref tools.PackHeight, 1.0f, 1, 16384);
        ImGui.Checkbox("Linear data", ref tools.PackLinear);
        string[] labels = ["R", "G", "B", "A"];
        for (int channel = 0; channel < 4; channel++)
        {
            ImGui.PushID(channel);
            ImGui.DragFloat(labels[channel], ref tools.PackConstants[channel], 0.01f, 0.0f, 1.0f);
            ImGui.SameLine();
            ImGui.Checkbox("Invert", ref tools.PackInvert[channel]);
            ImGui.PopID();
        }
        if (ImGui.SmallButton("Build deterministic preview"))
        {
            int previewWidth = Math.Min(tools.PackWidth, 256);
            int previewHeight = Math.Min(tools.PackHeight, 256);
            TexturePackingRecipe recipe = new()
            {
                Width = previewWidth,
                Height = previewHeight,
                LinearData = tools.PackLinear,
                Channels =
                [
                    CreateConstantChannel(tools, 0),
                    CreateConstantChannel(tools, 1),
                    CreateConstantChannel(tools, 2),
                    CreateConstantChannel(tools, 3),
                ],
            };
            tools.PackedPreview = MaterialTexturePacker.Pack(
                recipe,
                new Dictionary<string, TexturePixelSource>());
            tools.Status = $"Previewed {previewWidth}x{previewHeight} RGBA ({(tools.PackLinear ? "linear" : "sRGB")}).";
        }
        if (tools.PackedPreview is not null)
        {
            Vector4 sample = tools.PackedPreview[tools.PackedPreview.Length / 2];
            ImGui.TextUnformatted($"RGBA sample: {sample.X:F3}, {sample.Y:F3}, {sample.Z:F3}, {sample.W:F3}");
            for (int channel = 0; channel < 4; channel++)
            {
                float value = sample[channel];
                ImGui.ProgressBar(value, new Vector2(100.0f, 0.0f), $"{labels[channel]} {value:F2}");
                if (channel < 3)
                    ImGui.SameLine();
            }
        }
        ImGui.TextDisabled("Advanced operations: brightness, hue, saturation, grayscale, transform, edge/kernel, and blend are recipe nodes.");
    }

    private static TexturePackingChannel CreateConstantChannel(AuthoringToolState tools, int index)
        => new()
        {
            Kind = ETextureChannelSourceKind.Constant,
            Constant = tools.PackConstants[index],
            Invert = tools.PackInvert[index],
            InputChannel = (ETextureChannel)index,
        };

    private static void DrawGradientCurveWorkspace(AuthoringToolState tools)
    {
        int gradientResolution = tools.Gradient.Resolution;
        if (ImGui.DragInt("Bake resolution", ref gradientResolution, 1.0f, 2, 16384))
            tools.Gradient.Resolution = gradientResolution;
        int interpolation = (int)tools.Gradient.Interpolation;
        if (ImGui.Combo("Interpolation", ref interpolation, "Linear\0Smooth\0Constant\0"))
            tools.Gradient.Interpolation = (EMaterialGradientInterpolation)interpolation;
        for (int index = 0; index < tools.Gradient.ColorKeys.Count; index++)
        {
            MaterialGradientKey key = tools.Gradient.ColorKeys[index];
            float position = key.Position;
            Vector4 value = key.Value;
            ImGui.PushID(index);
            bool changed = ImGui.DragFloat("Position", ref position, 0.0025f, 0.0f, 1.0f);
            ImGui.SameLine();
            changed |= ImGui.ColorEdit4("Color", ref value);
            if (changed)
                tools.Gradient.ColorKeys[index] = new(position, value);
            ImGui.PopID();
        }
        if (ImGui.SmallButton("Add gradient key"))
            tools.Gradient.ColorKeys.Add(new(0.5f, Vector4.One));
        ImGui.SameLine();
        if (ImGui.SmallButton("Bake gradient"))
        {
            tools.Gradient.Normalize();
            tools.PackedPreview = tools.Gradient.Bake();
            tools.Status = $"Baked {tools.PackedPreview.Length} deterministic gradient samples.";
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Four-channel curve");
        int curveResolution = tools.Curve.Resolution;
        if (ImGui.DragInt("Curve resolution", ref curveResolution, 1.0f, 2, 16384))
            tools.Curve.Resolution = curveResolution;
        if (ImGui.SmallButton("Bake curves"))
        {
            tools.PackedPreview = tools.Curve.Bake();
            tools.Status = $"Baked {tools.PackedPreview.Length} RGBA curve samples.";
        }
        ImGui.TextDisabled("Curve keys retain numeric value and in/out tangents in the versioned asset.");
    }

    private static void DrawTextureArrayWorkspace(AuthoringToolState tools)
    {
        ImGui.InputTextWithHint("##ArraySource", "Absolute source image path", ref tools.ArraySource, 512u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Add layer") && File.Exists(tools.ArraySource))
        {
            tools.Array.Layers.Add(new(
                Path.GetFullPath(tools.ArraySource),
                tools.ArrayWidth,
                tools.ArrayHeight,
                tools.ArrayFormat,
                tools.ArrayMipCount,
                tools.ArrayLinear ? EMaterialTextureColorSpace.Linear : EMaterialTextureColorSpace.Srgb,
                tools.ArraySemantic));
            tools.ArraySource = string.Empty;
        }
        ImGui.DragInt("Layer width", ref tools.ArrayWidth, 1.0f, 1, 16384);
        ImGui.DragInt("Layer height", ref tools.ArrayHeight, 1.0f, 1, 16384);
        ImGui.DragInt("Mip count", ref tools.ArrayMipCount, 1.0f, 1, 32);
        ImGui.Checkbox("Linear", ref tools.ArrayLinear);
        bool allowResample = tools.Array.AllowResample;
        if (ImGui.Checkbox("Allow explicit resample", ref allowResample))
            tools.Array.AllowResample = allowResample;
        for (int index = 0; index < tools.Array.Layers.Count; index++)
        {
            ImGui.PushID(index);
            ImGui.TextUnformatted($"{index}: {Path.GetFileName(tools.Array.Layers[index].SourcePath)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && index > 0)
                tools.Array.Move(index, index - 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                tools.Array.Layers.RemoveAt(index);
                index--;
            }
            ImGui.PopID();
        }
        IReadOnlyList<string> diagnostics = tools.Array.Validate();
        foreach (string diagnostic in diagnostics)
            ImGui.TextColored(AuthoringUnsupportedColor, diagnostic);
        if (diagnostics.Count == 0 && tools.Array.Layers.Count > 0)
            ImGui.TextUnformatted($"{tools.Array.Layers.Count} compatible layer(s), ordered deterministically.");
    }

    private static void DrawDecalWorkspace(XRMaterial material, AuthoringToolState tools)
    {
        ImGui.TextWrapped($"Decal property: {tools.SourceProperty ?? "select a decal positioning control"}");
        Vector3 position = tools.Decal.Position;
        Vector3 scale = tools.Decal.Scale;
        Vector2 uvOffset = tools.Decal.UvOffset;
        Vector2 uvScale = tools.Decal.UvScale;
        float depth = tools.Decal.DepthOffset;
        bool mirrored = tools.Decal.Mirrored;
        bool changed = ImGui.DragFloat3("Position", ref position, 0.01f);
        changed |= ImGui.DragFloat3("Scale", ref scale, 0.01f);
        changed |= ImGui.DragFloat2("UV offset", ref uvOffset, 0.0025f);
        changed |= ImGui.DragFloat2("UV scale", ref uvScale, 0.0025f);
        changed |= ImGui.DragFloat("Depth / side offset", ref depth, 0.001f);
        changed |= ImGui.Checkbox("Mirrored side", ref mirrored);
        if (changed)
            tools.Decal = tools.Decal with
            {
                Position = position,
                Scale = scale,
                UvOffset = uvOffset,
                UvScale = uvScale,
                DepthOffset = depth,
                Mirrored = mirrored,
            };
        ImGui.TextDisabled("Viewport raycast/gizmo activation uses the registered IMaterialDecalViewportBridge; numeric editing remains available without a scene viewport.");
        if (ImGui.SmallButton("Commit numeric decal transform"))
        {
            MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
            MaterialAuthoringTransaction transaction = new("Set decal transform");
            string serialized = JsonSerializer.Serialize(tools.Decal);
            string decalKey = $"decal:{tools.SourceProperty ?? "unknown"}";
            bool hadDecal = metadata.LocalOverrides.TryGetValue(decalKey, out string? previousDecal);
            transaction.AddStructural(
                material,
                tools.SourceProperty ?? "Decal",
                () => metadata.LocalOverrides[decalKey] = serialized,
                () =>
                {
                    if (hadDecal)
                        metadata.LocalOverrides[decalKey] = previousDecal!;
                    else
                        metadata.LocalOverrides.Remove(decalKey);
                },
                true);
            transaction.TryExecute(out MaterialAuthoringTransactionReport report);
            tools.Status = report.Succeeded ? "Decal transform committed." : string.Join("; ", report.Diagnostics);
        }
    }

    private static void DrawUtilityWorkspace(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        AuthoringToolState tools)
    {
        ImGui.TextUnformatted("Semantic translator preview");
        ImGui.TextDisabled($"{schema.NodeLookup.Count} stable nodes; source names are not used as cross-shader contracts.");
        ImGui.Separator();
        ImGui.TextUnformatted("Material cleanup");
        MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
        ImGui.TextUnformatted($"{metadata.ImportedTags.Count} imported tag(s), {metadata.Tags.Count} local tag(s), {metadata.LocalOverrides.Count} local override(s).");
        if (ImGui.SmallButton("List cleanup candidates"))
            tools.Status = "Cleanup report created. Imported reconversion metadata is protected by default.";
        ImGui.Separator();
        ImGui.TextUnformatted("Material linking");
        ImGui.InputText("Semantic ID", ref tools.LinkSemanticId, 256u);
        ImGui.InputText("Group name", ref tools.LinkName, 128u);
        if (ImGui.SmallButton("Create link descriptor"))
        {
            MaterialAuthoringPersistentLinkGroup group = new(
                MaterialAuthoringPersistentLinkGroup.CurrentVersion,
                Guid.NewGuid(),
                tools.LinkName,
                tools.LinkSemanticId,
                []);
            tools.LinkJson = group.Serialize();
            tools.Status = "Created a cycle-safe persistent semantic link descriptor.";
        }
        if (!string.IsNullOrWhiteSpace(tools.LinkJson) && ImGui.TreeNode("Link descriptor"))
        {
            ImGui.TextWrapped(tools.LinkJson);
            ImGui.TreePop();
        }
        if (!string.IsNullOrWhiteSpace(tools.Status))
            ImGui.TextWrapped(tools.Status);
    }

    private static void DrawLocaleNotesWorkspace(
        XRMaterial material,
        ShaderAuthoringSchema schema,
        AuthoringInspectorState inspectorState,
        AuthoringToolState tools)
    {
        ImGui.InputText("Locale", ref tools.Locale, 16u);
        ImGui.InputTextWithHint("##LocaleFilter", "Search localized and raw identities", ref tools.LocaleSearch, 128u);
        int matches = 0;
        foreach (ShaderAuthoringNode node in schema.DeclarationOrder)
        {
            if (string.IsNullOrWhiteSpace(tools.LocaleSearch) ||
                AuthoringLocales.SearchTerms(tools.Locale, node)
                    .Any(term => term.Contains(tools.LocaleSearch, StringComparison.OrdinalIgnoreCase)))
                matches++;
        }
        ImGui.TextUnformatted($"{matches} localized/raw search match(es). Missing keys fall back to source labels.");

        MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
        ImGui.InputTextWithHint("##NoteProperty", "Semantic property ID (blank = material)", ref tools.NoteSemanticId, 256u);
        string key = string.IsNullOrWhiteSpace(tools.NoteSemanticId) ? schema.Root.SemanticId : tools.NoteSemanticId;
        if (tools.LastNoteKey != key)
        {
            metadata.Notes.TryGetValue(key, out string? note);
            tools.NoteText = note ?? string.Empty;
            tools.LastNoteKey = key;
        }
        ImGui.InputTextMultiline("##NoteText", ref tools.NoteText, 4096u, new Vector2(-1.0f, 84.0f));
        if (ImGui.SmallButton("Save note"))
        {
            MaterialAuthoringTransaction transaction = new("Set material authoring note");
            bool hadNote = metadata.Notes.TryGetValue(key, out string? previousNote);
            transaction.AddStructural(
                material,
                "Local note",
                () =>
                {
                    if (string.IsNullOrWhiteSpace(tools.NoteText))
                        metadata.Notes.Remove(key);
                    else
                        metadata.Notes[key] = tools.NoteText;
                },
                () =>
                {
                    if (hadNote)
                        metadata.Notes[key] = previousNote!;
                    else
                        metadata.Notes.Remove(key);
                });
            transaction.TryExecute(out _);
            inspectorState.Status = "Local note saved.";
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Find notes"))
        {
            inspectorState.Search = tools.LocaleSearch;
            tools.Status = $"{metadata.Notes.Count} local note(s). Note content remains local and is never logged.";
        }
        ImGui.TextDisabled("RemoteMessage, version checks, and remote images are preserved inactive; opening this inspector never fetches them.");
    }

    private static void DrawOptimizerWorkspace(XRMaterial material, AuthoringToolState tools)
    {
        UberMaterialVariantStatus status = material.UberVariantStatus;
        string authoredState = status.Stage == EUberMaterialVariantStage.None ? "Authored" : status.Stage.ToString();
        ImGui.TextUnformatted($"State: {authoredState}");
        ImGui.TextUnformatted($"Requested key: 0x{status.RequestedVariantHash:x16}");
        ImGui.TextUnformatted($"Active key: 0x{status.ActiveVariantHash:x16}");
        ImGui.TextUnformatted($"Uniforms: {status.UniformCount}, samplers: {status.SamplerCount}, source: {status.GeneratedSourceLength:N0} chars");
        if (!string.IsNullOrWhiteSpace(status.FailureReason))
            ImGui.TextColored(AuthoringUnsupportedColor, status.FailureReason);
        if (ImGui.SmallButton("Prepare / prewarm"))
            tools.Status = material.PrepareUberVariantImmediately()
                ? "Variant is ready."
                : status.FailureReason ?? "Variant preparation failed; the previous usable variant remains active.";
        ImGui.SameLine();
        if (ImGui.SmallButton("Unprepare"))
        {
            material.ClearUberVariantRuntimeState();
            tools.Status = "Prepared state cleared; authored values were retained.";
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Rebuild"))
        {
            material.RequestUberVariantRebuild();
            tools.Status = "Variant rebuild requested.";
        }
        ImGui.TextDisabled("DoNotLock fields remain dynamic; DoNotAnimate fields are excluded from automatic animation marking.");
        if (!string.IsNullOrWhiteSpace(tools.Status))
            ImGui.TextWrapped(tools.Status);
    }

    private static bool TryDrawAuthoringWidget(
        XRMaterial material,
        ShaderAuthoringNode node,
        ShaderVar parameter,
        ShaderUiProperty property,
        Dictionary<string, ShaderVar> parameterLookup)
    {
        string? widget = node.WidgetId;
        if (widget is null)
            return false;

        ShaderAuthoringAttribute? sourceAttribute = node.Attributes.FirstOrDefault(attribute =>
            string.Equals(attribute.Name, widget, StringComparison.OrdinalIgnoreCase));
        if (widget is "Enum" or "KeywordEnum" or "ThryWideEnum")
            return DrawAuthoringEnum(material, parameter, sourceAttribute?.Arguments);
        if (widget is "Toggle" or "ToggleUI" or "MaterialToggle" or "ThryToggle" or "ThryToggleUI" or "lilToggleLeft")
            return DrawAuthoringToggle(material, parameter);
        if (widget == "ButtonVector" && parameter is ShaderVector4 buttonVector)
            return DrawButtonVector(material, buttonVector, sourceAttribute?.Arguments);
        if (widget == "ThryMultiFloatButtons")
            return DrawReferencedFloatButtons(material, node, parameterLookup);
        if (widget is "ThryMask" && parameter is ShaderInt mask)
            return DrawChannelMask(material, mask);

        if (ShaderAuthoringWidgetRegistry.TryResolve(widget, out _))
        {
            DrawUberShaderParameterControl(material, parameter, property);
            return true;
        }
        return false;
    }

    private static bool DrawAuthoringEnum(XRMaterial material, ShaderVar parameter, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || !TryGetNumericValue(parameter, out double numeric))
            return false;
        string[] tokens = arguments.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
            return false;
        string current = numeric.ToString(CultureInfo.InvariantCulture);
        for (int index = 0; index + 1 < tokens.Length; index += 2)
            if (double.TryParse(tokens[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double candidate) &&
                Math.Abs(candidate - numeric) < 0.000001)
                current = tokens[index];
        if (!ImGui.BeginCombo("##Enum", current))
            return true;
        for (int index = 0; index + 1 < tokens.Length; index += 2)
        {
            if (!double.TryParse(tokens[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;
            if (ImGui.Selectable($"{tokens[index]} ({tokens[index + 1]})"))
                SetNumericValue(material, parameter, value);
        }
        ImGui.EndCombo();
        return true;
    }

    private static bool DrawAuthoringToggle(XRMaterial material, ShaderVar parameter)
    {
        if (!TryGetNumericValue(parameter, out double numeric))
            return false;
        bool enabled = Math.Abs(numeric) > 0.000001;
        if (ImGui.Checkbox("##Toggle", ref enabled))
            SetNumericValue(material, parameter, enabled ? 1.0 : 0.0);
        return true;
    }

    private static bool DrawButtonVector(
        XRMaterial material,
        ShaderVector4 parameter,
        string? arguments)
    {
        string[] labels = (arguments ?? "X,Y,Z,W").Split(',', StringSplitOptions.TrimEntries);
        Vector4 value = parameter.Value;
        for (int component = 0; component < 4; component++)
        {
            string label = component < labels.Length ? labels[component] : "NA";
            if (component > 0)
                ImGui.SameLine();
            using (new ImGuiDisabledScope(label.Equals("NA", StringComparison.OrdinalIgnoreCase)))
            if (ImGui.SmallButton($"{label}##{component}"))
            {
                value[component] = Math.Abs(value[component]) > 0.000001f ? 0.0f : 1.0f;
                parameter.SetValue(value);
                material.MarkDirty();
            }
        }
        return true;
    }

    private static bool DrawReferencedFloatButtons(
        XRMaterial material,
        ShaderAuthoringNode node,
        Dictionary<string, ShaderVar> parameterLookup)
    {
        bool rendered = false;
        foreach (ShaderAuthoringNode referenced in node.ReferencedProperties)
        {
            string? propertyName = referenced.ManifestProperty?.Name;
            if (propertyName is null ||
                !parameterLookup.TryGetValue(propertyName, out ShaderVar? parameter) ||
                !TryGetNumericValue(parameter, out double numeric))
                continue;
            if (rendered)
                ImGui.SameLine();
            bool enabled = Math.Abs(numeric) > 0.000001;
            if (ImGui.Checkbox($"{referenced.DisplayName}##{referenced.SemanticId}", ref enabled))
                SetNumericValue(material, parameter, enabled ? 1.0 : 0.0);
            rendered = true;
        }
        return rendered;
    }

    private static bool DrawChannelMask(XRMaterial material, ShaderInt parameter)
    {
        int value = parameter.Value;
        string[] labels = ["R", "G", "B", "A"];
        for (int component = 0; component < 4; component++)
        {
            if (component > 0)
                ImGui.SameLine();
            bool enabled = (value & (1 << component)) != 0;
            if (ImGui.Checkbox($"{labels[component]}##mask{component}", ref enabled))
            {
                value = enabled ? value | (1 << component) : value & ~(1 << component);
                parameter.SetValue(value);
                material.MarkDirty();
            }
        }
        return true;
    }

    private static bool TryGetNumericValue(ShaderVar parameter, out double value)
    {
        switch (parameter)
        {
            case ShaderFloat number:
                value = number.Value;
                return true;
            case ShaderInt number:
                value = number.Value;
                return true;
            case ShaderUInt number:
                value = number.Value;
                return true;
            case ShaderBool boolean:
                value = boolean.Value ? 1.0 : 0.0;
                return true;
            default:
                value = 0.0;
                return false;
        }
    }

    private static void SetNumericValue(XRMaterial material, ShaderVar parameter, double value)
    {
        switch (parameter)
        {
            case ShaderFloat number:
                number.SetValue((float)value);
                break;
            case ShaderInt number:
                number.SetValue((int)Math.Round(value));
                break;
            case ShaderUInt number:
                number.SetValue((uint)Math.Max(0.0, Math.Round(value)));
                break;
            case ShaderBool boolean:
                boolean.SetValue(Math.Abs(value) > 0.000001);
                break;
            default:
                return;
        }
        material.MarkDirty();
    }

    private enum EAuthoringWorkspace
    {
        Rendering,
        Presets,
        TexturePacker,
        GradientCurve,
        TextureArray,
        Decal,
        Utilities,
        LocaleNotes,
        Optimizer,
    }

    private sealed class AuthoringToolState
    {
        public bool Open;
        public bool LocaleInitialized;
        public bool VariantChanged;
        public bool ConfirmConversionReset;
        public EAuthoringWorkspace Workspace;
        public string? SourceProperty;
        public string? Status;
        public string PresetRoot = Path.Combine(
            Engine.Assets?.GameAssetsPath ?? Path.Combine(Directory.GetCurrentDirectory(), "Assets"),
            "MaterialPresets");
        public string PresetSearch = string.Empty;
        public MaterialAuthoringClipboardPayload? PendingPayload;
        public int PackWidth = 1024;
        public int PackHeight = 1024;
        public bool PackLinear = true;
        public readonly float[] PackConstants = [0.0f, 0.0f, 0.0f, 1.0f];
        public readonly bool[] PackInvert = new bool[4];
        public Vector4[]? PackedPreview;
        public readonly MaterialGradientAsset Gradient = new();
        public readonly MaterialCurveAsset Curve = new();
        public readonly MaterialTextureArrayRecipe Array = new();
        public string ArraySource = string.Empty;
        public int ArrayWidth = 1024;
        public int ArrayHeight = 1024;
        public int ArrayMipCount = 1;
        public bool ArrayLinear = true;
        public string ArrayFormat = "RGBA8";
        public string ArraySemantic = "Color";
        public DecalTransform Decal = new(
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.One,
            Vector2.Zero,
            Vector2.One,
            0.0f,
            false);
        public string LinkSemanticId = string.Empty;
        public string LinkName = "Material Link";
        public string LinkJson = string.Empty;
        public string Locale = "en";
        public string LocaleSearch = string.Empty;
        public string NoteSemanticId = string.Empty;
        public string NoteText = string.Empty;
        public string? LastNoteKey;
    }
}
