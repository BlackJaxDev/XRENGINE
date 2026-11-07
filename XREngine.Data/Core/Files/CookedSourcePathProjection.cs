namespace XREngine.Core.Files;

/// <summary>
/// Projects the source paths that a cook writes into cooked bytes. Inside a cook scope, the asset header of each cooked
/// asset gets a portable <see cref="XRAsset.OriginalPath"/> (<c>game://</c> or <c>engine://</c>) and no
/// <see cref="XRAsset.FilePath"/>. The projection reads the asset and never changes it, so live editor assets that a cooked
/// copy references stay as they are. Outside a cook scope, each path is written as it is.
/// </summary>
/// <remarks>
/// The size pass and the write pass of a serializer must call the same projection, so that both see the same value. The
/// scope caches the result of each path, so the two passes always agree.
/// </remarks>
public static class CookedSourcePathProjection
{
    private static readonly AsyncLocal<CookedSourcePathScope?> CurrentScope = new();

    /// <summary>True while a cook scope is open on this execution context.</summary>
    public static bool IsActive => CurrentScope.Value is not null;

    /// <summary>
    /// Opens a cook scope on this execution context. Dispose the scope to close it, then call
    /// <see cref="CookedSourcePathScope.Failures"/> to find the paths that could not be made portable.
    /// </summary>
    /// <returns>The scope.</returns>
    public static CookedSourcePathScope Begin()
    {
        CookedSourcePathScope scope = new(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Closes a scope and makes its parent the current scope again.</summary>
    internal static void End(CookedSourcePathScope scope)
    {
        if (ReferenceEquals(CurrentScope.Value, scope))
            CurrentScope.Value = scope.Parent;
    }

    /// <summary>
    /// Gets the <see cref="XRAsset.OriginalPath"/> value to write for an asset. Inside a cook scope, an absolute path in an
    /// asset root becomes a portable reference. An absolute path outside every asset root is recorded as a failure of the
    /// scope and written as it is, because the cook fails after the write. Other values are written as they are.
    /// </summary>
    /// <param name="owner">The asset that owns the path. The failure record uses its name.</param>
    /// <param name="path">The path that the asset holds.</param>
    /// <returns>The path to write.</returns>
    public static string? ProjectOriginalPath(object? owner, string? path)
    {
        CookedSourcePathScope? scope = CurrentScope.Value;
        return scope is null ? path : scope.ProjectOriginalPath(owner, path);
    }

    /// <summary>
    /// Gets the <see cref="XRAsset.FilePath"/> value to write. Inside a cook scope it is always null: a file path of the cook
    /// machine has no meaning in a cooked build, and the runtime sets the file path again from the resolved source path.
    /// </summary>
    /// <param name="path">The path that the asset holds.</param>
    /// <returns>The path to write.</returns>
    public static string? ProjectFilePath(string? path)
        => CurrentScope.Value is null ? path : null;
}
