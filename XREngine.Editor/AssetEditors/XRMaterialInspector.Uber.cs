using System.IO;
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
    private static readonly ConditionalWeakTable<XRMaterial, PendingUberFeatureResolution> PendingUberFeatureResolutions = new();

    private static void DrawUberInspector(XRMaterial material)
    {
        if (!TryGetUberMaterialManifest(material, out XRShader? fragmentShader, out ShaderUiManifest? manifest) ||
            fragmentShader is null ||
            manifest is null)
            return;

        material.EnsureUberStateInitialized();

        XRShader uberFragmentShader = fragmentShader!;
        ShaderUiManifest uberManifest = manifest!;

        if (!ImGui.CollapsingHeader("Uber Shader", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var visibleProperties = uberManifest.Properties
            .Where(static x => IsAuthorableUberProperty(x))
            .ToArray();

        var featureLookup = uberManifest.FeatureLookup;
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

        foreach (string rawCategory in categoryNames)
        {
            string category = rawCategory!;
            var categoryProperties = visibleProperties
                .Where(x => string.Equals(ResolveCategoryName(x, featureLookup), category, StringComparison.Ordinal))
                .ToArray();

            if (!ImGui.TreeNodeEx($"{category}##UberCategory_{category}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            DrawUberCategoryProperties(material, uberFragmentShader, featureLookup, parameterLookup, samplerLookup, categoryProperties);
            ImGui.TreePop();
        }

        ImGui.Separator();
    }

    private static bool TryGetUberMaterialManifest(XRMaterial material, out XRShader? fragmentShader, out ShaderUiManifest? manifest)
    {
        fragmentShader = material.GetShader(EShaderType.Fragment);
        manifest = null;
        if (fragmentShader is null)
            return false;

        string? shaderPath = fragmentShader.Source?.FilePath ?? fragmentShader.FilePath;
        if (!string.Equals(Path.GetFileName(shaderPath), "UberShader.frag", StringComparison.OrdinalIgnoreCase))
            return false;

        manifest = fragmentShader.GetUiManifest();
        return manifest.Properties.Count > 0;
    }

    private static void DrawUberCategoryProperties(
        XRMaterial material,
        XRShader fragmentShader,
        IReadOnlyDictionary<string, ShaderUiFeature> featureLookup,
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

            DrawUberPropertyBucket(material, fragmentShader, featureLookup, parameterLookup, samplerLookup, currentFeatureId, bucket);
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
        Dictionary<string, ShaderVar> parameterLookup,
        Dictionary<string, SamplerBindingEntry> samplerLookup,
        string? featureId,
        IReadOnlyList<ShaderUiProperty> properties)
    {
        bool variantChanged = false;
        if (featureId is not null && featureLookup.TryGetValue(featureId, out ShaderUiFeature? feature))
        {
            bool enabled = material.IsUberFeatureEnabled(feature.Id, feature.DefaultEnabled);
            int animatedPropertyCount = properties.Count(x => !x.IsSampler && material.GetUberPropertyMode(x.Name, x.DefaultMode, x.IsSampler) == EShaderUiPropertyMode.Animated);
            ImGui.PushID(feature.Id);
            if (feature.Required)
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

            if (!string.IsNullOrWhiteSpace(feature.Tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(feature.Tooltip);

            if (feature.Cost != EShaderUiFeatureCost.Unspecified)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"cost: {feature.Cost.ToString().ToLowerInvariant()} | animated: {animatedPropertyCount}");
            }

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
            bool featureEnabled = featureId is null ||
                (featureLookup.TryGetValue(featureId, out ShaderUiFeature? bucketFeature) && material.IsUberFeatureEnabled(bucketFeature.Id, bucketFeature.DefaultEnabled)) ||
                (featureLookup.TryGetValue(featureId, out ShaderUiFeature? requiredFeature) && requiredFeature.Required);
            EShaderUiPropertyMode propertyMode = material.GetUberPropertyMode(property.Name, property.DefaultMode, property.IsSampler);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(property.DisplayName);
            if (!string.IsNullOrWhiteSpace(property.Tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(property.Tooltip);

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
                ImGui.TextUnformatted(animated ? "Animated" : "Static");
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
                ShaderVar? parameter = FindParameter(parameterLookup, property.Name);
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
                    DrawShaderParameterControl(material, parameter);
                    ImGui.PopID();

                    if (propertyMode == EShaderUiPropertyMode.Static && featureEnabled && !Equals(previousValue, parameter.GenericValue))
                    {
                        variantChanged = true;
                        material.RefreshUberPropertyStaticLiteral(property.Name);
                    }
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
                ShaderVar? parameter = FindParameter(parameterLookup, property.Name);
                if (propertyMode == EShaderUiPropertyMode.Static)
                    ImGui.TextDisabled(featureEnabled ? "Rebuild variant" : "Feature off");
                else
                    DrawParameterDriveCell(material, parameter, property.Name, property.GlslType);
            }
        }

        ImGui.EndTable();
        ImGui.Spacing();

        if (variantChanged)
            material.RequestUberVariantRebuild();
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
        if (ImGui.SmallButton("Convert Eligible Properties To Static"))
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

    private sealed record PendingUberFeatureResolution(string FeatureId, string[] MissingDependencies, string[] ActiveConflicts);
}