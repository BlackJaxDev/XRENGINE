namespace XREngine.Rendering;

/// <summary>
/// Registration seam for the optional leaf-owned shader compiler.
/// </summary>
internal static class RuntimeShaderCrossCompiler
{
    private static readonly object Sync = new();
    private static IRuntimeShaderCrossCompiler? _current;

    public static IRuntimeShaderCrossCompiler? Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Register(IRuntimeShaderCrossCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);

        lock (Sync)
        {
            if (_current is not null && !ReferenceEquals(_current, compiler))
                throw new InvalidOperationException("A shader cross-compiler is already registered.");

            _current = compiler;
        }

        return new Registration(compiler);
    }

    private sealed class Registration(IRuntimeShaderCrossCompiler compiler) : IDisposable
    {
        private IRuntimeShaderCrossCompiler? _compiler = compiler;

        public void Dispose()
        {
            IRuntimeShaderCrossCompiler? current = Interlocked.Exchange(ref _compiler, null);
            if (current is null)
                return;

            lock (Sync)
            {
                if (ReferenceEquals(_current, current))
                    _current = null;
            }
        }
    }
}
