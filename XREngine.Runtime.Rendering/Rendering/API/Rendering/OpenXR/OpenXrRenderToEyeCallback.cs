namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Renders one OpenXR view into its current graphics image.</summary>
public delegate void OpenXrRenderToEyeCallback(uint textureHandle, uint viewIndex);
