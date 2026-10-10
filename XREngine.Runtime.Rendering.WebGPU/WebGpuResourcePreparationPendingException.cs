using System.Runtime.CompilerServices;

namespace XREngine.Rendering.WebGPU;

/// <summary>Defers a scene while retaining the required resource owner for cold failure diagnostics.</summary>
internal sealed class WebGpuResourcePreparationPendingException(string message, object? owner = null,
    [CallerMemberName] string origin = "")
    : RenderResourcePreparationPendingException(message)
{
    internal object? Owner { get; } = owner;
    internal string Origin { get; } = origin;
}
