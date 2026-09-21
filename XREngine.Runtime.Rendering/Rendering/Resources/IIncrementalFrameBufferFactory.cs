namespace XREngine.Rendering.Resources;

/// <summary>
/// Materializes one generation-owned framebuffer across bounded owner-thread steps.
/// </summary>
public interface IIncrementalFrameBufferFactory : IDisposable
{
    /// <summary>
    /// Advances one indivisible preparation step.
    /// </summary>
    /// <param name="frameBuffer">The completed framebuffer when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the framebuffer is complete and ownership can transfer to the generation.</returns>
    bool MoveNext(out XRFrameBuffer? frameBuffer);
}