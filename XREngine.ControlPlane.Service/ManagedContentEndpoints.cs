using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using XREngine.Networking;

namespace XREngine.ControlPlane.Service;

/// <summary>Authenticated immutable content endpoints. Call after the service identity middleware.</summary>
internal static class ManagedContentEndpoints
{
    /// <summary>
    /// Maps the managed content endpoints to the specified web application.
    /// </summary>
    /// <param name="app">The web application to which the managed content endpoints will be mapped.</param>
    public static void MapManagedContent(this WebApplication app)
    {
        // Map the endpoint for retrieving the manifest of a specific package.
        app.MapGet("/v1/packages/{packageId}/{hash}/manifest", (string packageId, string hash, LocalPackageCatalog catalog) =>
        {
            WorldPackageManifest package = catalog.Load(packageId);
            return Matches(package, hash) 
                ? Results.Ok(package) 
                : Results.NotFound();
        });

        // Map the endpoint for retrieving a specific file from a package.
        app.MapGet("/v1/packages/{packageId}/{hash}/files/{index:int}", async (string packageId, string hash, int index, LocalPackageCatalog catalog, HttpContext context) =>
        {
            WorldPackageManifest package = catalog.Load(packageId);

            if (!Matches(package, hash) || 
                index < 0 || 
                index >= package.Files.Count)
                return Results.NotFound();

            WorldPackageFile file = package.Files[index];
            string root = Path.GetFullPath(package.RootPath);
            string path = Path.GetFullPath(Path.Combine(root, file.RelativePath));

            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            
            for (string? cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    return Results.NotFound();
            
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            try
            {
                if (stream.Length != file.Length || 
                    !Convert.ToHexString(await SHA256.HashDataAsync(stream, context.RequestAborted)).Equals(WorldAssetIdentity.NormalizeHash(file.Sha256), StringComparison.OrdinalIgnoreCase))
                {
                    await stream.DisposeAsync();
                    return Results.Conflict(new { code = "PackageUnavailable" });
                }

                stream.Position = 0;

                return Results.File(stream, "application/octet-stream", enableRangeProcessing: true, entityTag: new EntityTagHeaderValue($"\"{WorldAssetIdentity.NormalizeHash(file.Sha256)}\""));
            }
            catch { await stream.DisposeAsync(); throw; }
        });
    }

    /// <summary>
    /// Determines whether the specified package matches the given hash and passes manifest verification.
    /// </summary>
    /// <param name="package">The world package manifest to be checked.</param>
    /// <param name="hash">The hash to be compared against the package's manifest hash.</param>
    /// <returns><c>true</c> if the package matches the hash and passes manifest verification; otherwise, <c>false</c>.</returns>
    private static bool Matches(WorldPackageManifest package, string hash)
        => hash.Equals(WorldAssetIdentity.NormalizeHash(package.ManifestHash), StringComparison.Ordinal)
            && WorldPackageManifestBuilder.VerifyManifest(package).Success;
}
