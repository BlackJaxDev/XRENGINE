using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

internal sealed class OpenXrGraphicsSessionException(Result result, string message) : Exception(message)
{
    public Result Result { get; } = result;
}
