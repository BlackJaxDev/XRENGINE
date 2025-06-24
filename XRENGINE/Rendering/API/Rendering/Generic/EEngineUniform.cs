namespace XREngine.Rendering
{
    public enum EEngineUniform
    {
        UpdateDelta,

        //Multiply together in the shader
        ModelMatrix,

        /// <summary>
        /// The inverse of the camera's world space transformation.
        /// </summary>
        ViewMatrix, //Desktop
        /// <summary>
        /// The camera's normal world space transformation (called 'inverse view' because the non-inversed view matrix transforms the entire scene inversely to the camera).
        /// </summary>
        InverseViewMatrix, //Desktop
        LeftEyeInverseViewMatrix, //Stereo
        RightEyeInverseViewMatrix, //Stereo
        
        ProjMatrix, //Desktop
        LeftEyeProjMatrix, //VR
        RightEyeProjMatrix, //VR

        //Multiply together in the shader
        PrevModelMatrix,

        PrevViewMatrix, //Desktop
        PrevLeftEyeViewMatrix, //Stereo
        PrevRightEyeViewMatrix, //Stereo
        
        PrevProjMatrix, //Desktop
        PrevLeftEyeProjMatrix, //VR
        PrevRightEyeProjMatrix, //VR

        ScreenWidth,
        ScreenHeight,
        ScreenOrigin,

        CameraFovX,
        CameraFovY,
        CameraAspect,
        CameraNearZ,
        CameraFarZ,
        CameraPosition,
        //CameraForward,
        //CameraUp,
        //CameraRight,

        RootInvModelMatrix,
        CameraForward,
        CameraUp,
        CameraRight,

        BillboardMode,
        VRMode,

        UIWidth,
        UIHeight,
        UIX,
        UIY,
        UIXYWH,
    }
}