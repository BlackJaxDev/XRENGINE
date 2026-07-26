using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Scene.Importers;

public sealed record MaterialSerializedParameter(string Kind, string Json)
{
    public static MaterialSerializedParameter? Capture(ShaderVar parameter)
        => parameter switch
        {
            ShaderFloat value => new("float", JsonSerializer.Serialize(value.Value)),
            ShaderInt value => new("int", JsonSerializer.Serialize(value.Value)),
            ShaderUInt value => new("uint", JsonSerializer.Serialize(value.Value)),
            ShaderBool value => new("bool", JsonSerializer.Serialize(value.Value)),
            ShaderVector2 value => new("vector2", JsonSerializer.Serialize(value.Value)),
            ShaderVector3 value => new("vector3", JsonSerializer.Serialize(value.Value)),
            ShaderVector4 value => new("vector4", JsonSerializer.Serialize(value.Value)),
            _ => null,
        };

    public bool Apply(ShaderVar parameter)
    {
        try
        {
            switch (Kind, parameter)
            {
                case ("float", ShaderFloat value):
                    value.Value = JsonSerializer.Deserialize<float>(Json);
                    return true;
                case ("int", ShaderInt value):
                    value.Value = JsonSerializer.Deserialize<int>(Json);
                    return true;
                case ("uint", ShaderUInt value):
                    value.Value = JsonSerializer.Deserialize<uint>(Json);
                    return true;
                case ("bool", ShaderBool value):
                    value.Value = JsonSerializer.Deserialize<bool>(Json);
                    return true;
                case ("vector2", ShaderVector2 value):
                    value.Value = JsonSerializer.Deserialize<Vector2>(Json);
                    return true;
                case ("vector3", ShaderVector3 value):
                    value.Value = JsonSerializer.Deserialize<Vector3>(Json);
                    return true;
                case ("vector4", ShaderVector4 value):
                    value.Value = JsonSerializer.Deserialize<Vector4>(Json);
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record MaterialTextureSnapshot(string? AssetPath, string? SamplerName)
{
    public static MaterialTextureSnapshot Capture(XRTexture? texture)
        => new(
            texture?.OriginalPath ?? texture?.FilePath,
            texture?.SamplerName);
}

/// <summary>
/// Converter-owned state captured before post-import overrides are applied.
/// It provides the stable comparison base for later reimports.
/// </summary>
public sealed class MaterialImportedStateSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public string ConverterVersion { get; init; } = MaterialConversionReportBuilder.ConverterVersion;
    public int SourceDescriptorVersion { get; init; } = MaterialConversionReportBuilder.SourceDescriptorVersion;
    public string SourceContentSha256 { get; init; } = string.Empty;
    public Dictionary<string, MaterialSerializedParameter> Parameters { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<int, MaterialTextureSnapshot> Textures { get; init; } = [];
    public Dictionary<string, bool> Features { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, EShaderUiPropertyMode> PropertyModes { get; init; } = new(StringComparer.Ordinal);
    public int RenderPass { get; init; }
    public ETransparencyMode TransparencyMode { get; init; }
    public float AlphaCutoff { get; init; }
    public int TransparentSortPriority { get; init; }
    public ETransparencyMode? TransparentTechniqueOverride { get; init; }

    public static MaterialImportedStateSnapshot Capture(
        XRMaterial material,
        MaterialConversionReport report)
    {
        Dictionary<string, MaterialSerializedParameter> parameters = new(StringComparer.Ordinal);
        foreach (ShaderVar parameter in material.Parameters.OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            MaterialSerializedParameter? value = MaterialSerializedParameter.Capture(parameter);
            if (value is not null)
                parameters[parameter.Name] = value;
        }

        Dictionary<int, MaterialTextureSnapshot> textures = [];
        for (int index = 0; index < material.Textures.Count; index++)
            textures[index] = MaterialTextureSnapshot.Capture(material.Textures[index]);

        return new()
        {
            ConverterVersion = report.ConverterVersion,
            SourceDescriptorVersion = report.SourceDescriptorVersion,
            SourceContentSha256 = report.SourceContentSha256,
            Parameters = parameters,
            Textures = textures,
            Features = material.UberAuthoredState.Features
                .OrderBy(static value => value.Id, StringComparer.Ordinal)
                .ToDictionary(static value => value.Id, static value => value.Enabled, StringComparer.Ordinal),
            PropertyModes = material.UberAuthoredState.Properties
                .OrderBy(static value => value.Name, StringComparer.Ordinal)
                .ToDictionary(static value => value.Name, static value => value.Mode, StringComparer.Ordinal),
            RenderPass = material.RenderPass,
            TransparencyMode = material.TransparencyMode,
            AlphaCutoff = material.AlphaCutoff,
            TransparentSortPriority = material.TransparentSortPriority,
            TransparentTechniqueOverride = material.TransparentTechniqueOverride,
        };
    }
}

/// <summary>
/// User changes relative to the last converter-owned snapshot. This data is
/// reapplied after conversion and is deliberately separate from imported state.
/// </summary>
public sealed class MaterialLocalOverrideSet
{
    public int FormatVersion { get; init; } = 1;
    public Dictionary<string, MaterialSerializedParameter> Parameters { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<int, MaterialTextureSnapshot> Textures { get; init; } = [];
    public Dictionary<string, bool> Features { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, EShaderUiPropertyMode> PropertyModes { get; init; } = new(StringComparer.Ordinal);
    public int? RenderPass { get; init; }
    public ETransparencyMode? TransparencyMode { get; init; }
    public float? AlphaCutoff { get; init; }
    public int? TransparentSortPriority { get; init; }
    public bool HasTransparentTechniqueOverride { get; init; }
    public ETransparencyMode? TransparentTechniqueOverride { get; init; }

    public bool IsEmpty
        => Parameters.Count == 0 &&
           Textures.Count == 0 &&
           Features.Count == 0 &&
           PropertyModes.Count == 0 &&
           RenderPass is null &&
           TransparencyMode is null &&
           AlphaCutoff is null &&
           TransparentSortPriority is null &&
           !HasTransparentTechniqueOverride;

    public static MaterialLocalOverrideSet Diff(
        XRMaterial material,
        MaterialImportedStateSnapshot baseline)
    {
        Dictionary<string, MaterialSerializedParameter> parameters = new(StringComparer.Ordinal);
        foreach (ShaderVar parameter in material.Parameters.OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            MaterialSerializedParameter? current = MaterialSerializedParameter.Capture(parameter);
            if (current is not null &&
                (!baseline.Parameters.TryGetValue(parameter.Name, out MaterialSerializedParameter? imported) ||
                 current != imported))
                parameters[parameter.Name] = current;
        }

        Dictionary<int, MaterialTextureSnapshot> textures = [];
        for (int index = 0; index < material.Textures.Count; index++)
        {
            MaterialTextureSnapshot current = MaterialTextureSnapshot.Capture(material.Textures[index]);
            if (!baseline.Textures.TryGetValue(index, out MaterialTextureSnapshot? imported) ||
                current != imported)
                textures[index] = current;
        }

        Dictionary<string, bool> features = new(StringComparer.Ordinal);
        foreach (UberMaterialFeatureState feature in material.UberAuthoredState.Features
                     .OrderBy(static value => value.Id, StringComparer.Ordinal))
        {
            if (!baseline.Features.TryGetValue(feature.Id, out bool imported) ||
                imported != feature.Enabled)
                features[feature.Id] = feature.Enabled;
        }

        Dictionary<string, EShaderUiPropertyMode> modes = new(StringComparer.Ordinal);
        foreach (UberMaterialPropertyState property in material.UberAuthoredState.Properties
                     .OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            if (!baseline.PropertyModes.TryGetValue(property.Name, out EShaderUiPropertyMode imported) ||
                imported != property.Mode)
                modes[property.Name] = property.Mode;
        }

        return new()
        {
            Parameters = parameters,
            Textures = textures,
            Features = features,
            PropertyModes = modes,
            RenderPass = material.RenderPass == baseline.RenderPass ? null : material.RenderPass,
            TransparencyMode = material.TransparencyMode == baseline.TransparencyMode ? null : material.TransparencyMode,
            AlphaCutoff = material.AlphaCutoff.Equals(baseline.AlphaCutoff) ? null : material.AlphaCutoff,
            TransparentSortPriority = material.TransparentSortPriority == baseline.TransparentSortPriority
                ? null
                : material.TransparentSortPriority,
            HasTransparentTechniqueOverride =
                material.TransparentTechniqueOverride != baseline.TransparentTechniqueOverride,
            TransparentTechniqueOverride = material.TransparentTechniqueOverride,
        };
    }

    public void Apply(XRMaterial material)
    {
        Dictionary<string, ShaderVar> parameterLookup =
            material.Parameters.ToDictionary(static value => value.Name, StringComparer.Ordinal);
        foreach ((string name, MaterialSerializedParameter serialized) in Parameters
                     .OrderBy(static value => value.Key, StringComparer.Ordinal))
        {
            if (parameterLookup.TryGetValue(name, out ShaderVar? parameter))
                serialized.Apply(parameter);
        }

        foreach ((int index, MaterialTextureSnapshot snapshot) in Textures.OrderBy(static value => value.Key))
        {
            if ((uint)index >= (uint)material.Textures.Count)
                continue;
            if (string.IsNullOrWhiteSpace(snapshot.AssetPath))
            {
                material.Textures[index] = null;
                continue;
            }

            XRTexture2D texture = new(snapshot.AssetPath)
            {
                SamplerName = snapshot.SamplerName,
            };
            material.Textures[index] = texture;
        }

        foreach ((string id, bool enabled) in Features.OrderBy(static value => value.Key, StringComparer.Ordinal))
            material.SetUberFeatureEnabled(id, enabled);
        foreach ((string name, EShaderUiPropertyMode mode) in PropertyModes
                     .OrderBy(static value => value.Key, StringComparer.Ordinal))
            material.SetUberPropertyMode(name, mode);

        if (RenderPass is int renderPass)
            material.RenderPass = renderPass;
        if (TransparencyMode is ETransparencyMode transparency)
            material.TransparencyMode = transparency;
        if (AlphaCutoff is float alphaCutoff)
            material.AlphaCutoff = alphaCutoff;
        if (TransparentSortPriority is int priority)
            material.TransparentSortPriority = priority;
        if (HasTransparentTechniqueOverride)
            material.TransparentTechniqueOverride = TransparentTechniqueOverride;
    }
}

public static class MaterialReimportWorkflow
{
    public static bool NeedsReimport(UnityMaterialAsset target, out string reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ImportedState is null)
        {
            reason = "No imported-state baseline is stored.";
            return true;
        }
        if (!string.Equals(
                target.ImportedState.ConverterVersion,
                MaterialConversionReportBuilder.ConverterVersion,
                StringComparison.Ordinal))
        {
            reason =
                $"Converter {target.ImportedState.ConverterVersion} -> " +
                $"{MaterialConversionReportBuilder.ConverterVersion}.";
            return true;
        }
        if (target.ImportedState.SourceDescriptorVersion !=
            MaterialConversionReportBuilder.SourceDescriptorVersion)
        {
            reason =
                $"Source descriptor {target.ImportedState.SourceDescriptorVersion} -> " +
                $"{MaterialConversionReportBuilder.SourceDescriptorVersion}.";
            return true;
        }
        if (string.IsNullOrWhiteSpace(target.OriginalPath) || !File.Exists(target.OriginalPath))
        {
            reason = "The original Unity material is unavailable.";
            return true;
        }

        using FileStream stream = File.OpenRead(target.OriginalPath);
        string sourceHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(
                sourceHash,
                target.ImportedState.SourceContentSha256,
                StringComparison.Ordinal))
        {
            reason = "The source Unity material content changed.";
            return true;
        }

        reason = "Imported state is current.";
        return false;
    }

    public static bool Reconvert(UnityMaterialAsset target, out UnityMaterialImportResult result)
        => Reimport(target, preserveLocalOverrides: true, out result);

    public static bool ResetAndReconvert(UnityMaterialAsset target, out UnityMaterialImportResult result)
        => Reimport(target, preserveLocalOverrides: false, out result);

    public static bool Reimport(
        UnityMaterialAsset target,
        bool preserveLocalOverrides,
        out UnityMaterialImportResult result)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.OriginalPath))
        {
            result = new()
            {
                ConversionReport = MaterialConversionReportBuilder.CreateFailure(
                    target.Name ?? "material.mat",
                    "The material has no original Unity .mat path."),
            };
            return false;
        }

        MaterialLocalOverrideSet overrides =
            preserveLocalOverrides && target.ImportedState is not null
                ? MaterialLocalOverrideSet.Diff(target, target.ImportedState)
                : new();
        result = UnityMaterialImporter.ImportWithReport(target.OriginalPath);
        if (result.Material is not XRMaterial imported || result.ConversionReport is null)
            return false;

        MaterialImportedStateSnapshot importedState =
            MaterialImportedStateSnapshot.Capture(imported, result.ConversionReport);
        overrides.Apply(imported);
        target.CopyFrom(imported);
        target.ImportedState = importedState;
        target.LocalOverrides = overrides;
        target.LastConversionReport = result.ConversionReport;
        target.OriginalLastWriteTimeUtc = File.Exists(target.OriginalPath)
            ? File.GetLastWriteTimeUtc(target.OriginalPath)
            : null;
        MaterialConversionReportRegistry.Instance.Set(target, result.ConversionReport);
        return true;
    }
}
