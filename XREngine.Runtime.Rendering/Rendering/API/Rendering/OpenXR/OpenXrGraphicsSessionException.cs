namespace XREngine.Rendering.API.Rendering.OpenXR;

internal sealed class OpenXrGraphicsSessionException(int result, string message) : Exception(message)
{
    public int Result { get; } = result;
}
