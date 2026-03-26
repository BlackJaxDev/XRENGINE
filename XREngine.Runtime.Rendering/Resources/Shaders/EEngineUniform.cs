using System.Collections.Frozen;

namespace XREngine.Rendering
{
    public enum EEngineUniform
    {
        UpdateDelta,

        /// <summary>
        /// Seconds since this material's shader program was first used. Per-material accumulator.
        /// </summary>
        RenderTime,
        /// <summary>
        /// Seconds since the engine started running (global wall-clock time).
        /// </summary>
        EngineTime,
        /// <summary>
        /// Render-frame delta time in seconds (time between the previous frame and the current one).
        /// </summary>
        DeltaTime,

        //Multiply together in the shader
        ModelMatrix,

        /// <summary>
        /// The inverse of the camera's world space transformation.
        /// </summary>
        ViewMatrix, //Desktop
        LeftEyeViewMatrix, //Stereo
        RightEyeViewMatrix, //Stereo
        /// <summary>
        /// The camera's normal world space transformation (called 'inverse view' because the non-inversed view matrix transforms the entire scene inversely to the camera).
        /// </summary>
        InverseViewMatrix, //Desktop
        LeftEyeInverseViewMatrix, //Stereo
        RightEyeInverseViewMatrix, //Stereo
        
        InverseProjMatrix, //Desktop
        LeftEyeInverseProjMatrix, //Stereo
        RightEyeInverseProjMatrix, //Stereo
        ProjMatrix, //Desktop
        LeftEyeProjMatrix, //VR
        RightEyeProjMatrix, //VR
        ViewProjectionMatrix, //Desktop
        LeftEyeViewProjectionMatrix, //Stereo
        RightEyeViewProjectionMatrix, //Stereo

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
        DepthMode,
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

    /// <summary>
    /// Extension methods for EEngineUniform to avoid expensive enum.ToString() calls.
    /// The string names are cached at startup for O(1) lookup.
    /// </summary>
    public static class EEngineUniformExtensions
    {
        private const string VertexUniformSuffix = "_VTX";

        private static readonly FrozenDictionary<EEngineUniform, string> _names = 
            Enum.GetValues<EEngineUniform>().ToFrozenDictionary(e => e, e => e.ToString());

        private static readonly FrozenDictionary<EEngineUniform, string> _namesWithVtxSuffix = 
            Enum.GetValues<EEngineUniform>().ToFrozenDictionary(e => e, e => e.ToString() + VertexUniformSuffix);

        /// <summary>
        /// Returns the cached string name of the enum value.
        /// Much faster than ToString() which uses reflection.
        /// </summary>
        public static string ToStringFast(this EEngineUniform value)
            => _names.TryGetValue(value, out var name) ? name : value.ToString();

        /// <summary>
        /// Returns the cached string name with "_VTX" suffix for vertex shader uniforms.
        /// Avoids string concatenation on every call.
        /// </summary>
        public static string ToVertexUniformName(this EEngineUniform value)
            => _namesWithVtxSuffix.TryGetValue(value, out var name) ? name : value.ToString() + VertexUniformSuffix;
    }
}