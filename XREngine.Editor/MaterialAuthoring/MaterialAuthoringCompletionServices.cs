using System.Collections.ObjectModel;
using System.Text.Json;
using ImageMagick;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.MaterialAuthoring;

public enum EMaterialAuthoringValueLayer
{
    Imported,
    Preset,
    Local,
}

/// <summary>
/// Explicit imported/preset/local value stack. Reverting a layer reveals the
/// next authored layer without mutating the lower-priority source value.
/// </summary>
public sealed class MaterialAuthoringLayeredValue<T>
{
    private readonly Dictionary<EMaterialAuthoringValueLayer, T> _values = [];

    public IReadOnlyDictionary<EMaterialAuthoringValueLayer, T> Values
        => new ReadOnlyDictionary<EMaterialAuthoringValueLayer, T>(_values);

    public void Apply(EMaterialAuthoringValueLayer layer, T value) => _values[layer] = value;

    public bool Revert(EMaterialAuthoringValueLayer layer) => _values.Remove(layer);

    public bool TryResolve(out T? value, out EMaterialAuthoringValueLayer layer)
    {
        foreach (EMaterialAuthoringValueLayer candidate in
                 new[] { EMaterialAuthoringValueLayer.Local, EMaterialAuthoringValueLayer.Preset, EMaterialAuthoringValueLayer.Imported })
        {
            if (!_values.TryGetValue(candidate, out value))
                continue;
            layer = candidate;
            return true;
        }
        value = default;
        layer = default;
        return false;
    }
}

public sealed record MaterialTextureArrayBuildResult(
    XRTexture2DArray Texture,
    IReadOnlyList<string> SourcePaths,
    int FrameCount);

