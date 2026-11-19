using System;
using System.Threading;

namespace XREngine.Rendering.EditorHighlight.Contracts;

/// <summary>
/// Lets an editor install a highlight module without adding an editor reference
/// to the runtime render pipeline.
/// </summary>
public static class EditorHighlightModuleRegistry
{
    private static Func<IEditorHighlightModule>? _factory;

    /// <summary>Installs one editor factory for the process lifetime.</summary>
    public static IDisposable Install(Func<IEditorHighlightModule> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Interlocked.CompareExchange(ref _factory, factory, null) is not null)
            throw new InvalidOperationException("An editor highlight module factory is already installed.");

        return new FactoryLease(factory);
    }

    /// <summary>Creates the editor module, or returns null when no editor installed a factory.</summary>
    public static IEditorHighlightModule? Create()
        => Volatile.Read(ref _factory)?.Invoke();

    private sealed class FactoryLease(Func<IEditorHighlightModule> factory) : IDisposable
    {
        private Func<IEditorHighlightModule>? _factory = factory;

        public void Dispose()
        {
            Func<IEditorHighlightModule>? registered = Interlocked.Exchange(ref _factory, null);
            if (registered is not null)
                Interlocked.CompareExchange(ref EditorHighlightModuleRegistry._factory, null, registered);
        }
    }
}
