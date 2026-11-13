using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Reconciles a locked source toon material against the compiled base pass and properties of its locked shader file.
/// Reads pass preprocessor defines, stripped property samplers, inline baked constants, and <c>ThryToggle</c> bindings
/// so downstream Uber conversion sees the active shader behavior rather than stale unlocked metadata.
/// </summary>
internal static class SourceToonLockedShaderReconciler
{
    private static readonly Regex CgProgramBlockRegex = new(
        @"CGPROGRAM\b([\s\S]*?)ENDCG\b",
        RegexOptions.Compiled);

    private static readonly Regex DefineRegex = new(
        @"^\s*#define\s+([A-Za-z0-9_]+)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PropSamplerRegex = new(
        @"#if(?:def\s+(PROP_[A-Za-z0-9_]+)|\s+defined\s*\(\s*(PROP_[A-Za-z0-9_]+)\s*\)(?:\s*\|\|\s*!defined\s*\(\s*OPTIMIZER_ENABLED\s*\))?)[^\n]*\r?\n\s*(?:UNITY_DECLARE_TEX2D(?:_NOSAMPLER)?|Texture2D(?:Array)?(?:<[^>]+>)?|sampler2D)\s*\(?\s*(_[A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    private static readonly Regex ThryToggleRegex = new(
        @"\[ThryToggle\(\s*([A-Za-z0-9_]+)\s*(?:,\s*(?:true|false)\s*)?\)\]\s*(?:\[[^\]]*\]\s*)*(_[A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    private static readonly Regex BakedScalarRegex = new(
        @"\(\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*/\*\s*(_[A-Za-z0-9_]+)\s*\*/\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex BakedVectorRegex = new(
        @"(?:float|half|fixed)(2|3|4)\s*\(\s*([^)]+?)\s*/\*\s*(_[A-Za-z0-9_]+)\s*\*/",
        RegexOptions.Compiled);

    private static readonly Regex AnimatedSuffixPropertyRegex = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(_[A-Za-z0-9_]+?)Animated\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly string[] SyncedFeatureKeywords =
    [
        "POSTPROCESS",
        "COLOR_GRADING_HDR",
        "COLOR_GRADING_HDR_3D",
        "POI_MATCAP0",
        "POI_MATCAP2",
        "POI_MATCAP3",
        "_EMISSION",
        "POI_EMISSION_1",
        "POI_EMISSION_2",
        "POI_EMISSION_3",
        "POI_GRABPASS",
        "FINALPASS",
        "POI_ANISOTROPICS",
        "POI_STYLIZED_StylizedSpecular",
        "POI_UDIMDISCARD",
        "POI_RIM2",
        "_GLOSSYREFLECTIONS_OFF",
        "POI_GLOBALMASK_TEXTURES",
        "DISTORT",
        "_METALLICGLOSSMAP",
        "MOCHIE_PBR",
        "GEOM_TYPE_BRANCH",
        "GEOM_TYPE_BRANCH_DETAIL",
        "GEOM_TYPE_FROND",
        "DEPTH_OF_FIELD_COC_VIEW"
    ];

    /// <summary>
    /// Returns a reconciled copy of <paramref name="document"/> when <paramref name="isLocked"/> is <see langword="true"/>
    /// and a valid locked shader file exists at <paramref name="shaderPath"/>.
    /// </summary>
    public static SerializedMaterialDocument Reconcile(
        SerializedMaterialDocument document,
        string? shaderPath,
        bool isLocked)
    {
        if (!isLocked || string.IsNullOrWhiteSpace(shaderPath) || !File.Exists(shaderPath))
            return document;

        string shaderText = File.ReadAllText(shaderPath);
        if (!TryExtractBasePassProgram(shaderText, out string passProgram))
            return document;

        SerializedMaterialDocument copy = document.CreateConversionCopy();
        HashSet<string> passDefines = ExtractPassDefines(passProgram);
        bool optimizerEnabled = passDefines.Contains("OPTIMIZER_ENABLED");
        HashSet<string> renamedAnimatedProperties = ExtractDeclaredAnimatedProperties(shaderText, copy);

        StripDisabledPropSamplers(copy, passProgram, passDefines, optimizerEnabled);
        ApplyBakedValues(copy, passProgram, renamedAnimatedProperties);
        SyncKeywordsAndToggles(copy, shaderText, passDefines);
        ApplySafetyOverrides(copy, passDefines);

        return copy;
    }

    private static bool TryExtractBasePassProgram(string shaderText, out string programText)
    {
        programText = string.Empty;
        string? firstProgram = null;

        foreach (Match match in CgProgramBlockRegex.Matches(shaderText))
        {
            string body = match.Groups[1].Value;
            firstProgram ??= body;
            if (body.Contains("#define POI_PASS_BASE", StringComparison.Ordinal))
            {
                programText = body;
                return true;
            }
        }

        if (firstProgram is null)
            return false;

        programText = firstProgram;
        return true;
    }

    private static HashSet<string> ExtractPassDefines(string passProgram)
    {
        HashSet<string> defines = new(StringComparer.Ordinal);
        foreach (Match match in DefineRegex.Matches(passProgram))
            defines.Add(match.Groups[1].Value);

        return defines;
    }

