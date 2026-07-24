using System.Numerics;
using System.Text;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Normalizes unlocked and optimizer-renamed Unity material properties into stable Poiyomi identities.
/// </summary>
public static class PoiyomiMaterialDescriptorFactory
{
    private const string RenameSuffixTag = "thry_rename_suffix";
    private const string AnimatedTagSuffix = "Animated";

    public static PoiyomiMaterialDescriptor Create(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        PoiyomiShaderMatchResult match,
        ICollection<MaterialConversionDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(match);

        if (!match.IsAccepted)
            throw new ArgumentException("A Poiyomi descriptor requires an accepted shader match.", nameof(match));

        string renameSuffix = ResolveRenameSuffix(document);
        Dictionary<string, PoiyomiPropertyBinding> bindings = new(StringComparer.Ordinal);
        Dictionary<string, PoiyomiTextureDescriptor> textures = new(StringComparer.Ordinal);
        Dictionary<string, float> floats = new(StringComparer.Ordinal);
        Dictionary<string, int> ints = new(StringComparer.Ordinal);
        Dictionary<string, Vector4> vectors = new(StringComparer.Ordinal);
        Dictionary<string, string> strings = new(StringComparer.Ordinal);

        foreach ((string sourceName, UnityTexturePropertyDocument texture) in document.Textures)
        {
            PoiyomiPropertyBinding binding = ResolveBinding(sourceName, document, renameSuffix);
            bindings[sourceName] = binding;

            UnityResolvedAsset resolved = resolver.Resolve(texture.TextureReference);
            UnityTextureImportDocument? importSettings = resolved.AssetPath is null
                ? null
                : UnityTextureImportDocumentParser.ParseFile(resolved.AssetPath);
            var descriptor = new PoiyomiTextureDescriptor
            {
                SourcePropertyName = sourceName,
                SemanticPropertyName = binding.SemanticName,
                Reference = texture.TextureReference,
                Scale = texture.Scale,
                Offset = texture.Offset,
                ResolvedAsset = resolved,
                ImportSettings = importSettings,
            };

            SetNormalized(textures, descriptor.SemanticPropertyName, descriptor, binding.IsRenamed);

            if (descriptor.IsMissing)
            {
                diagnostics?.Add(new MaterialConversionDiagnostic(
                    MaterialConversionDiagnosticCodes.AssetReferenceMissing,
                    MaterialConversionDiagnosticSeverity.Warning,
                    $"Unity texture GUID '{texture.TextureReference.Guid}' could not be resolved. The reference and transform remain in source metadata.",
                    binding.SemanticName));
            }
            else if (descriptor.ImportSettings is null &&
                     resolved.AssetPath is not null &&
                     !IsOrdinary2DImage(resolved.AssetPath))
            {
                diagnostics?.Add(new MaterialConversionDiagnostic(
                    MaterialConversionDiagnosticCodes.UnsupportedTextureAsset,
                    MaterialConversionDiagnosticSeverity.Warning,
                    $"Resolved Unity asset '{resolved.AssetPath}' has no supported TextureImporter metadata. Its GUID and path remain preserved for future conversion.",
                    binding.SemanticName));
            }
            else if (descriptor.RequiresNativeArrayOrCube)
            {
                diagnostics?.Add(new MaterialConversionDiagnostic(
                    MaterialConversionDiagnosticCodes.UnsupportedTextureAsset,
                    MaterialConversionDiagnosticSeverity.Info,
                    $"Unity texture shape '{importSettings!.Shape}' was preserved without flattening and requires a native array/cube binding in a later conversion phase.",
                    binding.SemanticName));
            }
        }

        Normalize(document.Floats, floats, bindings, document, renameSuffix);
        Normalize(document.Ints, ints, bindings, document, renameSuffix);
        Normalize(document.Vectors, vectors, bindings, document, renameSuffix);
        Normalize(document.Strings, strings, bindings, document, renameSuffix);

        return new PoiyomiMaterialDescriptor
        {
            Name = document.Name,
            Version = match.Version ?? PoiyomiToon93Catalog.Version,
            IsLocked = match.IsLocked,
            SourceDocument = document,
            ShaderAsset = resolver.Resolve(document.Shader),
            PropertyBindings = bindings,
            Textures = textures,
            Floats = floats,
            Ints = ints,
            Vectors = vectors,
            Strings = strings,
            ValidKeywords = document.ValidKeywords,
            InvalidKeywords = document.InvalidKeywords,
            DisabledShaderPasses = document.DisabledShaderPasses,
            OverrideTags = document.OverrideTags,
        };
    }

    private static void Normalize<T>(
        Dictionary<string, T> source,
        Dictionary<string, T> destination,
        Dictionary<string, PoiyomiPropertyBinding> bindings,
        UnityMaterialDocument document,
        string renameSuffix)
    {
        foreach ((string sourceName, T value) in source)
        {
            PoiyomiPropertyBinding binding = ResolveBinding(sourceName, document, renameSuffix);
            bindings[sourceName] = binding;
            SetNormalized(destination, binding.SemanticName, value, binding.IsRenamed);
        }
    }

    private static void SetNormalized<T>(
        Dictionary<string, T> destination,
        string semanticName,
        T value,
        bool isRenamed)
    {
        if (isRenamed || !destination.ContainsKey(semanticName))
            destination[semanticName] = value;
    }

    private static PoiyomiPropertyBinding ResolveBinding(
        string sourceName,
        UnityMaterialDocument document,
        string renameSuffix)
    {
        string semanticName = sourceName;
        bool isRenamed = false;
        if (!string.IsNullOrEmpty(renameSuffix))
        {
            string suffix = "_" + renameSuffix;
            if (sourceName.EndsWith(suffix, StringComparison.Ordinal))
            {
                string candidate = sourceName[..^suffix.Length];
                if (document.OverrideTags.TryGetValue(candidate + AnimatedTagSuffix, out string? mode) &&
                    string.Equals(mode, "2", StringComparison.Ordinal))
                {
                    semanticName = candidate;
                    isRenamed = true;
                }
            }
        }

        bool isAnimated =
            isRenamed ||
            (document.OverrideTags.TryGetValue(semanticName + AnimatedTagSuffix, out string? animatedMode) &&
             (string.Equals(animatedMode, "1", StringComparison.Ordinal) ||
              string.Equals(animatedMode, "2", StringComparison.Ordinal)));

        return new PoiyomiPropertyBinding
        {
            SourceName = sourceName,
            SemanticName = semanticName,
            IsAnimated = isAnimated,
            IsRenamed = isRenamed,
        };
    }

    private static string ResolveRenameSuffix(UnityMaterialDocument document)
    {
        string rawSuffix = document.OverrideTags.TryGetValue(RenameSuffixTag, out string? configured)
            ? configured
            : document.Name;

        StringBuilder cleaned = new(rawSuffix.Length);
        foreach (char character in rawSuffix)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '_')
                cleaned.Append(character);
        }

        return cleaned.ToString();
    }

    private static bool IsOrdinary2DImage(string assetPath)
        => Path.GetExtension(assetPath).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or
            ".tif" or ".tiff" or ".gif" or ".exr" or ".hdr" or
            ".dds" or ".webp";
}
