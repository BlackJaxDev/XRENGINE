using System.IO;
using System.Linq;
using System.Numerics;
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

    private static void DrawUberInspector(XRMaterial material)
    {
        if (!TryGetUberMaterialManifest(material, out XRShader? fragmentShader, out ShaderUiManifest? manifest) ||
            fragmentShader is null ||
            manifest is null)
            return;

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

        int enabledFeatureCount = uberManifest.Features.Count(static x => x.DefaultEnabled);
        ulong variantHash = ComputeStableSourceHash(uberFragmentShader.Source?.Text ?? string.Empty);

        ImGui.TextColored(UberHeaderColor, $"Feature Modules: {uberManifest.Features.Count} | Authorable Properties: {visibleProperties.Length}");
        ImGui.TextDisabled($"Enabled: {enabledFeatureCount} | Variant Hash: 0x{variantHash:X16} | Async Compile: {(uberFragmentShader.GenerateAsync ? "On" : "Off")}");
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
        if (featureId is not null && featureLookup.TryGetValue(featureId, out ShaderUiFeature? feature))
        {
            bool enabled = feature.DefaultEnabled;
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
                    ApplyUberFeatureToggle(material, fragmentShader, feature, toggled);

                ImGui.SameLine();
                ImGui.TextColored(toggled ? UberFeatureEnabledColor : UberFeatureDisabledColor, toggled ? "enabled" : "disabled");
            }

            if (!string.IsNullOrWhiteSpace(feature.Tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(feature.Tooltip);

            if (feature.Dependencies.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"deps: {string.Join(", ", feature.Dependencies)}");
            }

            ImGui.PopID();
        }

        if (!ImGui.BeginTable($"UberPropertyTable_{featureId ?? "base"}", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.28f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn("Drive", ImGuiTableColumnFlags.WidthStretch, 0.18f);
        ImGui.TableHeadersRow();

        foreach (ShaderUiProperty rawProperty in properties)
        {
            ShaderUiProperty property = rawProperty!;
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
                }
            }

            ImGui.TableSetColumnIndex(3);
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
                DrawParameterDriveCell(material, parameter, property.Name, property.GlslType);
            }
        }

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static bool IsAuthorableUberProperty(ShaderUiProperty property)
    {
        if (IsEngineUniform(property.Name, out _))
            return false;

        return property.Name.StartsWith("_", StringComparison.Ordinal) ||
               property.Name.Equals("AlphaCutoff", StringComparison.Ordinal);
    }

    private static string ResolveCategoryName(ShaderUiProperty property, IReadOnlyDictionary<string, ShaderUiFeature> featureLookup)
    {
        if (!string.IsNullOrWhiteSpace(property.Category))
            return property.Category!;

        if (property.FeatureId is not null && featureLookup.TryGetValue(property.FeatureId, out ShaderUiFeature? feature) && !string.IsNullOrWhiteSpace(feature.Category))
            return feature.Category!;

        return "General";
    }

    private static void ApplyUberFeatureToggle(XRMaterial material, XRShader fragmentShader, ShaderUiFeature feature, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(feature.GuardMacro) || fragmentShader.Source?.Text is not { Length: > 0 } source)
            return;

        bool defineShouldExist = feature.GuardDefinedEnablesFeature ? enabled : !enabled;
        string updatedSource = SetMacroDefined(source, feature.GuardMacro, defineShouldExist);
        if (string.Equals(updatedSource, source, StringComparison.Ordinal))
            return;

        TextFile updatedText = TextFile.FromText(updatedSource);
        updatedText.FilePath = fragmentShader.Source?.FilePath;
        updatedText.Name = fragmentShader.Source?.Name;

        var replacement = new XRShader(fragmentShader.Type, updatedText)
        {
            Name = fragmentShader.Name,
            GenerateAsync = fragmentShader.GenerateAsync,
        };

        material.SetShader(EShaderType.Fragment, replacement, coerceShaderType: true);
        material.MarkDirty();
    }

    private static string SetMacroDefined(string source, string macroName, bool shouldBeDefined)
    {
        string definePattern = $@"^[ \t]*#define[ \t]+{Regex.Escape(macroName)}(?:\s+.*)?$\r?\n?";
        string stripped = Regex.Replace(source, definePattern, string.Empty, RegexOptions.Multiline);
        if (!shouldBeDefined)
            return stripped;

        Match versionMatch = Regex.Match(stripped, @"^[ \t]*#version.*$", RegexOptions.Multiline);
        if (!versionMatch.Success)
            return $"#define {macroName}{Environment.NewLine}{stripped}";

        int insertionIndex = versionMatch.Index + versionMatch.Length;
        string prefix = stripped[..insertionIndex];
        string suffix = insertionIndex < stripped.Length ? stripped[insertionIndex..] : string.Empty;
        return $"{prefix}{Environment.NewLine}#define {macroName}{Environment.NewLine}{suffix.TrimStart('\r', '\n')}";
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
}