    private static HashSet<string> ExtractDeclaredAnimatedProperties(
        string shaderText,
        SerializedMaterialDocument document)
    {
        // Source toon optimizer tags in .mat files can leave stale *Animated floats behind;
        // only preserve runtime authoring values when the locked shader still declares *Animated in Properties.
        HashSet<string> animated = new(StringComparer.Ordinal);
        foreach (Match match in AnimatedSuffixPropertyRegex.Matches(shaderText))
        {
            string baseName = match.Groups[1].Value;
            if (document.TryGetPositive(baseName + "Animated"))
                animated.Add(baseName);
        }

        return animated;
    }

    private static void StripDisabledPropSamplers(
        SerializedMaterialDocument copy,
        string passProgram,
        HashSet<string> passDefines,
        bool optimizerEnabled)
    {
        if (!optimizerEnabled)
            return;

        foreach (Match match in PropSamplerRegex.Matches(passProgram))
        {
            string propDefine = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            string samplerName = match.Groups[3].Value;
            if (string.Equals(samplerName, "_MainTex", StringComparison.Ordinal))
                continue;

            if (!passDefines.Contains(propDefine))
                copy.Textures.Remove(samplerName);
        }
    }

    private static void ApplyBakedValues(
        SerializedMaterialDocument copy,
        string passProgram,
        HashSet<string> renamedAnimatedProperties)
    {
        foreach (Match match in BakedScalarRegex.Matches(passProgram))
        {
            string propertyName = match.Groups[2].Value;
            if (renamedAnimatedProperties.Contains(propertyName))
                continue;

            if (float.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float bakedValue))
            {
                copy.Floats[propertyName] = bakedValue;
            }
        }

        foreach (Match match in BakedVectorRegex.Matches(passProgram))
        {
            string propertyName = match.Groups[3].Value;
            if (renamedAnimatedProperties.Contains(propertyName))
                continue;

            string[] parts = match.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                continue;

            float[] values = new float[4];
            bool parsedAll = true;
            int componentCount = Math.Min(parts.Length, 4);
            for (int index = 0; index < componentCount; ++index)
            {
                if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                {
                    parsedAll = false;
                    break;
                }
            }

            if (parsedAll)
                copy.Vectors[propertyName] = new System.Numerics.Vector4(values[0], values[1], values[2], values[3]);
        }
    }

    private static void SyncKeywordsAndToggles(
        SerializedMaterialDocument copy,
        string shaderText,
        HashSet<string> passDefines)
    {
        HashSet<string> validKeywords = new(copy.ValidKeywords, StringComparer.Ordinal);
        foreach (string keyword in SyncedFeatureKeywords)
        {
            if (passDefines.Contains(keyword))
                validKeywords.Add(keyword);
            else
                validKeywords.Remove(keyword);
        }

        foreach (Match match in ThryToggleRegex.Matches(shaderText))
        {
            string keyword = match.Groups[1].Value;
            if (string.Equals(keyword, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyword, "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string toggleProperty = match.Groups[2].Value;
            bool enabled = passDefines.Contains(keyword);
            copy.Floats[toggleProperty] = enabled ? 1.0f : 0.0f;
            if (enabled)
                validKeywords.Add(keyword);
            else
                validKeywords.Remove(keyword);
        }

        copy.ValidKeywords.Clear();
        foreach (string keyword in validKeywords)
            copy.ValidKeywords.Add(keyword);
    }

    private static void ApplySafetyOverrides(
        SerializedMaterialDocument copy,
        HashSet<string> passDefines)
    {
        copy.Floats["_PostProcess"] = passDefines.Contains("POSTPROCESS") ? 1.0f : 0.0f;

        if (!passDefines.Contains("POI_MATCAP0"))
            copy.Floats["_MatcapEnable"] = 0.0f;

        if (!passDefines.Contains("COLOR_GRADING_HDR_3D"))
            copy.Floats["_Matcap2Enable"] = 0.0f;

        if (!passDefines.Contains("POI_MATCAP2"))
            copy.Floats["_Matcap3Enable"] = 0.0f;

        if (!passDefines.Contains("POI_MATCAP3"))
            copy.Floats["_Matcap4Enable"] = 0.0f;

        if (!passDefines.Contains("_EMISSION"))
            copy.Floats["_EnableEmission"] = 0.0f;

        if (!passDefines.Contains("POI_GRABPASS"))
        {
            copy.Floats["_EnableGrabpass"] = 0.0f;
            copy.Floats["_GrabPass"] = 0.0f;
        }

        if (!passDefines.Contains("POI_UDIMDISCARD"))
            copy.Floats["_EnableUDIMDiscardOptions"] = 0.0f;

        if (!passDefines.Contains("POI_STYLIZED_StylizedSpecular"))
            copy.Floats["_StylizedSpecular"] = 0.0f;

        if (!passDefines.Contains("DISTORT"))
            copy.Floats["_DistortionEnabled"] = 0.0f;

        if (!passDefines.Contains("_METALLICGLOSSMAP"))
            copy.Floats["_MetallicEnabled"] = 0.0f;
    }
}