/// <summary>
/// Deterministically constructs a texture array from an ordered recipe. Any
/// resize is explicit in the recipe; decoded images are disposed immediately
/// after their pixel data has been copied into engine mipmaps.
/// </summary>
public static class MaterialTextureArrayBuilder
{
    public static async Task<MaterialTextureArrayBuildResult> BuildAsync(
        MaterialTextureArrayRecipe recipe,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> diagnostics = recipe.Validate();
        if (diagnostics.Count > 0)
            throw new InvalidDataException(string.Join("; ", diagnostics));

        return await Task.Run(
            () =>
            {
                XRTexture2D[] layers = new XRTexture2D[recipe.Layers.Count];
                string[] sources = new string[recipe.Layers.Count];
                int width = recipe.Layers[0].Width;
                int height = recipe.Layers[0].Height;
                for (int index = 0; index < recipe.Layers.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MaterialTextureArrayLayer layer = recipe.Layers[index];
                    using MagickImage image = new(layer.SourcePath);
                    if (image.Width != (uint)width || image.Height != (uint)height)
                    {
                        if (!recipe.AllowResample)
                            throw new InvalidDataException(
                                $"Layer '{layer.SourcePath}' does not match {width}x{height}.");
                        image.FilterType = FilterType.Lanczos;
                        image.Resize((uint)width, (uint)height);
                    }
                    layers[index] = new XRTexture2D(image)
                    {
                        FilePath = Path.GetFullPath(layer.SourcePath),
                    };
                    sources[index] = Path.GetFullPath(layer.SourcePath);
                }
                return new MaterialTextureArrayBuildResult(
                    new XRTexture2DArray(layers),
                    sources,
                    layers.Length);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static bool TryAssign(
        MaterialAuthoringTransaction transaction,
        XRMaterial material,
        MaterialTextureArrayBuildResult result,
        Action<XRTexture2DArray> assign,
        Action restore,
        Action<int>? updateFrameCount)
    {
        transaction.AddStructural(
            material,
            $"Assign {result.FrameCount}-layer texture array",
            () =>
            {
                assign(result.Texture);
                updateFrameCount?.Invoke(result.FrameCount);
            },
            restore,
            invalidatesVariant: true);
        return transaction.TryExecute(out _);
    }
}

public enum EMaterialVariantBatchOperation
{
    Prepare,
    Unprepare,
    Rebuild,
    Prewarm,
}

/// <summary>
/// Failure-isolated, cancellable batch variant operations used by the optimizer
/// workspace and unprepared-material manager.
/// </summary>
public static class MaterialVariantBatchOperations
{
    public static async Task<IReadOnlyList<MaterialVariantPreparationResult>> ExecuteAsync(
        IReadOnlyList<XRMaterial> materials,
        EMaterialVariantBatchOperation operation,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        List<MaterialVariantPreparationResult> results = new(materials.Count);
        for (int index = 0; index < materials.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XRMaterial material = materials[index];
            try
            {
                bool succeeded = await Task.Run(
                    () => Execute(material, operation),
                    cancellationToken).ConfigureAwait(false);
                UberMaterialVariantStatus status = material.UberVariantStatus;
                results.Add(new(
                    material,
                    succeeded,
                    status.Stage,
                    status.RequestedVariantHash,
                    succeeded ? null : status.FailureReason));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                UberMaterialVariantStatus status = material.UberVariantStatus;
                results.Add(new(
                    material,
                    false,
                    status.Stage,
                    status.RequestedVariantHash,
                    exception.Message));
            }
            progress?.Report((index + 1, materials.Count));
        }
        return results;
    }

    private static bool Execute(XRMaterial material, EMaterialVariantBatchOperation operation)
    {
        switch (operation)
        {
            case EMaterialVariantBatchOperation.Unprepare:
                material.ClearUberVariantRuntimeState();
                return true;
            case EMaterialVariantBatchOperation.Rebuild:
                material.RequestUberVariantRebuild();
                return material.PrepareUberVariantImmediately();
            case EMaterialVariantBatchOperation.Prepare:
            case EMaterialVariantBatchOperation.Prewarm:
                return material.PrepareUberVariantImmediately();
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }
}

public static class MaterialLinkRegistryPersistence
{
    private sealed record Envelope(
        int Version,
        IReadOnlyList<MaterialAuthoringPersistentLinkGroup> Groups);

    public static string Serialize(MaterialLinkRegistry registry)
        => JsonSerializer.Serialize(
            new Envelope(
                MaterialAuthoringPersistentLinkGroup.CurrentVersion,
                [.. registry.Groups.OrderBy(static group => group.Name, StringComparer.Ordinal)
                    .ThenBy(static group => group.Id)]),
            new JsonSerializerOptions { WriteIndented = true });

    public static bool TryDeserialize(
        string json,
        out MaterialLinkRegistry registry,
        out IReadOnlyList<string> diagnostics)
    {
        registry = new();
        List<string> issues = [];
        try
        {
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json);
            if (envelope?.Version != MaterialAuthoringPersistentLinkGroup.CurrentVersion)
                issues.Add("The material-link registry version is unsupported.");
            else
            {
                foreach (MaterialAuthoringPersistentLinkGroup group in envelope.Groups)
                {
                    string? diagnostic = registry.AddOrReplace(group);
                    if (diagnostic is not null)
                        issues.Add(diagnostic);
                }
            }
        }
        catch (JsonException exception)
        {
            issues.Add(exception.Message);
        }
        diagnostics = issues;
        return issues.Count == 0;
    }
}

public static class MaterialCleanupService
{
    public static MaterialCleanupReport Analyze(
        XRMaterial material,
        ShaderAuthoringSchema schema)
    {
        HashSet<string> boundProperties = schema.PropertyLookup.Values
            .Where(static node => node.ManifestProperty is not null)
            .Select(static node => node.ManifestProperty!.Name)
            .ToHashSet(StringComparer.Ordinal);
        List<MaterialCleanupItem> items = [];
        foreach (ShaderVar parameter in material.Parameters)
        {
            if (boundProperties.Contains(parameter.Name))
                continue;
            items.Add(new(
                "UnboundValue",
                parameter.Name,
                $"Parameter '{parameter.Name}' has no schema binding.",
                false,
                false));
        }
        MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
        foreach ((string name, string value) in metadata.ImportedTags)
            items.Add(new(
                "ImportedTag",
                name,
                $"{name} = {value}",
                true,
                false));
        return new MaterialCleanupReport { Items = items };
    }

    public static bool Apply(
        XRMaterial material,
        MaterialCleanupReport report,
        bool confirmImportedMetadata,
        Action<MaterialCleanupItem> remove,
        Action<MaterialCleanupItem> restore,
        out MaterialAuthoringTransactionReport transactionReport)
    {
        if (report.RequiresImportedMetadataConfirmation && !confirmImportedMetadata)
        {
            transactionReport = new(
                false,
                0,
                ["Imported reconversion metadata removal was not confirmed."]);
            return false;
        }

        MaterialAuthoringTransaction transaction = new("Clean material authoring data");
        foreach (MaterialCleanupItem item in report.Items)
        {
            if (!item.Selected)
                continue;
            MaterialCleanupItem captured = item;
            transaction.AddStructural(
                material,
                item.Description,
                () => remove(captured),
                () => restore(captured),
                invalidatesVariant: true);
        }
        return transaction.TryExecute(out transactionReport);
    }
}
