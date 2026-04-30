namespace XREngine.Rendering.Models.Materials
{
    [Flags]
    public enum EGenShaderVarType
    {
        Bool    = 0x000001,
        Int     = 0x000002,
        Uint    = 0x000004,
        Float   = 0x000008,
        Double  = 0x000010,

        Vector2    = 0x000020,
        Vector3    = 0x000040,
        Vector4    = 0x000080,

        BVector2   = 0x000100,
        BVector3   = 0x000200,
        BVector4   = 0x000400,

        IVector2   = 0x000800,
        IVector3   = 0x001000,
        IVector4   = 0x002000,

        UVector2   = 0x004000,
        UVector3   = 0x008000,
        UVector4   = 0x010000,

        DVector2   = 0x020000,
        DVector3   = 0x040000,
        DVector4   = 0x080000,

        Mat3    = 0x100000,
        Mat4    = 0x200000,

        GenBool     = Bool   | BVector2 | BVector3 | BVector4,
        GenInt      = Int    | IVector2 | IVector3 | IVector4,
        GenUInt     = Uint   | UVector2 | UVector3 | UVector4,
        GenFloat    = Float  |  Vector2 |  Vector3 |  Vector4,
        GenDouble   = Double | DVector2 | DVector3 | DVector4,

        VecBool     = BVector2 | BVector3 | BVector4,
        VecInt      = IVector2 | IVector3 | IVector4,
        VecUint     = UVector2 | UVector3 | UVector4,
        VecFloat    =  Vector2 |  Vector3 |  Vector4,
        VecDouble   = DVector2 | DVector3 | DVector4,
    }
}
