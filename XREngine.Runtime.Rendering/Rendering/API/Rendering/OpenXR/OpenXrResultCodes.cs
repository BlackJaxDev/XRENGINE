namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Exact OpenXR result values used by the native-free graphics contract.</summary>
public static class OpenXrResultCodes
{
    public const int Success = 0;
    public const int ErrorFunctionUnsupported = -7;
    public const int ErrorHandleInvalid = -12;
    public const int ErrorInstanceLost = -13;
}
