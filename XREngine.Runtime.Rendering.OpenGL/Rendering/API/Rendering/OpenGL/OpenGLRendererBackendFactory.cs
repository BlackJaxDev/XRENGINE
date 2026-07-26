using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering;

/// <summary>
/// Statically linked OpenGL renderer factory. This class moves with the OpenGL leaf module
/// when backend assemblies are split.
/// </summary>
public sealed class OpenGLRendererBackendFactory : IRendererBackendFactory
{
    public IRuntimeRendererHost Create(in RendererBackendCreateContext context)
    {
        if (context.Window is not XRWindow window)
        {
            throw new ArgumentException(
                $"The built-in OpenGL backend requires a {nameof(XRWindow)} host, but received " +
                $"'{context.Window?.GetType().FullName ?? "null"}'.",
                nameof(context));
        }

        return new OpenGLRenderer(window, context.LinkRendererToWindow);
    }
}
