using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ImGuiNET;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static readonly Vector4 UberHeaderColor = new(0.85f, 0.62f, 0.29f, 1.0f);
    private static readonly Vector4 UberFeatureEnabledColor = new(0.32f, 0.82f, 0.52f, 1.0f);
    private static readonly Vector4 UberFeatureDisabledColor = new(0.82f, 0.42f, 0.34f, 1.0f);
    private static readonly Vector4 UberFeatureRequestedColor = new(0.90f, 0.74f, 0.28f, 1.0f);
    private static readonly Vector4 UberFeatureCompilingColor = new(0.33f, 0.67f, 0.89f, 1.0f);
    private static readonly Vector4 UberFeatureUnavailableColor = new(0.56f, 0.56f, 0.56f, 1.0f);
    private static readonly HashSet<string> UberImportVariantUnavailableFeatureIds = new(StringComparer.Ordinal)
    {
        "advanced-specular",
        "backface",
        "color-adjustments",
        "detail-textures",
        "dissolve",
        "emission",
        "flipbook",
        "glitter",
        "matcap",
        "material-ao",
        "outline",
        "parallax",
        "rim-lighting",
        "shadow-masks",
        "stylized-shading",
        "subsurface",
    };
    private static readonly ConditionalWeakTable<XRMaterial, PendingUberFeatureResolution> PendingUberFeatureResolutions = new();

    private static void DrawUberInspector(XRMaterial material)
    {
        if (!TryGetUberMaterialManifest(material, out XRShader? activeFragmentShader, out XRShader? fragmentShader, out ShaderUiManifest? manifest) ||
            activeFragmentShader is null ||
            fragmentShader is null ||
            manifest is null)
            return;

        material.EnsureUberStateInitialized();

        XRShader uberFragmentShader = fragmentShader!;
        ShaderUiManifest uberManifest = manifest!;
        MaterialInspectorUiState uiState = MaterialUiStates.GetValue(material, static _ => new MaterialInspectorUiState());

        if (!ImGui.CollapsingHeader("Uber Shader", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var visibleProperties = uberManifest.Properties
            .Where(static x => IsAuthorableUberProperty(x))
            .ToArray();

        var featureLookup = uberManifest.FeatureLookup;
        HashSet<string> unavailableFeatureIds = ResolveUnavailableUberFeatureIds(activeFragmentShader, uberManifest);
        var categoryNames = visibleProperties
            .Select(x => ResolveCategoryName(x, featureLookup))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var parameterLookup = BuildParameterLookup(material);
        var samplerLookup = ResolveSamplerBindings(material, CollectSamplerDefinitions(material))
            .Where(static x => x.EngineBinding is null)
            .ToDictionary(static x => x.Sampler.Name, StringComparer.Ordinal);

        int enabledFeatureCount = uberManifest.Features.Count(x => material.IsUberFeatureEnabled(x.Id, x.DefaultEnabled));
        ulong requestedVariantHash = material.RequestedUberVariant.VariantHash;
        ulong activeVariantHash = material.ActiveUberVariant.VariantHash;
        if (requestedVariantHash == 0)
            requestedVariantHash = ComputeStableSourceHash(uberFragmentShader.Source?.Text ?? string.Empty);

        UberShaderVariantTelemetry.Snapshot telemetrySnapshot = UberShaderVariantTelemetry.GetSnapshot();

        ImGui.TextColored(UberHeaderColor, $"Feature Modules: {uberManifest.Features.Count} | Authorable Properties: {visibleProperties.Length}");
        ImGui.TextDisabled($"Enabled: {enabledFeatureCount} | Requested: 0x{requestedVariantHash:X16} | Active: 0x{activeVariantHash:X16} | Async Compile: {(uberFragmentShader.GenerateAsync ? "On" : "Off")}");
        if (unavailableFeatureIds.Count > 0)
        {
            string unavailableModules = string.Join(", ",
                uberManifest.Features
                    .Where(feature => unavailableFeatureIds.Contains(feature.Id))
                    .Select(feature => feature.DisplayName));
            ImGui.TextColored(ValidationWarningColor, $"Active shader trims {unavailableFeatureIds.Count} module(s): {unavailableModules}");
            ImGui.TextDisabled("Switch the material to the full Uber fragment to author those modules.");
        }
        DrawUberVariantStatus(material);
        ImGui.TextDisabled($"Session: requests {telemetrySnapshot.RequestCount} | successes {telemetrySnapshot.SuccessCount} | failures {telemetrySnapshot.FailureCount} | cache {telemetrySnapshot.CacheHitRate:P0} | avg prepare {telemetrySnapshot.AveragePreparationMilliseconds:0.##} ms | avg adopt {telemetrySnapshot.AverageAdoptionMilliseconds:0.##} ms | avg compile {telemetrySnapshot.AverageCompileMilliseconds:0.##} ms | avg link {telemetrySnapshot.AverageLinkMilliseconds:0.##} ms");
        if (uberManifest.ValidationIssues.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ValidationWarningColor, $"Validation: {uberManifest.ValidationIssues.Count} issue(s)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                foreach (ShaderUiValidationIssue issue in uberManifest.ValidationIssues.Take(8))
                    ImGui.TextUnformatted($"L{issue.LineNumber}: {issue.Message}");
                if (uberManifest.ValidationIssues.Count > 8)
                    ImGui.TextDisabled("Open the shader inspector for the full validation list.");
                ImGui.EndTooltip();
            }
        }

        DrawUberBulkActions(material, uberManifest, visibleProperties);
        bool toolbarVariantChanged = false;
        DrawUberFeatureToolbar(material, uberManifest, featureLookup, unavailableFeatureIds, ref toolbarVariantChanged);

        bool showDisabled = uiState.ShowDisabledUberFeatures;
        if (ImGui.Checkbox("Show Disabled Modules", ref showDisabled))
            uiState.ShowDisabledUberFeatures = showDisabled;

        ShaderUiProperty[] filteredProperties = visibleProperties
            .Where(property => ShouldDisplayUberProperty(material, featureLookup, property, uiState.ShowDisabledUberFeatures))
            .ToArray();

        foreach (ShaderUiFeature feature in uberManifest.Features)
            DrawPendingUberFeatureResolution(material, feature, ref toolbarVariantChanged);

        foreach (string rawCategory in categoryNames)
        {
            string category = rawCategory!;
            var categoryProperties = filteredProperties
                .Where(x => string.Equals(ResolveCategoryName(x, featureLookup), category, StringComparison.Ordinal))
                .ToArray();

            if (categoryProperties.Length == 0)
                continue;

            if (!ImGui.TreeNodeEx($"{category}##UberCategory_{category}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            DrawUberCategoryProperties(material, uberFragmentShader, featureLookup, unavailableFeatureIds, parameterLookup, samplerLookup, categoryProperties);
            ImGui.TreePop();
        }

        ImGui.Separator();

        if (toolbarVariantChanged)
            material.RequestUberVariantRebuild();
    }

    private static bool ShouldDisplayUberProperty(
        XRMaterial material,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        ShaderUiProperty property,
        bool showDisabledFeatures)
    {
        if (showDisabledFeatures || string.IsNullOrWhiteSpace(property.FeatureId))
            return true;

        if (!featureLookup.TryGetValue(property.FeatureId, out ShaderUiFeature? feature))
            return true;

        return feature.Required || material.IsUberFeatureEnabled(feature.Id, feature.DefaultEnabled);
    }

    private static bool TryGetUberMaterialManifest(XRMaterial material, out XRShader? activeFragmentShader, out XRShader? fragmentShader, out ShaderUiManifest? manifest)
    {
        activeFragmentShader = material.GetShader(EShaderType.Fragment);
        if (!material.TryGetUberMaterialState(out fragmentShader, out ShaderUiManifest resolvedManifest) ||
            activeFragmentShader is null ||
            fragmentShader is null)
        {
            manifest = null;
            return false;
        }

        manifest = resolvedManifest;
        return resolvedManifest.Properties.Count > 0;
    }

    private static void DrawUberCategoryProperties(
        XRMaterial material,
        XRShader fragmentShader,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        IReadOnlySet<string> unavailableFeatureIds,
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        IReadOnlyList<ShaderUiProperty> categoryProperties)
    {
        string? currentFeatureId = null;
        List<ShaderUiProperty> bucket = [];

        void FlushBucket()
        {
            if (bucket.Count == 0)
                return;

            DrawUberPropertyBucket(material, fragmentShader, featureLookup, unavailableFeatureIds, parameterLookup, samplerLookup, currentFeatureId, bucket);
            bucket.Clear();
        }

        foreach (ShaderUiProperty rawProperty in categoryProperties)
        {
            ShaderUiProperty property = rawProperty!;
            if (!string.Equals(property.FeatureId, currentFeatureId, StringComparison.Ordinal))
            {
                FlushBucket();
                currentFeatureId = property.FeatureId;
            }

            bucket.Add(property);
        }

        FlushBucket();
    }

    private static void DrawUberPropertyBucket(
        XRMaterial material,
        XRShader fragmentShader,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        IReadOnlySet<string> unavailableFeatureIds,
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        string? featureId,
        IReadOnlyList<ShaderUiProperty> properties)
    {
        bool variantChanged = false;
        bool constantLiteralChanged = false;
        if (featureId is not null && featureLookup.TryGetValue(featureId, out ShaderUiFeature? feature))
        {
            bool enabled = material.IsUberFeatureEnabled(feature.Id, feature.DefaultEnabled);
            bool available = !unavailableFeatureIds.Contains(feature.Id);
            int animatedPropertyCount = properties.Count(x => !x.IsSampler && material.GetUberPropertyMode(x.Name, x.DefaultMode, x.IsSampler) == EShaderUiPropertyMode.Animated);
            bool featureAuthored = material.UberAuthoredState.GetFeature(feature.Id)?.ExplicitlyAuthored == true;
            ImGui.PushID(feature.Id);
            if (!available)
            {
                ImGui.TextColored(UberFeatureUnavailableColor, feature.DisplayName);
                ImGui.SameLine();
                ImGui.TextDisabled("(unavailable in active shader)");
            }
            else if (feature.Required)
            {
                ImGui.TextColored(UberFeatureEnabledColor, feature.DisplayName);
                ImGui.SameLine();
                ImGui.TextDisabled("(required)");
            }
            else
            {
                bool toggled = enabled;
                if (ImGui.Checkbox(feature.DisplayName, ref toggled) && toggled != enabled)
                {
                    PendingUberFeatureResolution? pendingResolution = toggled
                        ? BuildPendingUberFeatureResolution(material, featureLookup, feature)
                        : null;

                    if (pendingResolution is null)
                    {
                        ClearPendingUberFeatureResolution(material, feature.Id);
                        variantChanged |= material.SetUberFeatureEnabled(feature.Id, toggled);
                    }
                    else
                    {
                        SetPendingUberFeatureResolution(material, pendingResolution);
                    }
                }

                ImGui.SameLine();
                ImGui.TextColored(toggled ? UberFeatureEnabledColor : UberFeatureDisabledColor, toggled ? "enabled" : "disabled");
            }

            if (ImGui.IsItemHovered())
            {
                string tooltip = !available
                    ? "Unavailable in the active shader variant."
                    : feature.Tooltip ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(tooltip))
                    ImGui.SetTooltip(tooltip);
            }

            if (feature.Cost != EShaderUiFeatureCost.Unspecified)
            {
                ImGui.SameLine();
                string stateLabel = !available
                    ? "unsupported"
                    : featureAuthored ? "authored" : "default";
                ImGui.TextDisabled($"cost: {feature.Cost.ToString().ToLowerInvariant()} | animated: {animatedPropertyCount} | {stateLabel}");
            }

            if (available)
                DrawPendingUberFeatureResolution(material, feature, ref variantChanged);

            ImGui.PopID();
        }

        if (!ImGui.BeginTable($"UberPropertyTable_{featureId ?? "base"}", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.28f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130.0f);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.32f);
        ImGui.TableSetupColumn("Update", ImGuiTableColumnFlags.WidthStretch, 0.18f);
        ImGui.TableHeadersRow();

        foreach (ShaderUiProperty rawProperty in properties)
        {
            ShaderUiProperty property = rawProperty!;
            ShaderVar? parameter = property.IsSampler ? null : FindParameter(parameterLookup, property.Name);
            samplerLookup.TryGetValue(property.Name, out SamplerBindingEntry? samplerBinding);
            bool propertyAuthored = material.UberAuthoredState.GetProperty(property.Name) is not null || parameter is not null || samplerBinding?.AssignedTexture is not null;
            bool featureAvailable = featureId is null || !unavailableFeatureIds.Contains(featureId);
            bool featureEnabled = featureAvailable && (featureId is null ||
                (featureLookup.TryGetValue(featureId, out ShaderUiFeature? bucketFeature) && material.IsUberFeatureEnabled(bucketFeature.Id, bucketFeature.DefaultEnabled)) ||
                (featureLookup.TryGetValue(featureId, out ShaderUiFeature? requiredFeature) && requiredFeature.Required));
            EShaderUiPropertyMode propertyMode = material.GetUberPropertyMode(property.Name, property.DefaultMode, property.IsSampler);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(propertyAuthored ? UberFeatureRequestedColor : UberFeatureCompilingColor, propertyAuthored ? "*" : "o");
            ImGui.SameLine();
            ImGui.TextUnformatted(property.DisplayName);
            if (!string.IsNullOrWhiteSpace(property.Tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(property.Tooltip);
            ImGui.SameLine();
            ImGui.TextDisabled(propertyAuthored ? "authored" : "inherited");
            DrawUberPropertyContextMenu(material, property, parameter, samplerBinding, propertyMode, ref variantChanged, ref constantLiteralChanged);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(property.ArraySize > 0 ? $"{property.GlslType}[{property.ArraySize}]" : property.GlslType);

            ImGui.TableSetColumnIndex(2);
            if (property.IsSampler)
            {
                ImGui.TextDisabled("Texture");
            }
            else
            {
                bool animated = propertyMode == EShaderUiPropertyMode.Animated;
                ImGui.PushID($"UberMode_{property.Name}");
                if (ImGui.Checkbox("##Animated", ref animated))
                {
                    EShaderUiPropertyMode nextMode = animated ? EShaderUiPropertyMode.Animated : EShaderUiPropertyMode.Static;
                    variantChanged |= material.SetUberPropertyMode(property.Name, nextMode);
                    propertyMode = nextMode;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(animated ? "Animated" : "Constant");
                ImGui.PopID();
            }

            ImGui.TableSetColumnIndex(3);
            using (new ImGuiDisabledScope(!featureEnabled))
            if (property.IsSampler)
            {
                if (samplerLookup.TryGetValue(property.Name, out SamplerBindingEntry? binding) && binding is not null)
                {
                    ImGui.PushID($"UberSampler_{property.Name}");
                    DrawSamplerTextureField(material, binding);
                    ImGui.PopID();
                }
                else
                {
                    ImGui.TextDisabled("Sampler not bound");
                }
            }
            else
            {
                object? previousValue = parameter?.GenericValue;
                if (parameter is null)
                {
                    if (ImGui.SmallButton($"Create##Uber_{property.Name}"))
                    {
                        if (TryCreateMaterialParameter(material, property.GlslType, property.Name))
                            parameterLookup[property.Name] = material.Parameters[^1];
                    }
                }
                else
                {
                    ImGui.PushID($"UberProperty_{property.Name}");
                    DrawUberShaderParameterControl(material, parameter, property);
                    ImGui.PopID();

                    if (propertyMode == EShaderUiPropertyMode.Static && featureEnabled && !Equals(previousValue, parameter.GenericValue))
                        constantLiteralChanged |= material.RefreshUberPropertyStaticLiteral(property.Name);
                }
            }

            ImGui.TableSetColumnIndex(4);
            if (property.IsSampler)
            {
                if (samplerLookup.TryGetValue(property.Name, out SamplerBindingEntry? binding) && binding is not null)
                    DrawSamplerDriveCell(material, binding);
                else
                    ImGui.TextDisabled("Texture slot missing");
            }
            else
            {
                if (!featureAvailable)
                    ImGui.TextDisabled("Unavailable");
                else if (propertyMode == EShaderUiPropertyMode.Static)
                    ImGui.TextDisabled(featureEnabled ? "Rebuild variant" : "Feature off");
                else
                    DrawParameterDriveCell(material, parameter, property.Name, property.GlslType);
            }
        }

        ImGui.EndTable();
        ImGui.Spacing();

        if (variantChanged)
            material.RequestUberVariantRebuild();
        else if (constantLiteralChanged)
            material.RequestUberVariantRebuildDebounced();
    }

    private static void DrawUberPropertyContextMenu(
        XRMaterial material,
        ShaderUiProperty property,
        ShaderVar? parameter,
        SamplerBindingEntry? samplerBinding,
        EShaderUiPropertyMode propertyMode,
        ref bool variantChanged,
        ref bool constantLiteralChanged)
    {
        if (!ImGui.BeginPopupContextItem($"UberPropertyContext_{property.Name}"))
            return;

        if (!property.IsSampler && parameter is not null && TryGetParameterPath(material, parameter, out string parameterPath))
        {
            if (ImGui.MenuItem("Copy Anim Path"))
                ImGui.SetClipboardText(parameterPath);
        }

        if (property.IsSampler && samplerBinding is not null)
        {
            int slotIndex = samplerBinding.MaterialTextureSlot ?? samplerBinding.FallbackTextureSlot;
            if (ImGui.MenuItem("Copy Anim Path"))
                ImGui.SetClipboardText($"Textures[{slotIndex}]");
        }

        if (!property.IsSampler)
        {
            bool canSwitchToAnimated = propertyMode != EShaderUiPropertyMode.Animated;
            if (ImGui.MenuItem("Convert To Animated", null, false, canSwitchToAnimated))
            {
                variantChanged |= material.SetUberPropertyMode(property.Name, EShaderUiPropertyMode.Animated);
            }

            bool canSwitchToConstant = propertyMode != EShaderUiPropertyMode.Static;
            if (ImGui.MenuItem("Convert To Constant", null, false, canSwitchToConstant))
            {
                variantChanged |= material.SetUberPropertyMode(property.Name, EShaderUiPropertyMode.Static);
                constantLiteralChanged |= material.RefreshUberPropertyStaticLiteral(property.Name);
            }
        }

        if (parameter is not null && TrySerializeShaderParameterValue(parameter, out string serializedValue) && ImGui.MenuItem("Copy Value"))
            ImGui.SetClipboardText(serializedValue);

        if (parameter is not null && ImGui.MenuItem("Paste Value", null, false, CanApplyShaderParameterClipboard(parameter, ImGui.GetClipboardText())))
        {
            if (TryApplyShaderParameterClipboard(material, parameter, ImGui.GetClipboardText()) && propertyMode == EShaderUiPropertyMode.Static)
                constantLiteralChanged |= material.RefreshUberPropertyStaticLiteral(property.Name);
        }

        if (ImGui.MenuItem("Reset To Default"))
        {
            if (!property.IsSampler)
                RemoveMaterialParameter(material, property.Name);

            if (property.IsSampler && samplerBinding is not null && samplerBinding.EngineBinding is null)
                AssignTextureToBinding(material, samplerBinding, null);

            variantChanged |= material.ResetUberPropertyOverride(property.Name);
        }

        ImGui.EndPopup();
    }

    private static bool IsAuthorableUberProperty(ShaderUiProperty property)
    {
        if (IsEngineUniform(property.Name, out _))
            return false;

        return property.HasExplicitMetadata &&
               (property.Name.StartsWith("_", StringComparison.Ordinal) ||
            property.Name.Equals("AlphaCutoff", StringComparison.Ordinal));
    }

    private static string ResolveCategoryName(ShaderUiProperty property, IReadOnlyDictionary<string, ShaderUiFeature> featureLookup)
    {
        if (!string.IsNullOrWhiteSpace(property.Category))
            return property.Category!;

        if (property.FeatureId is not null && featureLookup.TryGetValue(property.FeatureId, out ShaderUiFeature? feature) && !string.IsNullOrWhiteSpace(feature.Category))
            return feature.Category!;

        return "General";
    }

    private static void DrawUberFeatureToolbar(
        XRMaterial material,
        ShaderUiManifest manifest,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        IReadOnlySet<string> unavailableFeatureIds,
        ref bool variantChanged)
    {
        int columnCount = Math.Max(1, Math.Min(4, (int)(ImGui.GetContentRegionAvail().X / 160.0f)));
        if (!ImGui.BeginTable("UberFeatureToolbar", columnCount, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            return;

        foreach (ShaderUiFeature feature in manifest.Features)
        {
            bool enabled = material.IsUberFeatureEnabled(feature.Id, feature.DefaultEnabled);
            bool available = !unavailableFeatureIds.Contains(feature.Id);
            Vector4 buttonColor = !available
                ? UberFeatureUnavailableColor
                : enabled ? UberFeatureEnabledColor : UberFeatureDisabledColor;

            ImGui.TableNextColumn();
            ImGui.PushID($"ToolbarFeature_{feature.Id}");
            ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor * 1.08f);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, buttonColor * 0.92f);

            using (new ImGuiDisabledScope(!available))
            {
                if (ImGui.Button(feature.DisplayName, new Vector2(-1.0f, 0.0f)) && !feature.Required)
                {
                    bool toggled = !enabled;
                    PendingUberFeatureResolution? pendingResolution = toggled
                        ? BuildPendingUberFeatureResolution(material, featureLookup, feature)
                        : null;

                    if (pendingResolution is null)
                    {
                        ClearPendingUberFeatureResolution(material, feature.Id);
                        variantChanged |= material.SetUberFeatureEnabled(feature.Id, toggled);
                    }
                    else
                    {
                        SetPendingUberFeatureResolution(material, pendingResolution);
                    }
                }
            }

            if (feature.Required)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("req");
            }
            else if (!available)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("n/a");
            }

            if (ImGui.IsItemHovered())
            {
                string status = !available
                    ? "Unavailable in active shader"
                    : enabled ? "Enabled" : "Disabled";
                string tooltip = string.IsNullOrWhiteSpace(feature.Tooltip)
                    ? status
                    : $"{feature.Tooltip}\n{status}";
                ImGui.SetTooltip(tooltip);
            }

            ImGui.PopStyleColor(3);
            ImGui.PopID();
        }

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static void DrawUberShaderParameterControl(XRMaterial material, ShaderVar parameter, ShaderUiProperty property)
    {
        if (TryDrawUberToggleControl(material, parameter, property) ||
            TryDrawUberEnumControl(material, parameter, property) ||
            TryDrawUberRangeControl(material, parameter, property))
            return;

        DrawShaderParameterControl(material, parameter);
    }

    private static bool TryDrawUberRangeControl(XRMaterial material, ShaderVar parameter, ShaderUiProperty property)
    {
        if (!TryParseShaderUiRange(property.Range, out ShaderUiNumericRange range))
            return false;

        switch (parameter)
        {
            case ShaderFloat shaderFloat:
                {
                    float value = shaderFloat.Value;
                    ImGui.SetNextItemWidth(-1.0f);
                    if (ImGui.SliderFloat("##Range", ref value, (float)range.Min, (float)range.Max))
                    {
                        shaderFloat.SetValue(value);
                        material.MarkDirty();
                    }

                    return true;
                }
            case ShaderInt shaderInt:
                {
                    int value = shaderInt.Value;
                    int min = (int)Math.Round(range.Min);
                    int max = (int)Math.Round(range.Max);
                    ImGui.SetNextItemWidth(-1.0f);
                    if (ImGui.SliderInt("##Range", ref value, min, max))
                    {
                        shaderInt.SetValue(value);
                        material.MarkDirty();
                    }

                    return true;
                }
            case ShaderUInt shaderUInt:
                {
                    int value = (int)Math.Clamp(shaderUInt.Value, 0u, int.MaxValue);
                    int min = (int)Math.Max(0.0d, Math.Round(range.Min));
                    int max = (int)Math.Max(min, Math.Round(range.Max));
                    ImGui.SetNextItemWidth(-1.0f);
                    if (ImGui.SliderInt("##Range", ref value, min, max))
                    {
                        shaderUInt.SetValue((uint)Math.Max(0, value));
                        material.MarkDirty();
                    }

                    return true;
                }
            default:
                return false;
        }
    }

    private static bool TryDrawUberToggleControl(XRMaterial material, ShaderVar parameter, ShaderUiProperty property)
    {
        if (!property.IsToggle)
            return false;

        switch (parameter)
        {
            case ShaderBool shaderBool:
                {
                    bool value = shaderBool.Value;
                    if (ImGui.Checkbox("##Toggle", ref value))
                    {
                        shaderBool.SetValue(value);
                        material.MarkDirty();
                    }

                    return true;
                }
            case ShaderFloat shaderFloat:
                {
                    bool value = shaderFloat.Value >= 0.5f;
                    if (ImGui.Checkbox("##Toggle", ref value))
                    {
                        shaderFloat.SetValue(value ? 1.0f : 0.0f);
                        material.MarkDirty();
                    }

                    return true;
                }
            case ShaderInt shaderInt:
                {
                    bool value = shaderInt.Value != 0;
                    if (ImGui.Checkbox("##Toggle", ref value))
                    {
                        shaderInt.SetValue(value ? 1 : 0);
                        material.MarkDirty();
                    }

                    return true;
                }
            case ShaderUInt shaderUInt:
                {
                    bool value = shaderUInt.Value != 0;
                    if (ImGui.Checkbox("##Toggle", ref value))
                    {
                        shaderUInt.SetValue(value ? 1u : 0u);
                        material.MarkDirty();
                    }

                    return true;
                }
            default:
                return false;
        }
    }

    private static bool TryDrawUberEnumControl(XRMaterial material, ShaderVar parameter, ShaderUiProperty property)
    {
        ShaderUiEnumOption[] options = ParseShaderUiEnumOptions(property.EnumOptions);
        if (options.Length == 0)
            return false;

        switch (parameter)
        {
            case ShaderFloat shaderFloat:
                DrawUberFloatEnumControl(material, shaderFloat, options);
                return true;
            case ShaderInt shaderInt:
                DrawUberIntEnumControl(material, shaderInt, options);
                return true;
            case ShaderUInt shaderUInt:
                DrawUberUIntEnumControl(material, shaderUInt, options);
                return true;
            case ShaderBool shaderBool:
                DrawUberBoolEnumControl(material, shaderBool, options);
                return true;
            default:
                return false;
        }
    }

    private static void DrawUberFloatEnumControl(XRMaterial material, ShaderFloat parameter, IReadOnlyList<ShaderUiEnumOption> options)
    {
        float value = parameter.Value;
        string currentLabel = ResolveShaderUiEnumLabel(options, value);

        ImGui.SetNextItemWidth(-1.0f);
        if (!ImGui.BeginCombo("##Enum", currentLabel))
            return;

        foreach (ShaderUiEnumOption option in options)
        {
            bool selected = Math.Abs(option.Value - value) <= 0.0001d;
            if (ImGui.Selectable(option.Label, selected) && !selected)
            {
                parameter.SetValue((float)option.Value);
                material.MarkDirty();
                value = (float)option.Value;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawUberIntEnumControl(XRMaterial material, ShaderInt parameter, IReadOnlyList<ShaderUiEnumOption> options)
    {
        int value = parameter.Value;
        string currentLabel = ResolveShaderUiEnumLabel(options, value);

        ImGui.SetNextItemWidth(-1.0f);
        if (!ImGui.BeginCombo("##Enum", currentLabel))
            return;

        foreach (ShaderUiEnumOption option in options)
        {
            int optionValue = (int)Math.Round(option.Value);
            bool selected = optionValue == value;
            if (ImGui.Selectable(option.Label, selected) && !selected)
            {
                parameter.SetValue(optionValue);
                material.MarkDirty();
                value = optionValue;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawUberUIntEnumControl(XRMaterial material, ShaderUInt parameter, IReadOnlyList<ShaderUiEnumOption> options)
    {
        uint value = parameter.Value;
        string currentLabel = ResolveShaderUiEnumLabel(options, value);

        ImGui.SetNextItemWidth(-1.0f);
        if (!ImGui.BeginCombo("##Enum", currentLabel))
            return;

        foreach (ShaderUiEnumOption option in options)
        {
            uint optionValue = (uint)Math.Max(0, Math.Round(option.Value));
            bool selected = optionValue == value;
            if (ImGui.Selectable(option.Label, selected) && !selected)
            {
                parameter.SetValue(optionValue);
                material.MarkDirty();
                value = optionValue;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawUberBoolEnumControl(XRMaterial material, ShaderBool parameter, IReadOnlyList<ShaderUiEnumOption> options)
    {
        bool value = parameter.Value;
        string currentLabel = ResolveShaderUiEnumLabel(options, value ? 1.0d : 0.0d);

        ImGui.SetNextItemWidth(-1.0f);
        if (!ImGui.BeginCombo("##Enum", currentLabel))
            return;

        foreach (ShaderUiEnumOption option in options)
        {
            bool optionValue = Math.Abs(option.Value) > 0.0001d;
            bool selected = optionValue == value;
            if (ImGui.Selectable(option.Label, selected) && !selected)
            {
                parameter.SetValue(optionValue);
                material.MarkDirty();
                value = optionValue;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static string ResolveShaderUiEnumLabel(IReadOnlyList<ShaderUiEnumOption> options, double value)
    {
        foreach (ShaderUiEnumOption option in options)
        {
            if (Math.Abs(option.Value - value) <= 0.0001d)
                return option.Label;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static ShaderUiEnumOption[] ParseShaderUiEnumOptions(string? rawOptions)
    {
        if (string.IsNullOrWhiteSpace(rawOptions))
            return [];

        List<ShaderUiEnumOption> options = [];
        foreach (string part in rawOptions.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = part.IndexOf(':');
            string rawValue = separatorIndex >= 0 ? part[..separatorIndex].Trim() : part.Trim();
            string label = separatorIndex >= 0 ? part[(separatorIndex + 1)..].Trim() : rawValue;
            if (!double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double parsedValue))
                continue;

            options.Add(new ShaderUiEnumOption(parsedValue, string.IsNullOrWhiteSpace(label) ? rawValue : label));
        }

        return [.. options];
    }

    private static bool TryParseShaderUiRange(string? rawRange, out ShaderUiNumericRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(rawRange))
            return false;

        string trimmed = rawRange.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal))
            return false;

        string[] parts = trimmed[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double min) ||
            !double.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double max))
        {
            return false;
        }

        range = new ShaderUiNumericRange(Math.Min(min, max), Math.Max(min, max));
        return true;
    }

    private static void DrawUberVariantStatus(XRMaterial material)
    {
        UberMaterialVariantStatus status = material.UberVariantStatus;
        if (status.Stage == EUberMaterialVariantStage.None)
            return;

        bool hasBackendSnapshot = TryGetUberBackendSnapshot(status, out UberShaderVariantTelemetry.BackendSnapshot backendSnapshot);
        double compileMilliseconds = status.CompileMilliseconds;
        double linkMilliseconds = status.LinkMilliseconds;
        string? failureReason = status.FailureReason;
        string stageLabel = status.Stage.ToString();
        Vector4 stageColor = status.Stage switch
        {
            EUberMaterialVariantStage.Active => UberFeatureEnabledColor,
            EUberMaterialVariantStage.Failed => UberFeatureDisabledColor,
            EUberMaterialVariantStage.Compiling => UberFeatureCompilingColor,
            _ => UberFeatureRequestedColor,
        };

        if (hasBackendSnapshot)
        {
            if (compileMilliseconds <= 0.0 && backendSnapshot.CompileMilliseconds > 0.0)
                compileMilliseconds = backendSnapshot.CompileMilliseconds;

            if (linkMilliseconds <= 0.0 && backendSnapshot.LinkMilliseconds > 0.0)
                linkMilliseconds = backendSnapshot.LinkMilliseconds;

            if (string.IsNullOrWhiteSpace(failureReason) && !string.IsNullOrWhiteSpace(backendSnapshot.FailureReason))
                failureReason = backendSnapshot.FailureReason;

            switch (backendSnapshot.Stage)
            {
                case UberShaderVariantTelemetry.BackendStage.Compiling:
                    stageLabel = "Backend Compile";
                    stageColor = UberFeatureCompilingColor;
                    break;
                case UberShaderVariantTelemetry.BackendStage.Linking:
                    stageLabel = "Backend Link";
                    stageColor = UberFeatureCompilingColor;
                    break;
                case UberShaderVariantTelemetry.BackendStage.Failed:
                    stageLabel = "Backend Failed";
                    stageColor = UberFeatureDisabledColor;
                    break;
            }
        }

        bool stale = status.RequestedVariantHash != 0 &&
            status.ActiveVariantHash != 0 &&
            status.RequestedVariantHash != status.ActiveVariantHash;

        DrawUberStatusBadge(stageLabel, stageColor);

        if (stale)
            DrawUberStatusBadge("Stale", ValidationWarningColor);

        ImGui.TextDisabled($"prepare {status.PreparationMilliseconds:0.##} ms | adopt {status.AdoptionMilliseconds:0.##} ms | compile {FormatTiming(compileMilliseconds)} | link {FormatTiming(linkMilliseconds)} | source {status.GeneratedSourceLength} B | uniforms {status.UniformCount} | samplers {status.SamplerCount} | cache {(status.CacheHit ? "hit" : "miss")}");
        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            ImGui.TextColored(UberFeatureDisabledColor, failureReason);
        }
    }

    private static bool TryGetUberBackendSnapshot(UberMaterialVariantStatus status, out UberShaderVariantTelemetry.BackendSnapshot snapshot)
    {
        ulong variantHash = status.RequestedVariantHash != 0 &&
            status.RequestedVariantHash != status.ActiveVariantHash
            ? status.RequestedVariantHash
            : status.ActiveVariantHash != 0
                ? status.ActiveVariantHash
                : status.RequestedVariantHash;

        return UberShaderVariantTelemetry.TryGetBackendSnapshot(variantHash, out snapshot);
    }

    private static void DrawUberStatusBadge(string label, Vector4 color)
    {
        ImGui.SameLine();
        ImGui.TextColored(color, $"[{label}]");
    }

    private static string FormatTiming(double milliseconds)
        => milliseconds > 0.0 ? $"{milliseconds:0.##} ms" : "n/a";

    private static HashSet<string> ResolveUnavailableUberFeatureIds(XRShader activeFragmentShader, ShaderUiManifest manifest)
    {
        string activeSource = activeFragmentShader.Source?.Text ?? string.Empty;
        if (!Regex.IsMatch(activeSource, @"^[ \t]*#define[ \t]+XRENGINE_UBER_IMPORT_MATERIAL(?:\s+.*)?$", RegexOptions.Multiline))
            return [];

        HashSet<string> unavailableFeatureIds = new(StringComparer.Ordinal);
        foreach (ShaderUiFeature feature in manifest.Features)
        {
            if (UberImportVariantUnavailableFeatureIds.Contains(feature.Id))
                unavailableFeatureIds.Add(feature.Id);
        }

        return unavailableFeatureIds;
    }

    private static void DrawUberBulkActions(XRMaterial material, ShaderUiManifest manifest, IReadOnlyList<ShaderUiProperty> visibleProperties)
    {
        bool variantChanged = false;
        if (ImGui.SmallButton("Disable All Expensive Features"))
        {
            foreach (ShaderUiFeature feature in manifest.Features)
            {
                if (feature.Required || feature.Cost < EShaderUiFeatureCost.Medium)
                    continue;

                variantChanged |= material.SetUberFeatureEnabled(feature.Id, false);
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Convert Eligible Properties To Constant"))
        {
            foreach (ShaderUiProperty property in visibleProperties)
            {
                if (property.IsSampler)
                    continue;

                variantChanged |= material.SetUberPropertyMode(property.Name, EShaderUiPropertyMode.Static);
            }
        }

        if (variantChanged)
            material.RequestUberVariantRebuild();

        ImGui.Spacing();
    }

    private static ulong ComputeStableSourceHash(string source)
    {
        const ulong fnvOffset = 14695981039346656037ul;
        const ulong fnvPrime = 1099511628211ul;

        ulong hash = fnvOffset;
        for (int i = 0; i < source.Length; i++)
        {
            hash ^= source[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    private readonly struct ImGuiDisabledScope : IDisposable
    {
        private readonly bool _disabled;

        public ImGuiDisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_disabled)
                ImGui.EndDisabled();
        }
    }

    private static PendingUberFeatureResolution? BuildPendingUberFeatureResolution(
        XRMaterial material,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
        ShaderUiFeature feature)
    {
        string[] missingDependencies = feature.Dependencies
            .Where(id => featureLookup.TryGetValue(id, out ShaderUiFeature? dependency) && !material.IsUberFeatureEnabled(dependency.Id, dependency.DefaultEnabled))
            .ToArray();

        string[] activeConflicts = feature.Conflicts
            .Where(id => featureLookup.TryGetValue(id, out ShaderUiFeature? conflict) && material.IsUberFeatureEnabled(conflict.Id, conflict.DefaultEnabled))
            .ToArray();

        return missingDependencies.Length == 0 && activeConflicts.Length == 0
            ? null
            : new PendingUberFeatureResolution(feature.Id, missingDependencies, activeConflicts);
    }

    private static void DrawPendingUberFeatureResolution(XRMaterial material, ShaderUiFeature feature, ref bool variantChanged)
    {
        if (!PendingUberFeatureResolutions.TryGetValue(material, out PendingUberFeatureResolution? pending) ||
            !string.Equals(pending.FeatureId, feature.Id, StringComparison.Ordinal))
            return;

        if (pending.MissingDependencies.Length > 0)
            ImGui.TextColored(ValidationWarningColor, $"Requires: {string.Join(", ", pending.MissingDependencies)}");
        if (pending.ActiveConflicts.Length > 0)
            ImGui.TextColored(ValidationWarningColor, $"Conflicts with: {string.Join(", ", pending.ActiveConflicts)}");

        if (ImGui.SmallButton($"Resolve##{feature.Id}"))
        {
            foreach (string dependencyId in pending.MissingDependencies)
                variantChanged |= material.SetUberFeatureEnabled(dependencyId, true);
            foreach (string conflictId in pending.ActiveConflicts)
                variantChanged |= material.SetUberFeatureEnabled(conflictId, false);

            variantChanged |= material.SetUberFeatureEnabled(feature.Id, true);
            ClearPendingUberFeatureResolution(material, feature.Id);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"Cancel##{feature.Id}"))
            ClearPendingUberFeatureResolution(material, feature.Id);
    }

    private static void SetPendingUberFeatureResolution(XRMaterial material, PendingUberFeatureResolution resolution)
    {
        PendingUberFeatureResolutions.Remove(material);
        PendingUberFeatureResolutions.Add(material, resolution);
    }

    private static void ClearPendingUberFeatureResolution(XRMaterial material, string featureId)
    {
        if (PendingUberFeatureResolutions.TryGetValue(material, out PendingUberFeatureResolution? pending) &&
            string.Equals(pending.FeatureId, featureId, StringComparison.Ordinal))
            PendingUberFeatureResolutions.Remove(material);
    }

    private readonly record struct ShaderUiEnumOption(double Value, string Label);
    private readonly record struct ShaderUiNumericRange(double Min, double Max);

    private sealed record PendingUberFeatureResolution(string FeatureId, string[] MissingDependencies, string[] ActiveConflicts);
}