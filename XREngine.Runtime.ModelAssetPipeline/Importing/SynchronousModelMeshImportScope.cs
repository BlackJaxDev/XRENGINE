namespace XREngine.Rendering.Models;

/// <summary>
/// Temporarily forces synchronous mesh reconstruction while preserving the
/// caller's nullable preference for later imports.
/// </summary>
internal sealed class SynchronousModelMeshImportScope : IDisposable
{
    private ModelImportOptions? _options;
    private readonly bool? _requestedValue;

    private SynchronousModelMeshImportScope(ModelImportOptions options)
    {
        _options = options;
        _requestedValue = options.ProcessMeshesAsynchronously;
        options.ProcessMeshesAsynchronously = false;
    }

    public static IDisposable Enter(ModelImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SynchronousModelMeshImportScope(options);
    }

    public void Dispose()
    {
        ModelImportOptions? options = Interlocked.Exchange(ref _options, null);
        if (options is not null)
            options.ProcessMeshesAsynchronously = _requestedValue;
    }
}
