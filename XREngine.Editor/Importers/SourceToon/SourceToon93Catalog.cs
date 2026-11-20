namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Pinned identity of the source toon 9.3.64 catalog that the importer reads.
/// </summary>
public static class SourceToon93Catalog
{
    public const string VersionText = "9.3.64";
    public const string RepositoryCommit = "c5aaeeb3a67782b7e8a26e184d5e0a1970792294";
    public const string ShaderGuid = "9444ce77bf4418748b1e8591b9d97f85";
    public const string ShaderBlob = "4e3a68b3551e63e6b6c57625669d19e86f70ac8c";
    public const string ShaderSha256 = "7efb9176022291a041ecf332bf999f68ba33591d6f446e60757be83e968e61d8";
    public const string ThryEditorTree = "6437aeaec7b715e7fd000bfd0bdd3d6b0840c6db";
    public const string CatalogResourceName = "XREngine.Scene.Importers.SourceToon.Catalogs.source-toon-9.3.64.json";

    public static readonly SourceToonShaderVersion Version = new(9, 3, 64);

    /// <summary>
    /// Opens the generated machine-readable property, pass, annotation, and workflow catalog.
    /// </summary>
    public static Stream OpenCatalog()
        => typeof(SourceToon93Catalog).Assembly.GetManifestResourceStream(CatalogResourceName)
           ?? throw new InvalidOperationException($"Embedded source toon catalog '{CatalogResourceName}' is missing.");
}
