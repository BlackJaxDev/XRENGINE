namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Owns the immutable texture state required by an OpenGL bindless handle.
/// </summary>
internal interface IGLBindlessTexture
{
    /// <summary>Applies a deferred parameter change by replacing the frozen texture identity.</summary>
    void PrepareForBindlessHandle();

    /// <summary>
    /// Returns whether this texture has completed every operation that can change native
    /// sampling parameters. ARB_bindless_texture freezes those parameters at handle creation.
    /// </summary>
    bool IsReadyForBindlessHandle();

    /// <summary>Records that obtaining <paramref name="handle"/> froze the texture parameters.</summary>
    void MarkBindlessHandleAcquired(ulong handle);
}
