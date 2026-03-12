namespace XREngine.Rendering.Models.Materials
{
    [Flags]
    public enum EUniformRequirements
    {
        None = 0,
        /// <summary>
        /// InverseViewMatrix, ProjMatrix, CameraPosition, CameraForward, CameraUp, CameraNearZ, CameraFarZ, ScreenWidth, ScreenHeight, and ScreenOrigin will be provided.
        /// If the camera is perspective, the CameraFovY, CameraFovX, and CameraAspect will also be provided.
        /// Additional custom uniforms can also be provided by whatever parameters class the camera is using.
        /// </summary>
        Camera = 1,
        /// <summary>
        /// Arrays of point lights, spot lights, and directional lights will be provided.
        /// </summary>
        Lights = 2,
        /// <summary>
        /// However many seconds the shader program has been running will be provided.
        /// </summary>
        RenderTime = 4,
        /// <summary>
        /// ScreenWidth, ScreenHeight, and ScreenOrigin will be provided.
        /// </summary>
        ViewportDimensions = 8,
        /// <summary>
        /// The current mouse position relative to the viewport will be provided.
        /// Origin is bottom left.
        /// </summary>
        MousePosition = 16,

        //UserInterface = 32,

        //LightsAndCamera = Lights | Camera,
    }
}
