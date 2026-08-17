namespace XREngine.Rendering;

internal sealed partial class ImportedTextureStreamingManager
{
    private sealed class ImportedTextureStreamingScope(ImportedTextureStreamingManager owner) : IDisposable
    {
        private readonly ImportedTextureStreamingManager _owner = owner;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            int remaining = Interlocked.Decrement(ref _owner._activeImportedModelImports);
            if (remaining < 0)
                Interlocked.Exchange(ref _owner._activeImportedModelImports, 0);
        }
    }
}
