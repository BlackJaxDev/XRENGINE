namespace XREngine.Rendering
{
    /// <summary>
    /// Determines how vertices should rotate and scale in relation to the camera.
    /// </summary>
    [Flags]
    public enum ECameraTransformFlags
    {
        /// <summary>
        /// No billboarding, all vertices are static.
        /// </summary>
        None = 0,

        /// <summary>
        /// If set, the X axis will rotate to face the camera position.
        /// If not set, the X axis will rotate to face the camera screen plane.
        /// </summary>
        PointRotationX = 0x001,
        /// <summary>
        /// If set, the Y axis will rotate to face the camera position.
        /// If not set, the Y axis will rotate to face the camera screen plane.
        /// </summary>
        PointRotationY = 0x002,
        /// <summary>
        /// If set, the Z axis will rotate to face the camera position.
        /// If not set, the Z axis will rotate to face the camera screen plane.
        /// </summary>
        PointRotationZ = 0x004,

        /// <summary>
        /// If set, the X axis will scale according to the distance to the camera position.
        /// If not set, the X axis will scale according to the distance to the camera screen plane.
        /// </summary>
        PointScaleX = 0x008,
        /// <summary>
        /// If set, the Y axis will scale according to the distance to the camera position.
        /// If not set, the Y axis will scale according to the distance to the camera screen plane.
        /// </summary>
        PointScaleY = 0x010,
        /// <summary>
        /// If set, the Z axis will scale according to the distance to the camera position.
        /// If not set, the Z axis will scale according to the distance to the camera screen plane.
        /// </summary>
        PointScaleZ = 0x020,

        /// <summary>
        /// If set, the X axis will rotate to face the camera.
        /// </summary>
        RotateX = 0x040,
        /// <summary>
        /// If set, the Y axis will rotate to face the camera.
        /// </summary>
        RotateY = 0x080,
        /// <summary>
        /// If set, the Z axis will rotate to face the camera.
        /// </summary>
        RotateZ = 0x100,

        /// <summary>
        /// If set, the X axis will scale according to camera.
        /// </summary>
        ScaleX = 0x200,
        /// <summary>
        /// If set, the Y axis will rotate to face the camera.
        /// </summary>
        ScaleY = 0x400,
        /// <summary>
        /// If set, the Z axis will rotate to face the camera.
        /// </summary>
        ScaleZ = 0x800,
        /// <summary>
        /// If set, the X axis translation will not move with the camera.
        /// </summary>
        ConstrainTranslationX = 0x1000,
        /// <summary>
        /// If set, the Y axis translation will not move with the camera.
        /// </summary>
        ConstrainTranslationY = 0x2000,
        /// <summary>
        /// If set, the Z axis translation will not move with the camera.
        /// </summary>
        ConstrainTranslationZ = 0x4000,

        /// <summary>
        /// If set, the position on all axes will not move with the camera.
        /// </summary>
        ConstrainTranslations = ConstrainTranslationX | ConstrainTranslationY | ConstrainTranslationZ,
    }
}