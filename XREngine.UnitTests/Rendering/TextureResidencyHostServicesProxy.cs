using System.Reflection;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// A rendering host for texture residency tests. It selects the OpenGL backend, the highest texture quality,
/// and a VRAM budget that the test sets. It sends all other members to the inner host.
/// </summary>
public class TextureResidencyHostServicesProxy : DispatchProxy
{
    /// <summary>The host that receives the members that this proxy does not replace.</summary>
    public IRuntimeRenderingHostServices Inner { get; set; } = null!;

    /// <summary>The tracked VRAM budget. <see cref="long.MaxValue"/> means no budget.</summary>
    public long TrackedVramBudgetBytes { get; set; } = long.MaxValue;

    /// <summary>The tracked VRAM use.</summary>
    public long TrackedVramBytes { get; set; }

    /// <summary>Creates the proxy.</summary>
    public static IRuntimeRenderingHostServices Create(
        IRuntimeRenderingHostServices inner,
        out TextureResidencyHostServicesProxy state)
    {
        IRuntimeRenderingHostServices proxy = Create<IRuntimeRenderingHostServices, TextureResidencyHostServicesProxy>();
        state = (TextureResidencyHostServicesProxy)(object)proxy;
        state.Inner = inner;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;

        return targetMethod.Name switch
        {
            $"get_{nameof(IRuntimeRenderingHostServices.CurrentRenderBackend)}" => RuntimeGraphicsApiKind.OpenGL,
            $"get_{nameof(IRuntimeRenderingHostServices.TrackedVramBudgetBytes)}" => TrackedVramBudgetBytes,
            $"get_{nameof(IRuntimeRenderingHostServices.TrackedVramBytes)}" => TrackedVramBytes,
            $"get_{nameof(IRuntimeRenderingHostServices.TextureQuality)}" => EEngineQuality.Highest,
            _ => targetMethod.Invoke(Inner, args),
        };
    }
}
